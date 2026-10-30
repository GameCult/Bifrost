// The parts of the Matrix mouth that sit on CultLib: the chat_* wire documents, the
// CultCache cursor and brake stores, and the CultNet RUDP endpoint. Needs the built
// CultLib TypeScript packages (CULTLIB_ROOT, else ../CultLib), like the other Bifrost tests.
import test from "node:test";
import assert from "node:assert/strict";
import dgram from "node:dgram";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import * as docs from "../tools/persona-chat-documents.mjs";
import { cultcacheState, connectionIdFor, createBrakeReader, loadRuntime, pageOf, startChatEndpoint } from "../tools/persona-matrix-mouth.mjs";

const root = resolve(import.meta.dirname, "..");
const runtime = loadRuntime(resolve(process.env.CULTLIB_ROOT ?? resolve(root, "..", "CultLib")));
const { msgpack } = runtime;
const fixtureDir = resolve(import.meta.dirname, "fixtures", "persona-chat-v1");

const delivery = {
  seq: 7, roomAlias: "#operator:gamecult.org", roomId: "!abc:gamecult.org", eventId: "$e1", sender: "@meta:gamecult.org", senderDisplay: "Meta",
  text: "hello eureka", atUnixMs: 1761700000000,
  context: [{ sender: "@eureka:gamecult.org", text: "earlier", atUnixMs: 1761699999000 }],
  mentions: ["@eureka:gamecult.org", "@nova:gamecult.org"], isDirect: true,
};
const samples = {
  [docs.kinds.pendingRequest]: { tuple: docs.pendingRequestTuple({ persona: "eureka", afterSeq: 3 }), parse: docs.parsePendingRequest },
  [docs.kinds.pending]: { tuple: docs.pendingTuple({ persona: "eureka", deliveries: [delivery] }), parse: docs.parsePending },
  [docs.kinds.ack]: { tuple: docs.ackTuple({ persona: "eureka", throughSeq: 7 }), parse: docs.parseAck },
  [docs.kinds.sendRequest]: { tuple: docs.sendRequestTuple({ requestId: "req-1", persona: "eureka", roomAlias: "#operator:gamecult.org", text: "hi" }), parse: docs.parseSendRequest },
  [docs.kinds.sendReceipt]: { tuple: docs.sendReceiptTuple({ requestId: "req-1", eventId: "$sent" }), parse: docs.parseSendReceipt },
  [docs.kinds.refusal]: { tuple: docs.refusalTuple({ persona: "ghost", refusal: "UnknownPersona" }), parse: docs.parseRefusal },
};

for (const [kind, { tuple, parse }] of Object.entries(samples)) {
  test(`golden fixture: ${kind} is byte-stable and parses back`, async () => {
    const bytes = Buffer.from(msgpack.encode(tuple));
    const file = resolve(fixtureDir, `${kind}.msgpack`);
    if (process.env.UPDATE_FIXTURES === "1") { await mkdir(fixtureDir, { recursive: true }); await writeFile(file, bytes); }
    assert.deepEqual(bytes, await readFile(file), "wire bytes changed: Huginn decodes these fixtures");
    assert.deepEqual(parse(msgpack.decode(bytes)), parse(tuple));
  });
}

test("the chat documents refuse malformed tuples", () => {
  assert.throws(() => docs.parseAck(["eureka"]));
  assert.throws(() => docs.parseAck(["eureka", -1]));
  assert.throws(() => docs.parsePendingRequest(["", 0]));
  assert.throws(() => docs.parseSendRequest(["r", "eureka", "#a:b", "x".repeat(4001)]));
  assert.throws(() => docs.parseSendRequest(["r", "eureka", "#a:b", 5]));
  const wire = docs.deliveryTuple(delivery);
  assert.equal(wire.length, 11);
  assert.deepEqual(docs.parseDelivery(wire), delivery);
  assert.throws(() => docs.parseDelivery(wire.slice(0, 9)), /11-field/, "the nine-field delivery is gone: one shape");
  assert.throws(() => docs.parseDelivery([...wire.slice(0, 10), "yes"]), /boolean/);
  assert.throws(() => docs.parseDelivery([...wire.slice(0, 9), [""], true]));
  assert.throws(() => docs.refusalTuple({ persona: "p", refusal: "Invented" }));
  assert.throws(() => docs.parseSendReceipt(["r", "$e", "Brake"]), /exactly one/);
  assert.throws(() => docs.parseSendReceipt(["r", null, null]), /exactly one/);
  assert.throws(() => docs.sendReceiptTuple({ requestId: "r", refusal: "Invented" }));
  assert.throws(() => docs.parseSendReceipt(["r", null, "Invented"]), /unknown refusal/);
  assert.deepEqual(docs.parseSendReceipt(["r", null, "Brake"]), { requestId: "r", eventId: null, refusal: "Brake" });
});

test("the cursor is a CultCache file that survives a restart", async () => {
  const path = resolve(await mkdtemp(resolve(tmpdir(), "mouth-cursor-")), "cursor.cc");
  assert.equal(await cultcacheState({ runtime, path }).load(), null);
  await cultcacheState({ runtime, path }).save({ nextBatch: "s72_9", seq: 12 });
  const loaded = await cultcacheState({ runtime, path }).load();
  assert.deepEqual([loaded.nextBatch, loaded.seq], ["s72_9", 12]);
});

async function writeControl(path, brakeTuple) {
  const store = new runtime.SingleFileMessagePackBackingStore(path);
  await store.push({ key: "brake", type: "eureka.control.brake.v1", payload: msgpack.encode(brakeTuple), storedAt: new Date().toISOString() });
}

test("the brake reader reads a real CultCache control store", async () => {
  const dir = await mkdtemp(resolve(tmpdir(), "mouth-control-"));
  const reader = path => createBrakeReader({ controlPath: path, readStore: runtime.readSingleFileStore, decode: msgpack.decode });
  assert.equal(await reader(resolve(dir, "missing.cc"))(), false);
  await writeControl(resolve(dir, "held.cc"), [false, "2026-10-29T00:00:00Z", "op"]);
  assert.equal(await reader(resolve(dir, "held.cc"))(), false);
  await writeControl(resolve(dir, "released.cc"), [true, "2026-10-29T00:00:00Z", "op"]);
  assert.equal(await reader(resolve(dir, "released.cc"))(), true);
  await writeFile(resolve(dir, "garbage.cc"), "not a store");
  assert.equal(await reader(resolve(dir, "garbage.cc"))(), false);
});

test("the RUDP endpoint serves pending, ack and send over CultNet, and answers a held brake with a Brake refusal", async () => {
  const calls = { acks: [], sends: [] };
  const mouth = {
    pendingAfter: after => (after < 7 ? [delivery] : []),
    ack: async through => { calls.acks.push(through); },
    send: async request => { calls.sends.push(request); return request.text === "muted" ? { requestId: request.requestId, eventId: null, refusal: "Brake" } : { requestId: request.requestId, eventId: "$sent", refusal: null }; },
  };
  const key = "connection-key-for-test";
  const rejected = [];
  const server = await startChatEndpoint({ mouths: new Map([["eureka", mouth]]), runtime, port: 0, connectionKey: key, runtimeId: "mouth-test", log: line => rejected.push(line) });
  const client = dgram.createSocket("udp4");
  await new Promise(ok => client.bind(0, "127.0.0.1", ok));
  const connection = new runtime.cultnet.CultNetRudpSocketTransportConnection({
    runtimeId: "huginn-test", socket: client, mode: "client", remoteHost: "127.0.0.1", remotePort: server.address.port,
    connectionId: connectionIdFor(key), initialSequence: 1, resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200,
  });
  const replies = [];
  const waiting = [];
  connection.on("frame", value => {
    const message = runtime.cultnet.parseCultNetMessage(msgpack.decode(value.payload), "cultnet.schema.v0");
    replies.push(message.document);
    waiting.splice(0).forEach(w => w());
  });
  const nextReply = () => new Promise((ok, fail) => { const t = setTimeout(() => fail(new Error(`no reply from the mouth; it logged: ${rejected.join(" | ")}`)), 5000); waiting.push(() => { clearTimeout(t); ok(replies.at(-1)); }); });
  const put = (kind, recordKey, tuple) => connection.send("schema", msgpack.encode(runtime.cultnet.encodeCultNetMessageForWire(
    docs.envelope({ kind, recordKey, payload: msgpack.encode(tuple), sourceRuntimeId: "huginn-test", sourceAgentId: "eureka", sourceRole: "persona-organ" }), "cultnet.schema.v0")));
  try {
    connection.connect();
    for (let i = 0; i < 100 && !connection.connected; i++) await new Promise(r => setTimeout(r, 20));
    assert.ok(connection.connected, "the RUDP session opens with the credential-derived connection id");
    let reply = nextReply();
    put(docs.kinds.pendingRequest, "eureka", docs.pendingRequestTuple({ persona: "eureka", afterSeq: 0 }));
    let document = await reply;
    assert.equal(document.schemaId, docs.kinds.pending);
    const pending = docs.parsePending(msgpack.decode(document.payload));
    assert.deepEqual(pending.deliveries.map(d => d.eventId), ["$e1"]);
    assert.deepEqual(pending.deliveries[0], delivery, "sender, mentions and the DM flag cross the wire exactly");

    put(docs.kinds.ack, "eureka:7", docs.ackTuple({ persona: "eureka", throughSeq: 7 }));
    reply = nextReply();
    put(docs.kinds.sendRequest, "req-2", docs.sendRequestTuple({ requestId: "req-2", persona: "eureka", roomAlias: "#operator:gamecult.org", text: "muted" }));
    document = await reply;
    assert.equal(document.schemaId, docs.kinds.sendReceipt);
    assert.deepEqual(docs.parseSendReceipt(msgpack.decode(document.payload)), { requestId: "req-2", eventId: null, refusal: "Brake" });
    reply = nextReply();
    put(docs.kinds.ack, "other:9", docs.ackTuple({ persona: "other", throughSeq: 9 }));
    document = await reply;
    assert.deepEqual([document.schemaId, docs.parseRefusal(msgpack.decode(document.payload))], [docs.kinds.refusal, { persona: "other", refusal: "UnknownPersona" }], "an ack for an unlisted Persona is refused, typed, and not applied");
    assert.deepEqual(calls.acks, [7]);

    reply = nextReply();
    put(docs.kinds.pendingRequest, "other", docs.pendingRequestTuple({ persona: "other", afterSeq: 0 }));
    document = await reply;
    assert.deepEqual([document.schemaId, docs.parseRefusal(msgpack.decode(document.payload)).refusal], [docs.kinds.refusal, "UnknownPersona"], "unknown_persona_refused");

    reply = nextReply();
    put(docs.kinds.sendRequest, "req-9", docs.sendRequestTuple({ requestId: "req-9", persona: "other", roomAlias: "#operator:gamecult.org", text: "speak" }));
    document = await reply;
    assert.deepEqual(docs.parseSendReceipt(msgpack.decode(document.payload)), { requestId: "req-9", eventId: null, refusal: "UnknownPersona" });
    assert.equal(calls.sends.length, 1, "nothing reached a mouth for the unlisted Persona");
  } finally {
    server.close();
    connection.close();
    try { client.close(); } catch { /* the connection already closed its socket */ }
  }
});

test("the RUDP endpoint refuses to listen beyond loopback", async () => {
  await assert.rejects(startChatEndpoint({ mouths: new Map(), runtime, port: 0, host: "0.0.0.0", connectionKey: "k", runtimeId: "m" }), /loopback/);
});

test("loopback means exactly an IPv4 127.x.y.z address", async () => {
  const refuses = host => assert.rejects(startChatEndpoint({ mouths: new Map(), runtime, port: 0, host, connectionKey: "k", runtimeId: "m" }), /loopback/);
  for (const host of ["9127.0.0.1", "127.0.0.1x", "10.127.0.0.1", "127.0.0", ""]) await refuses(host);
  const ok = await startChatEndpoint({ mouths: new Map(), runtime, port: 0, host: "127.10.20.30", connectionKey: "k", runtimeId: "m" }).catch(e => e);
  assert.ok(!/loopback/.test(ok?.message ?? ""), "a multi-digit loopback address is accepted by the check");
  ok.close?.();
});

test("one process serves N Personas from systemd credentials: each token only in its own requests, nothing joined while braked, and no token on stderr", async () => {
  const dir = await mkdtemp(resolve(tmpdir(), "mouth-main-"));
  const creds = resolve(dir, "creds"), control = resolve(dir, "control"), stateDir = resolve(dir, "state");
  const tokens = { eureka: "syt_AAA_eureka_token", nova: "syt_BBB_nova_token" };
  await mkdir(creds, { recursive: true });
  for (const [persona, token] of Object.entries(tokens)) await writeFile(resolve(creds, `matrix-token-${persona}`), `${token}\n`);
  await writeFile(resolve(creds, "chat-key"), "chat-key-credential\n");
  await mkdir(resolve(control, "eureka"), { recursive: true });
  await mkdir(resolve(control, "nova"), { recursive: true });
  await writeControl(resolve(control, "eureka", "control.cc"), [true, "2026-10-29T00:00:00Z", "op"]);
  await writeControl(resolve(control, "nova", "control.cc"), [false, "2026-10-29T00:00:00Z", "op"]); // nova is braked

  const seen = [];
  const joinedBy = new Map();
  const hs = createServer((req, res) => {
    const auth = req.headers.authorization ?? "";
    const path = decodeURIComponent(new URL(req.url, "http://x").pathname).replace("/_matrix/client/v3", "");
    seen.push({ auth, method: req.method, path });
    const reply = body => { res.writeHead(200, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
    if (path === "/account/whoami") return reply({ user_id: auth.includes(tokens.eureka) ? "@eureka:t" : "@nova:t" });
    if (path === "/joined_rooms") return reply({ joined_rooms: [...(joinedBy.get(auth) ?? [])] });
    if (path.startsWith("/join/")) { joinedBy.set(auth, (joinedBy.get(auth) ?? new Set()).add(path.slice(6))); return reply({ room_id: path.slice(6) }); }
    if (path.startsWith("/directory/room/")) return reply({ room_id: "!swarm:t" });
    if (path === "/sync") return reply({ next_batch: "b0" });
    res.writeHead(404); res.end("{}");
  });
  await new Promise(ok => hs.listen(0, "127.0.0.1", ok));
  const probe = dgram.createSocket("udp4");
  await new Promise(ok => probe.bind(0, "127.0.0.1", ok));
  const port = probe.address().port;
  probe.close();
  const child = spawn(process.execPath, [resolve(root, "tools", "persona-matrix-mouth.mjs")], {
    env: {
      PATH: process.env.PATH, CULTLIB_ROOT: resolve(process.env.CULTLIB_ROOT ?? resolve(root, "..", "CultLib")), PERSONAS: "eureka,nova", CREDENTIALS_DIRECTORY: creds, CONTROL_ROOT: control,
      STATE_DIRECTORY: stateDir, MATRIX_HOMESERVER: `http://127.0.0.1:${hs.address().port}`, MATRIX_ROOMS: "#swarm:t", MATRIX_SERVER_NAME: "t", CHAT_RUDP_LISTEN: `127.0.0.1:${port}`,
    },
  });
  let stderr = "";
  child.stderr.on("data", c => (stderr += c));
  try {
    for (let i = 0; i < 100 && !(seen.some(r => r.path === "/sync" && r.auth.includes(tokens.nova)) && seen.some(r => r.path === "/sync" && r.auth.includes(tokens.eureka))); i++) await new Promise(r => setTimeout(r, 50));
    const by = persona => seen.filter(r => r.auth === `Bearer ${tokens[persona]}`);
    assert.ok(by("eureka").some(r => r.path === "/sync") && by("nova").some(r => r.path === "/sync"), `both accounts sync from one process; stderr: ${stderr}`);
    assert.equal(by("eureka").length + by("nova").length, seen.length, "every request carries exactly one Persona's token");
    assert.deepEqual(by("eureka").filter(r => r.method === "POST").map(r => r.path), ["/join/!swarm:t"], "the released Persona joins");
    assert.deepEqual(by("nova").filter(r => r.method === "POST"), [], "the braked Persona joins nothing");
  } finally {
    child.kill();
    await new Promise(ok => hs.close(ok));
  }
  assert.ok(!Object.values(tokens).some(t => stderr.includes(t)) && !stderr.includes("chat-key-credential"), "no secret on stderr");
});

test("a reply is paged to the byte budget, oldest first, and one delivery that will not encode is skipped and logged without silencing the rest", () => {
  const mk = (n, body) => ({ ...delivery, seq: n, eventId: `$e${n}`, text: body });
  const logs = [];
  const page = pageOf("eureka", [mk(1, "ok"), { ...mk(2, "bad"), sender: 123 }, mk(3, "fine")], msgpack, l => logs.push(l));
  assert.deepEqual(page[1].map(t => t[0]), [1, 3]);
  assert.equal(logs.length, 1);
  assert.ok(logs[0].includes("delivery 2 skipped"));

  const bodies = Array.from({ length: 200 }, (_, i) => mk(i + 1, "x".repeat(3900)));
  const paged = pageOf("eureka", bodies, msgpack);
  const size = msgpack.encode(paged).length;
  assert.ok(size <= docs.limits.replyBytes + 1000, `page of ${size} bytes is over the budget`);
  assert.ok(paged[1].length > 1 && paged[1].length < 200);
  assert.deepEqual(paged[1].map(t => t[0]), paged[1].map((_, i) => i + 1), "the page is the oldest deliveries");
  assert.equal(pageOf("eureka", [mk(1, "x".repeat(4000))], msgpack)[1].length, 1, "a single delivery is always served");
});

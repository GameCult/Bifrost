// The parts of the Matrix mouth that sit on CultLib: the chat_* wire documents, the
// CultCache cursor and brake stores, and the CultNet RUDP endpoint. Needs the built
// CultLib TypeScript packages (VOIDBOT_CULTLIB_ROOT, else ../CultLib), like the other Bifrost tests. CultLib's dist/ is gitignored:
// run `npm ci && npm run build:ts` there first. Since CultLib 286a9af the packages are @gamecult/cultcache-ts,
// @gamecult/cultnet-ts and @gamecult/cultmesh-ts; the directories under packages/ keep their short names.
import test from "node:test";
import { cultLibRoot } from "./cultlib-root.mjs";
import assert from "node:assert/strict";
import dgram from "node:dgram";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import * as docs from "../tools/persona-chat-documents.mjs";
import { createMouth, cultcacheState, connectionIdFor, createBrakeReader, loadRuntime, pageOf, startChatEndpoint } from "../tools/persona-matrix-mouth.mjs";

const root = resolve(import.meta.dirname, "..");
const runtime = loadRuntime(cultLibRoot);
const { msgpack } = runtime;
const fixtureDir = resolve(import.meta.dirname, "fixtures", "persona-chat-v1");

const tailLine = { roomAlias: "#operator:gamecult.org", eventId: "$t1", atUnixMs: 1_700_000_000_000, sender: "@meta:gamecult.org", form: "message", basis: null, text: "earlier" };
const delivery = {
  seq: 7, roomAlias: "#operator:gamecult.org", roomId: "!abc:gamecult.org", eventId: "$e1", sender: "@meta:gamecult.org", senderDisplay: "Meta",
  text: "hello eureka", atUnixMs: 1761700000000,
  context: [{ sender: "@eureka:gamecult.org", text: "earlier", atUnixMs: 1761699999000 }],
  mentions: ["@eureka:gamecult.org", "@nova:gamecult.org"], isDirect: true, image: null,
};
const image = { path: `${"cd".repeat(32)}/${"ab".repeat(32)}.png`, mime: "image/png", size: 2048, sha256: "ab".repeat(32) };
const samples = {
  [docs.kinds.pendingRequest]: { tuple: docs.pendingRequestTuple({ persona: "eureka", afterSeq: 3 }), parse: docs.parsePendingRequest },
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
  assert.equal(wire.length, 12);
  assert.deepEqual(docs.parseDelivery(wire), delivery);
  assert.throws(() => docs.parseDelivery(wire.slice(0, 11)), /12-field/, "the eleven-field delivery is gone: one shape");
  assert.throws(() => docs.parseDelivery([...wire.slice(0, 10), "yes", null]), /boolean/);
  assert.throws(() => docs.parseDelivery([...wire.slice(0, 9), [""], true, null]));
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

test("two clients served at once each get their own answer on their own session", async () => {
  const mouth = {
    pendingAfter: () => [],
    tail: async () => ({ lines: [], gaps: [], rooms: [] }),
    ack: async () => {},
    // Slow enough that the two requests are in flight together.
    send: async request => { await new Promise(r => setTimeout(r, 150)); return { requestId: request.requestId, eventId: `$${request.requestId}`, refusal: null }; },
  };
  const key = "connection-key-for-concurrency";
  const server = await startChatEndpoint({ mouths: new Map([["eureka", mouth]]), runtime, port: 0, connectionKey: key, runtimeId: "mouth-test" });
  const dial = async name => {
    const socket = dgram.createSocket("udp4");
    await new Promise(ok => socket.bind(0, "127.0.0.1", ok));
    const connection = new runtime.cultnet.CultNetRudpSocketTransportConnection({
      runtimeId: name, socket, mode: "client", remoteHost: "127.0.0.1", remotePort: server.address.port,
      connectionId: connectionIdFor(key), resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200,
    });
    const receipts = [];
    connection.on("frame", value => {
      const message = runtime.cultnet.parseCultNetMessage(msgpack.decode(value.payload), "cultnet.schema.v0");
      receipts.push(docs.parseSendReceipt(msgpack.decode(message.document.payload)));
    });
    connection.connect();
    for (let i = 0; i < 100 && !connection.connected; i++) await new Promise(r => setTimeout(r, 20));
    assert.ok(connection.connected, `${name} opened its session`);
    const send = requestId => connection.send("schema", msgpack.encode(runtime.cultnet.encodeCultNetMessageForWire(
      docs.envelope({ kind: docs.kinds.sendRequest, recordKey: requestId, payload: msgpack.encode(docs.sendRequestTuple({ requestId, persona: "eureka", roomAlias: "#operator:gamecult.org", text: name })), sourceRuntimeId: name, sourceAgentId: "eureka", sourceRole: "persona-organ" }), "cultnet.schema.v0")));
    return { connection, receipts, send };
  };
  const first = await dial("mind-persona-one");
  const second = await dial("mind-heartbeat");
  try {
    first.send("req-first");
    second.send("req-second");
    for (let i = 0; i < 250 && (first.receipts.length < 1 || second.receipts.length < 1); i++) await new Promise(r => setTimeout(r, 20));
    assert.deepEqual(first.receipts, [{ requestId: "req-first", eventId: "$req-first", refusal: null }]);
    assert.deepEqual(second.receipts, [{ requestId: "req-second", eventId: "$req-second", refusal: null }]);
    assert.ok(first.connection.connected && second.connection.connected, "neither session took the other's");
  } finally {
    server.close();
    first.connection.close();
    second.connection.close();
  }
});

test("the RUDP endpoint serves pending, ack and send over CultNet, and answers a held brake with a Brake refusal", async () => {
  const calls = { acks: [], sends: [], reads: [] };
  const mouth = {
    pendingAfter: after => (after < 7 ? [delivery] : []),
    tail: async () => { if (calls.tailFails) throw new Error("room read failed"); return { lines: [tailLine], gaps: ["#operator:gamecult.org"], rooms: ["#operator:gamecult.org", "#swarm:gamecult.org"] }; },
    speakableRooms: () => ["#operator:gamecult.org", "#swarm:gamecult.org"],
    ack: async through => { calls.acks.push(through); },
    read: async request => { calls.reads.push(request); return null; },
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
    assert.deepEqual(Buffer.from(document.payload), await readFile(resolve(root, "tests", "fixtures", "persona-chat-v3", "gamecult.persona.chat_pending.v2.no-image.msgpack")), "a v1 request is answered with the two-field v2, byte for byte the golden, whatever the tail gaps");

    reply = nextReply();
    put(docs.kinds.pendingRequestV3, "eureka", docs.pendingRequestTuple({ persona: "eureka", afterSeq: 0 }));
    document = await reply;
    assert.equal(document.schemaId, docs.kinds.pendingV3, "the kind asked by names the answer");
    const v3 = docs.parsePendingV3(msgpack.decode(document.payload));
    assert.deepEqual(v3.deliveries[0], { ...delivery, basis: null, form: "message" });
    assert.deepEqual(v3.tail, [tailLine], "the v3 answer carries the room tail");
    assert.deepEqual(v3.tailGaps, ["#operator:gamecult.org"], "and the rooms whose tail is incomplete");

    calls.tailFails = true;
    reply = nextReply();
    put(docs.kinds.pendingRequestV3, "eureka", docs.pendingRequestTuple({ persona: "eureka", afterSeq: 0 }));
    document = await reply;
    const degraded = docs.parsePendingV3(msgpack.decode(document.payload));
    assert.equal(degraded.deliveries.length, 1, "a tail that throws does not silence the deliveries");
    assert.deepEqual(degraded.tail, []);
    assert.deepEqual(degraded.tailGaps, ["#operator:gamecult.org", "#swarm:gamecult.org"], "a tail that throws leaves every speakable room a gap, not quiet");
    calls.tailFails = false;

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
    put(docs.kinds.read, "other:#operator:gamecult.org", docs.readTuple({ persona: "other", roomAlias: "#operator:gamecult.org", eventId: "$e1", atUnixMs: 5 }));
    document = await reply;
    assert.deepEqual([document.schemaId, docs.parseRefusal(msgpack.decode(document.payload))], [docs.kinds.refusal, { persona: "other", refusal: "UnknownPersona" }], "a read report for an unlisted Persona is refused, typed, and not applied");
    assert.deepEqual(calls.reads, []);

    put(docs.kinds.read, "eureka:#operator:gamecult.org", docs.readTuple({ persona: "eureka", roomAlias: "#operator:gamecult.org", eventId: "$e1", atUnixMs: 5 }));
    for (let i = 0; i < 100 && calls.reads.length < 1; i++) await new Promise(r => setTimeout(r, 20));
    assert.deepEqual(calls.reads, [{ persona: "eureka", roomAlias: "#operator:gamecult.org", eventId: "$e1", atUnixMs: 5 }], "her own report reaches her mouth with its persona");

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
      PATH: process.env.PATH, CULTLIB_ROOT: cultLibRoot, PERSONAS: "eureka,nova", CREDENTIALS_DIRECTORY: creds, CONTROL_ROOT: control,
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

// The pending answer v2 (ChatDelivery v3): the bytes Huginn pins in its crates/mind-body/tests/fixtures/persona-chat-v3/.
const v3Dir = resolve(root, "tests", "fixtures", "persona-chat-v3");
const v3Samples = {
  "gamecult.persona.chat_pending.v2.image": { tuple: docs.pendingTuple({ persona: "eureka", deliveries: [{ ...delivery, image }] }), parse: docs.parsePending },
  "gamecult.persona.chat_pending.v2.no-image": { tuple: docs.pendingTuple({ persona: "eureka", deliveries: [delivery] }), parse: docs.parsePending },
};
for (const [name, { tuple, parse }] of Object.entries(v3Samples)) {
  test(`golden fixture: ${name} is byte-stable and parses back`, async () => {
    const bytes = Buffer.from(msgpack.encode(tuple));
    const file = resolve(v3Dir, `${name}.msgpack`);
    if (process.env.UPDATE_FIXTURES === "1") { await mkdir(v3Dir, { recursive: true }); await writeFile(file, bytes); }
    assert.deepEqual(bytes, await readFile(file), "wire bytes changed: Huginn decodes these fixtures");
    assert.deepEqual(parse(msgpack.decode(bytes)), parse(tuple));
    assert.equal(msgpack.decode(bytes)[1][0].length, 12);
  });
}

// The pending answer v3 (ChatDelivery v4, basis and form): the bytes Huginn pins in its crates/mind-body/tests/fixtures/persona-chat-v5/.
const v5Dir = resolve(root, "tests", "fixtures", "persona-chat-v5");
const v5Samples = {
  "gamecult.persona.chat_pending_request.v2": { tuple: docs.pendingRequestTuple({ persona: "eureka", afterSeq: 3 }), parse: docs.parsePendingRequest },
  "gamecult.persona.chat_pending.v3.tag": { tuple: docs.pendingV3Tuple({ persona: "eureka", deliveries: [{ ...delivery, isDirect: false, basis: "tag", form: "message" }] }), parse: docs.parsePendingV3 },
  "gamecult.persona.chat_pending.v3.tail": { tuple: docs.pendingV3Tuple({ persona: "eureka", deliveries: [], tail: [
    { roomAlias: "#operator:gamecult.org", eventId: "$e0", atUnixMs: 1_700_000_000_000, sender: "@meta:gamecult.org", form: "message", basis: null, text: "earlier" },
    { roomAlias: "#operator:gamecult.org", eventId: "$e1", atUnixMs: 1_700_000_001_000, sender: "@meta:gamecult.org", form: "notice", basis: "tag", text: "hello eureka" },
  ] }), parse: docs.parsePendingV3 },
  "gamecult.persona.chat_pending.v3.tail-gaps": { tuple: docs.pendingV3Tuple({ persona: "eureka", deliveries: [], tail: [
    { roomAlias: "#operator:gamecult.org", eventId: "$e1", atUnixMs: 1_700_000_001_000, sender: "@meta:gamecult.org", form: "message", basis: null, text: "hello eureka" },
  ], tailGaps: ["#swarm:gamecult.org", "!dm1:gamecult.org"] }), parse: docs.parsePendingV3 },
  "gamecult.persona.chat_pending.v3.unproven-notice": { tuple: docs.pendingV3Tuple({ persona: "eureka", deliveries: [{ ...delivery, isDirect: false, mentions: [], basis: null, form: "notice" }] }), parse: docs.parsePendingV3 },
};
for (const [name, { tuple, parse }] of Object.entries(v5Samples)) {
  test(`golden fixture: ${name} is byte-stable and parses back`, async () => {
    const bytes = Buffer.from(msgpack.encode(tuple));
    const file = resolve(v5Dir, `${name}.msgpack`);
    if (process.env.UPDATE_FIXTURES === "1") { await mkdir(v5Dir, { recursive: true }); await writeFile(file, bytes); }
    assert.deepEqual(bytes, await readFile(file), "wire bytes changed: Huginn decodes these fixtures");
    assert.deepEqual(parse(msgpack.decode(bytes)), parse(tuple));
    if (name.includes("pending.v3")) assert.equal(msgpack.decode(bytes).length, 4, "persona, deliveries, tail, tail_gaps");
    if (name.endsWith("v3.tag") || name.endsWith("unproven-notice")) assert.equal(msgpack.decode(bytes)[1][0].length, 14);
    if (name.endsWith("v3.tail")) { assert.equal(msgpack.decode(bytes)[2][0].length, 7); assert.deepEqual(msgpack.decode(bytes)[3], [], "no gap: every room read fully"); }
    if (name.endsWith("v3.tail-gaps")) assert.deepEqual(msgpack.decode(bytes)[3], ["#swarm:gamecult.org", "!dm1:gamecult.org"]);
  });
}

// The send pair v2: the bytes the Huginn organ pins from its side.
const v2Dir = resolve(root, "tests", "fixtures", "persona-chat-v2");
const v2Request = basis => docs.sendRequestV2Tuple({ requestId: "req-1", persona: "eureka", roomAlias: "#swarm:gamecult.org", text: "hi", basis });
const v2Samples = {
  "gamecult.persona.chat_send_request.v2.nil-basis": { tuple: v2Request(null), parse: docs.parseSendRequestV2 },
  "gamecult.persona.chat_send_request.v2.seen-and-since": { tuple: v2Request({ seenEventId: "$seen", sinceUnixMs: 1793698425998 }), parse: docs.parseSendRequestV2 },
  "gamecult.persona.chat_send_request.v2.since-only": { tuple: v2Request({ seenEventId: null, sinceUnixMs: 1793698425998 }), parse: docs.parseSendRequestV2 },
  "gamecult.persona.chat_send_receipt.v2.sent": { tuple: docs.sendReceiptV2Tuple({ requestId: "req-1", eventId: "$sent" }), parse: docs.parseSendReceiptV2 },
  "gamecult.persona.chat_send_receipt.v2.room-moved": { tuple: docs.sendReceiptV2Tuple({ requestId: "req-1", refusal: "RoomMoved", newer: ["$a", "$b"] }), parse: docs.parseSendReceiptV2 },
};
for (const [name, { tuple, parse }] of Object.entries(v2Samples)) {
  test(`golden fixture: ${name} is byte-stable and parses back`, async () => {
    const bytes = Buffer.from(msgpack.encode(tuple));
    const file = resolve(v2Dir, `${name}.msgpack`);
    if (process.env.UPDATE_FIXTURES === "1") { await mkdir(v2Dir, { recursive: true }); await writeFile(file, bytes); }
    assert.deepEqual(bytes, await readFile(file), "wire bytes changed: Huginn decodes these fixtures");
    assert.deepEqual(parse(msgpack.decode(bytes)), parse(tuple));
  });
}

test("RoomMoved is v2 only, and the v2 documents are strict about arity and newer", () => {
  assert.throws(() => docs.sendReceiptTuple({ requestId: "r", refusal: "RoomMoved" }), /unknown refusal/, "the v1 receipt cannot carry RoomMoved");
  assert.throws(() => docs.parseSendReceipt(["r", null, "RoomMoved"]), /unknown refusal/);
  assert.throws(() => docs.refusalTuple({ persona: "p", refusal: "RoomMoved" }));
  assert.throws(() => docs.sendReceiptV2Tuple({ requestId: "r", refusal: "RoomMoved" }), /newer/, "RoomMoved names what moved the room");
  assert.throws(() => docs.sendReceiptV2Tuple({ requestId: "r", eventId: "$e", newer: ["$x"] }), /newer/, "a sent receipt names nothing");
  assert.throws(() => docs.sendReceiptV2Tuple({ requestId: "r", refusal: "RoomMoved", newer: Array.from({ length: 51 }, (_, i) => `$${i}`) }), /50/);
  assert.throws(() => docs.parseSendRequestV2(["r", "eureka", "#a:b", "x"]), /5-field/, "a v1 tuple is not a v2 request");
  assert.throws(() => docs.parseSendRequest(v2Request(null)), /4-field/, "a v2 tuple is not a v1 request");
  assert.throws(() => docs.parseSendRequestV2(["r", "eureka", "#a:b", "x", [null]]), /2-field/);
  assert.throws(() => docs.parseSendRequestV2(["r", "eureka", "#a:b", "x", [null, -1]]));
});

test("the RUDP endpoint answers a v1 send with the three-field receipt and a v2 send with the four-field one", async () => {
  const seen = [];
  const mouth = { pendingAfter: () => [], tail: async () => ({ lines: [], gaps: [], rooms: [] }), ack: async () => {}, send: async request => {
    seen.push(request);
    return request.basis ? { requestId: request.requestId, eventId: null, refusal: "RoomMoved", newer: ["$a"] } : { requestId: request.requestId, eventId: "$sent", refusal: null, newer: [] };
  } };
  const key = "connection-key-for-v2";
  const server = await startChatEndpoint({ mouths: new Map([["eureka", mouth]]), runtime, port: 0, connectionKey: key, runtimeId: "mouth-test" });
  const client = dgram.createSocket("udp4");
  await new Promise(ok => client.bind(0, "127.0.0.1", ok));
  const connection = new runtime.cultnet.CultNetRudpSocketTransportConnection({
    runtimeId: "huginn-test", socket: client, mode: "client", remoteHost: "127.0.0.1", remotePort: server.address.port,
    connectionId: connectionIdFor(key), initialSequence: 1, resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200,
  });
  const replies = [];
  connection.on("frame", value => replies.push(runtime.cultnet.parseCultNetMessage(msgpack.decode(value.payload), "cultnet.schema.v0").document));
  const put = (kind, recordKey, tuple) => connection.send("schema", msgpack.encode(runtime.cultnet.encodeCultNetMessageForWire(
    docs.envelope({ kind, recordKey, payload: msgpack.encode(tuple), sourceRuntimeId: "huginn-test", sourceAgentId: "eureka", sourceRole: "persona-organ" }), "cultnet.schema.v0")));
  const reply = async n => { for (let i = 0; i < 250 && replies.length < n; i++) await new Promise(r => setTimeout(r, 20)); assert.ok(replies.length >= n, "the mouth answered"); return replies[n - 1]; };
  try {
    connection.connect();
    for (let i = 0; i < 100 && !connection.connected; i++) await new Promise(r => setTimeout(r, 20));
    put(docs.kinds.sendRequest, "v1", docs.sendRequestTuple({ requestId: "v1", persona: "eureka", roomAlias: "#swarm:gamecult.org", text: "old" }));
    let document = await reply(1);
    assert.equal(document.schemaId, docs.kinds.sendReceipt);
    assert.deepEqual(msgpack.decode(document.payload), ["v1", "$sent", null], "three fields, as the old organ parses them");
    put(docs.kinds.sendRequestV2, "v2", docs.sendRequestV2Tuple({ requestId: "v2", persona: "eureka", roomAlias: "#swarm:gamecult.org", text: "new", basis: { seenEventId: "$s", sinceUnixMs: 5 } }));
    document = await reply(2);
    assert.equal(document.schemaId, docs.kinds.sendReceiptV2);
    assert.deepEqual(docs.parseSendReceiptV2(msgpack.decode(document.payload)), { requestId: "v2", eventId: null, refusal: "RoomMoved", newer: ["$a"] });
    assert.deepEqual(seen.map(r => r.basis), [undefined, { seenEventId: "$s", sinceUnixMs: 5 }], "v1 reaches the mouth with no basis, v2 with hers");
    put(docs.kinds.sendRequestV2, "v2-ghost", docs.sendRequestV2Tuple({ requestId: "v2-ghost", persona: "ghost", roomAlias: "#swarm:gamecult.org", text: "x" }));
    document = await reply(3);
    assert.deepEqual(docs.parseSendReceiptV2(msgpack.decode(document.payload)), { requestId: "v2-ghost", eventId: null, refusal: "UnknownPersona", newer: [] });
  } finally {
    server.close();
    connection.close();
    try { client.close(); } catch { /* the connection already closed its socket */ }
  }
});

test("over the RUDP endpoint: pending and an ack post no receipt, a read report posts one, and it never holds up the same peer's next request", async () => {
  const receipts = [];
  let release;
  const held = new Promise(r => { release = r; });
  const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
  const message = { type: "m.room.message", event_id: "$m1", sender: "@alice:t", origin_server_ts: 5, content: { msgtype: "m.text", body: "hi" } };
  const fetch = async (url, init = {}) => {
    const u = new URL(url), p = u.pathname.replace("/_matrix/client/v3", "");
    if (p === "/account/whoami") return json({ user_id: "@eureka:t" });
    if (p === "/joined_rooms") return json({ joined_rooms: ["!r:t"] });
    if (p.startsWith("/directory/room/")) return json({ room_id: "!r:t" });
    if (p.includes("/account_data/")) return json({}, 404);
    if (p === "/sync") return json(u.searchParams.get("since") === "b0" ? { next_batch: "b1", rooms: { join: { "!r:t": { timeline: { events: [message] } } } } } : { next_batch: "b0" });
    if (p.includes("/messages")) return json({ chunk: [] });
    if (p.includes("/receipt/")) { receipts.push(p); await held; return json({}); }
    return json({}, 404);
  };
  const state = { load: async () => null, save: async () => {} };
  const mouth = createMouth({ persona: "eureka", homeserver: "http://hs.test", rooms: ["#r:t"], token: "tok", state, fetch, serverName: "t", brake: async () => true, syncTimeoutMs: 1 });
  await mouth.syncOnce(); await mouth.syncOnce();
  const key = "connection-key-for-receipts";
  const server = await startChatEndpoint({ mouths: new Map([["eureka", mouth]]), runtime, port: 0, connectionKey: key, runtimeId: "mouth-test" });
  const client = dgram.createSocket("udp4");
  await new Promise(ok => client.bind(0, "127.0.0.1", ok));
  const connection = new runtime.cultnet.CultNetRudpSocketTransportConnection({
    runtimeId: "huginn-test", socket: client, mode: "client", remoteHost: "127.0.0.1", remotePort: server.address.port,
    connectionId: connectionIdFor(key), initialSequence: 1, resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200,
  });
  const replies = [];
  connection.on("frame", value => replies.push(runtime.cultnet.parseCultNetMessage(msgpack.decode(value.payload), "cultnet.schema.v0").document));
  const put = (kind, recordKey, tuple) => connection.send("schema", msgpack.encode(runtime.cultnet.encodeCultNetMessageForWire(
    docs.envelope({ kind, recordKey, payload: msgpack.encode(tuple), sourceRuntimeId: "huginn-test", sourceAgentId: "eureka", sourceRole: "persona-organ" }), "cultnet.schema.v0")));
  const waitFor = async (check, what) => { for (let i = 0; i < 250 && !check(); i++) await new Promise(r => setTimeout(r, 20)); assert.ok(check(), what); };
  try {
    connection.connect();
    await waitFor(() => connection.connected, "session opens");
    put(docs.kinds.pendingRequest, "eureka", docs.pendingRequestTuple({ persona: "eureka", afterSeq: 0 }));
    await waitFor(() => replies.length === 1, "pending answered");
    assert.deepEqual(receipts, [], "a pending request posts no receipt");
    put(docs.kinds.read, "eureka:#r:t", docs.readTuple({ persona: "eureka", roomAlias: "#r:t", eventId: "$m1", atUnixMs: 6 }));
    await waitFor(() => receipts.length === 1, "the read report posted her receipt");
    assert.ok(receipts[0].endsWith("/receipt/m.read/%24m1"), "at the reported event");
    put(docs.kinds.ack, "eureka:1", docs.ackTuple({ persona: "eureka", throughSeq: 1 }));
    put(docs.kinds.pendingRequest, "eureka", docs.pendingRequestTuple({ persona: "eureka", afterSeq: 0 }));
    await waitFor(() => replies.length === 2, "the next request is answered while the receipt response is still held open");
    assert.deepEqual(docs.parsePending(msgpack.decode(replies[1].payload)).deliveries, [], "the ack applied");
    assert.equal(receipts.length, 1, "the ack posted no receipt of its own");
  } finally {
    release();
    server.close();
    connection.close();
    try { client.close(); } catch { /* already closed */ }
  }
});

// The open-direct pair (v4): the bytes Huginn pins in its crates/mind-body/tests/fixtures/persona-chat-v4/.
const v4Dir = resolve(root, "tests", "fixtures", "persona-chat-v4");
const v4Samples = {
  "gamecult.persona.chat_open_direct_request.v1": { tuple: docs.openDirectRequestTuple({ requestId: "req-1", persona: "eureka", userId: "@meta:gamecult.org", text: "hello Meta" }), parse: docs.parseOpenDirectRequest },
  "gamecult.persona.chat_open_direct_receipt.v1.opened": { tuple: docs.openDirectReceiptTuple({ requestId: "req-1", outcome: "Opened", roomId: "!dm1:gamecult.org", eventId: "$line1" }), parse: docs.parseOpenDirectReceipt },
  "gamecult.persona.chat_open_direct_receipt.v1.existing": { tuple: docs.openDirectReceiptTuple({ requestId: "req-2", outcome: "Existing", roomId: "!dm1:gamecult.org", eventId: "$line2" }), parse: docs.parseOpenDirectReceipt },
  "gamecult.persona.chat_open_direct_receipt.v1.declined": { tuple: docs.openDirectReceiptTuple({ requestId: "req-3", outcome: "Declined" }), parse: docs.parseOpenDirectReceipt },
  "gamecult.persona.chat_refusal.v1.unknown-kind": { tuple: docs.refusalTuple({ persona: "eureka", refusal: "UnknownKind" }), parse: docs.parseRefusal },
};
for (const [name, { tuple, parse }] of Object.entries(v4Samples)) {
  test(`golden fixture: ${name} is byte-stable and parses back`, async () => {
    const bytes = Buffer.from(msgpack.encode(tuple));
    const file = resolve(v4Dir, `${name}.msgpack`);
    if (process.env.UPDATE_FIXTURES === "1") { await mkdir(v4Dir, { recursive: true }); await writeFile(file, bytes); }
    assert.deepEqual(bytes, await readFile(file), "wire bytes changed: Huginn decodes these fixtures");
    assert.deepEqual(parse(msgpack.decode(bytes)), parse(tuple));
  });
}

test("the open-direct documents are strict: room and event ids exactly for Opened and Existing, a known outcome, a bounded line", () => {
  for (const outcome of docs.directOutcomes) {
    const placed = outcome === "Opened" || outcome === "Existing";
    assert.doesNotThrow(() => docs.openDirectReceiptTuple(placed ? { requestId: "r", outcome, roomId: "!a:b", eventId: "$e" } : { requestId: "r", outcome }), outcome);
    assert.throws(() => docs.openDirectReceiptTuple(placed ? { requestId: "r", outcome } : { requestId: "r", outcome, roomId: "!a:b", eventId: "$e" }), /exactly for Opened and Existing/, outcome);
    assert.throws(() => docs.parseOpenDirectReceipt(["r", outcome, placed ? "!a:b" : null, placed ? null : "$e"]), /exactly for/, `${outcome}: event id alone is half a placement`);
  }
  assert.throws(() => docs.openDirectReceiptTuple({ requestId: "r", outcome: "Invented" }), /unknown open-direct outcome/);
  assert.throws(() => docs.parseOpenDirectRequest(["r", "eureka", "@a:b", "x".repeat(4001)]), /byte bound/);
  assert.throws(() => docs.parseOpenDirectRequest(["r", "eureka", "", "x"]), /user_id/);
  assert.throws(() => docs.parseOpenDirectRequest(["r", "eureka", "@a:b"]), /4-field/);
  assert.throws(() => docs.sendReceiptTuple({ requestId: "r", refusal: "UnknownKind" }), /unknown refusal/, "UnknownKind exists only in the typed refusal document");
});

test("the RUDP endpoint answers an open-direct request with a receipt for its Persona, refuses another Persona's and answers a request of an unknown kind loudly", async () => {
  const opened = [];
  const mouth = { openDirect: async request => { opened.push(request); return { requestId: request.requestId, outcome: "Opened", roomId: "!dm1:gamecult.org", eventId: "$line1" }; } };
  const key = "connection-key-for-test";
  const logged = [];
  const server = await startChatEndpoint({ mouths: new Map([["eureka", mouth]]), runtime, port: 0, connectionKey: key, runtimeId: "mouth-test", log: line => logged.push(line) });
  const client = dgram.createSocket("udp4");
  await new Promise(ok => client.bind(0, "127.0.0.1", ok));
  const connection = new runtime.cultnet.CultNetRudpSocketTransportConnection({
    runtimeId: "huginn-test", socket: client, mode: "client", remoteHost: "127.0.0.1", remotePort: server.address.port,
    connectionId: connectionIdFor(key), initialSequence: 1, resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200,
  });
  const replies = [];
  const waiting = [];
  connection.on("frame", value => {
    replies.push(runtime.cultnet.parseCultNetMessage(msgpack.decode(value.payload), "cultnet.schema.v0").document);
    waiting.splice(0).forEach(w => w());
  });
  const nextReply = () => new Promise((ok, fail) => { const t = setTimeout(() => fail(new Error(`no reply; the mouth logged: ${logged.join(" | ")}`)), 5000); waiting.push(() => { clearTimeout(t); ok(replies.at(-1)); }); });
  const put = (kind, recordKey, tuple, agent = "eureka") => connection.send("schema", msgpack.encode(runtime.cultnet.encodeCultNetMessageForWire(
    docs.envelope({ kind, recordKey, payload: msgpack.encode(tuple), sourceRuntimeId: "huginn-test", sourceAgentId: agent, sourceRole: "persona-organ" }), "cultnet.schema.v0")));
  try {
    connection.connect();
    for (let i = 0; i < 100 && !connection.connected; i++) await new Promise(r => setTimeout(r, 20));
    assert.ok(connection.connected);
    let reply = nextReply();
    put(docs.kinds.openDirectRequest, "req-1", docs.openDirectRequestTuple({ requestId: "req-1", persona: "eureka", userId: "@meta:gamecult.org", text: "hello" }));
    let document = await reply;
    assert.equal(document.schemaId, docs.kinds.openDirectReceipt);
    assert.deepEqual(docs.parseOpenDirectReceipt(msgpack.decode(document.payload)), { requestId: "req-1", outcome: "Opened", roomId: "!dm1:gamecult.org", eventId: "$line1" });
    assert.deepEqual(opened, [{ requestId: "req-1", persona: "eureka", userId: "@meta:gamecult.org", text: "hello" }]);

    reply = nextReply();
    put(docs.kinds.openDirectRequest, "req-2", docs.openDirectRequestTuple({ requestId: "req-2", persona: "other", userId: "@meta:gamecult.org", text: "hello" }), "other");
    document = await reply;
    assert.deepEqual(docs.parseOpenDirectReceipt(msgpack.decode(document.payload)), { requestId: "req-2", outcome: "UnknownPersona", roomId: null, eventId: null });
    assert.equal(opened.length, 1, "nothing reached a mouth for the unlisted Persona");

    reply = nextReply();
    put("gamecult.persona.chat_invented_request.v9", "req-3", ["x"]);
    document = await reply;
    assert.deepEqual([document.schemaId, docs.parseRefusal(msgpack.decode(document.payload))], [docs.kinds.refusal, { persona: "eureka", refusal: "UnknownKind" }], "an unknown kind is answered, typed");
    assert.ok(logged.some(l => /unknown kind: gamecult\.persona\.chat_invented_request\.v9/.test(l)), "and logged");
  } finally {
    server.close();
    connection.close();
    try { client.close(); } catch { /* the connection already closed its socket */ }
  }
});

test("tail_gaps is additive: a three-field reader finds persona, deliveries and tail unchanged in the first three elements of the new bytes", async () => {
  const sample = { persona: "eureka", deliveries: [{ ...delivery, isDirect: false, basis: "tag", form: "message" }], tail: [{ roomAlias: "#operator:gamecult.org", eventId: "$e0", atUnixMs: 1_700_000_000_000, sender: "@meta:gamecult.org", form: "message", basis: null, text: "earlier" }] };
  const noGaps = msgpack.decode(msgpack.encode(docs.pendingV3Tuple(sample)));
  const after = msgpack.decode(await readFile(resolve(root, "tests", "fixtures", "persona-chat-v5", "gamecult.persona.chat_pending.v3.tail-gaps.msgpack")));
  const withGaps = msgpack.decode(msgpack.encode(docs.pendingV3Tuple({ ...sample, tailGaps: ["#swarm:gamecult.org"] })));
  assert.equal(withGaps.length, 4);
  assert.deepEqual(withGaps.slice(0, 3), noGaps.slice(0, 3), "persona, deliveries and tail are byte-for-meaning the same with or without gaps");
  assert.deepEqual(docs.parsePendingV3([...withGaps.slice(0, 3), []]).tail, docs.parsePendingV3(noGaps).tail);
  assert.equal(after.length, 4);
  // Not claimed: a strict three-field decoder (rmp-serde into a three-field tuple) accepts it. It fails on the length, as it did for the tail, so Huginn defaults tail_gaps before this mouth deploys.
  assert.notEqual(withGaps.length, 3);
});

// ---- chat_pending.v4: reply_to (docs/bridge.md, "Reply link"). Behind a flag, default off.
const v6Dir = resolve(root, "tests", "fixtures", "persona-chat-v6");
const quoted = { eventId: "$q1", sender: "@alice:gamecult.org", excerpt: "can someone look at the build?" };
const v4Delivery = { ...delivery, isDirect: false, basis: null, form: "message" };
// The excerpts the mouth makes from hostile quoted text (pinned in tests/persona-matrix-mouth.test.mjs, "the fixture excerpts"): the scrubbed line, never the hidden bytes.
const scrubbedExcerpt = "ignorethisline";
const variationExcerpt = "ok\u{1F44D}fine";
const v6Samples = {
  "gamecult.persona.chat_pending_request.v3": { tuple: docs.pendingRequestTuple({ persona: "eureka", afterSeq: 3 }), parse: docs.parsePendingRequest },
  "gamecult.persona.chat_pending.v4.reply-to": { tuple: docs.pendingV4Tuple({ persona: "eureka", deliveries: [{ ...v4Delivery, replyTo: quoted }] }), parse: docs.parsePendingV4 },
  "gamecult.persona.chat_pending.v4.no-reply-to": { tuple: docs.pendingV4Tuple({ persona: "eureka", deliveries: [v4Delivery] }), parse: docs.parsePendingV4 },
  "gamecult.persona.chat_pending.v4.reply-to-redacted": { tuple: docs.pendingV4Tuple({ persona: "eureka", deliveries: [{ ...v4Delivery, replyTo: { ...quoted, excerpt: null } }] }), parse: docs.parsePendingV4 },
  "gamecult.persona.chat_pending.v4.reply-to-scrubbed": { tuple: docs.pendingV4Tuple({ persona: "eureka", deliveries: [{ ...v4Delivery, replyTo: { ...quoted, excerpt: scrubbedExcerpt } }] }), parse: docs.parsePendingV4 },
  "gamecult.persona.chat_pending.v4.reply-to-variation-selector": { tuple: docs.pendingV4Tuple({ persona: "eureka", deliveries: [{ ...v4Delivery, replyTo: { ...quoted, excerpt: variationExcerpt } }] }), parse: docs.parsePendingV4 },
};
const hexOf = bytes => `${[...bytes].map(b => b.toString(16).padStart(2, "0")).join("")}\n`;
for (const [name, { tuple, parse }] of Object.entries(v6Samples)) {
  test(`golden fixture: ${name} is byte-stable (msgpack and hex) and parses back`, async () => {
    const bytes = Buffer.from(msgpack.encode(tuple));
    if (process.env.UPDATE_FIXTURES === "1") { await mkdir(v6Dir, { recursive: true }); await writeFile(resolve(v6Dir, `${name}.msgpack`), bytes); await writeFile(resolve(v6Dir, `${name}.hex`), hexOf(bytes)); }
    assert.deepEqual(bytes, await readFile(resolve(v6Dir, `${name}.msgpack`)), "wire bytes changed: Huginn decodes these fixtures");
    assert.equal(await readFile(resolve(v6Dir, `${name}.hex`), "utf8"), hexOf(bytes), "the hex dump is the msgpack file");
    assert.deepEqual(parse(msgpack.decode(bytes)), parse(tuple));
    if (name.includes("pending.v4")) {
      const decoded = msgpack.decode(bytes);
      assert.equal(decoded.length, 4, "persona, deliveries, tail, tail_gaps");
      assert.equal(decoded[1][0].length, 15, "the 14 v3 fields, then reply_to");
      assert.deepEqual(decoded[1][0][14], name.endsWith("no-reply-to") ? null : [quoted.eventId, quoted.sender, name.endsWith("redacted") ? null : name.endsWith("scrubbed") ? scrubbedExcerpt : name.endsWith("variation-selector") ? variationExcerpt : quoted.excerpt]);
    }
  });
}

test("documents: reply_to refuses an event id or sender that is not an id of at most 255 bytes, on encode and on parse", () => {
  const ok = { eventId: "$q1", sender: "@alice:gamecult.org", excerpt: null };
  const d = { ...delivery, isDirect: false, basis: null, form: "message" };
  for (const bad of [{ eventId: "q1" }, { eventId: "$" }, { eventId: `$${"x".repeat(255)}` }, { eventId: "$a b" }, { sender: "alice" }, { sender: "@" }, { sender: `@${"é".repeat(128)}` }, { sender: "@a:b\u0085" }])
    assert.throws(() => docs.deliveryV4Tuple({ ...d, replyTo: { ...ok, ...bad } }), /reply_to/, JSON.stringify(bad).slice(0, 40));
  assert.doesNotThrow(() => docs.deliveryV4Tuple({ ...d, replyTo: { ...ok, eventId: `$${"x".repeat(254)}` } }));
  const tuple = docs.deliveryV4Tuple({ ...d, replyTo: ok });
  tuple[14] = ["q1", ok.sender, null];
  assert.throws(() => docs.parseDeliveryV4(tuple), /reply_to/);
});

test("v1, v2 and v3 bytes are unchanged by v4: a delivery without reply_to encodes to the v3 bytes plus one nil, and the older tuples never see the field", async () => {
  const v3 = msgpack.encode(docs.pendingV3Tuple({ persona: "eureka", deliveries: [v4Delivery] }));
  assert.deepEqual(docs.deliveryV3Tuple({ ...v4Delivery, replyTo: quoted }), docs.deliveryV3Tuple(v4Delivery), "reply_to never reaches a v3 tuple");
  assert.deepEqual(docs.deliveryTuple({ ...v4Delivery, replyTo: quoted }), docs.deliveryTuple(v4Delivery), "nor a v2 tuple");
  assert.deepEqual(docs.deliveryV4Tuple(v4Delivery).slice(0, 14), docs.deliveryV3Tuple(v4Delivery));
  assert.equal(msgpack.decode(v3)[1][0].length, 14);
  const golden = msgpack.decode(await readFile(resolve(v5Dir, "gamecult.persona.chat_pending.v3.unproven-notice.msgpack")));
  assert.equal(golden[1][0].length, 14, "the pinned v3 fixture stays 14 fields");
});

test("reply_to tuple: an excerpt over 240 code points, an empty id or sender, or a bad shape is refused; the cap counts code points, not bytes", () => {
  const ok = n => docs.deliveryV4Tuple({ ...v4Delivery, replyTo: { ...quoted, excerpt: "\u{1F600}".repeat(n) } });
  assert.equal([...ok(240)[14][2]].length, 240, "240 astral characters (960 bytes) fit");
  assert.throws(() => ok(241), /at most 240 code points/);
  assert.throws(() => docs.deliveryV4Tuple({ ...v4Delivery, replyTo: { ...quoted, sender: "" } }), /reply_to sender/);
  assert.throws(() => docs.parseDeliveryV4([...docs.deliveryV3Tuple(v4Delivery), ["$q"]]), /3-field/);
  assert.throws(() => docs.parseDeliveryV4(docs.deliveryV3Tuple(v4Delivery)), /15-field/);
});

test("v4 paging: reply_to counts toward each delivery's size, and one oversize or malformed delivery cannot break the page", () => {
  const mk = (n, replyTo, body = "ok") => ({ ...v4Delivery, seq: n, eventId: `$e${n}`, text: body, replyTo });
  const logs = [];
  const page = pageOf("eureka", [mk(1, quoted), mk(2, { ...quoted, excerpt: "x".repeat(241) }), mk(3, null)], msgpack, l => logs.push(l), true, undefined, true);
  assert.deepEqual(page[1].map(t => t[0]), [1, 3], "the delivery whose reply_to will not encode is skipped, logged, and the rest served");
  assert.ok(logs.some(l => l.includes("delivery 2 skipped")));
  const size = msgpack.encode(pageOf("eureka", [mk(1, quoted)], msgpack, () => {}, true, undefined, true)[1][0]).length;
  const bare = msgpack.encode(pageOf("eureka", [mk(1, null)], msgpack, () => {}, true, undefined, true)[1][0]).length;
  assert.ok(size > bare + quoted.excerpt.length, "the quote is part of the delivery's size");
  const many = Array.from({ length: 400 }, (_, i) => mk(i + 1, { ...quoted, excerpt: "y".repeat(240) }, "x".repeat(3900)));
  const paged = pageOf("eureka", many, msgpack, () => {}, true, undefined, true);
  assert.ok(msgpack.encode(paged).length <= docs.limits.replyBytes + 1000 && paged[1].length > 1 && paged[1].length < 400, "quotes count toward the 256 KiB budget");
  assert.equal(pageOf("eureka", [mk(1, quoted, "x".repeat(4000))], msgpack, () => {}, true, undefined, true)[1].length, 1, "a single delivery is always served");
  assert.equal(docs.parsePendingV3(pageOf("eureka", [mk(1, quoted)], msgpack, () => {}, true))?.deliveries.length, 1, "asked as v3, the same delivery is served without reply_to");
  assert.equal(pageOf("eureka", [mk(1, quoted)], msgpack, () => {}, true)[1][0].length, 14);
});

// Dial a throwaway endpoint, send one request and return the first document answered.
async function askOnce({ replyLink, kind, tuple }) {
  const mouth = { pendingAfter: () => [{ ...v4Delivery, replyTo: quoted }], tail: async () => ({ lines: [], gaps: [], rooms: [] }), speakableRooms: () => [] };
  const key = "connection-key-v4";
  const logs = [];
  const server = await startChatEndpoint({ mouths: new Map([["eureka", mouth]]), runtime, port: 0, connectionKey: key, runtimeId: "mouth-v4", log: l => logs.push(l), ...(replyLink === undefined ? {} : { replyLink }) });
  const client = dgram.createSocket("udp4");
  await new Promise(ok => client.bind(0, "127.0.0.1", ok));
  const connection = new runtime.cultnet.CultNetRudpSocketTransportConnection({ runtimeId: "huginn-v4", socket: client, mode: "client", remoteHost: "127.0.0.1", remotePort: server.address.port, connectionId: connectionIdFor(key), initialSequence: 1, resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200 });
  try {
    const got = new Promise((ok, fail) => { const t = setTimeout(() => fail(new Error(`no reply; logged ${logs.join(" | ")}`)), 5000); connection.on("frame", v => { clearTimeout(t); ok(runtime.cultnet.parseCultNetMessage(msgpack.decode(v.payload), "cultnet.schema.v0").document); }); });
    connection.connect();
    for (let i = 0; i < 100 && !connection.connected; i++) await new Promise(r => setTimeout(r, 20));
    connection.send("schema", msgpack.encode(runtime.cultnet.encodeCultNetMessageForWire(docs.envelope({ kind, recordKey: "eureka", payload: msgpack.encode(tuple), sourceRuntimeId: "huginn-v4", sourceAgentId: "eureka", sourceRole: "persona-organ" }), "cultnet.schema.v0")));
    return { document: await got, logs };
  } finally { server.close(); connection.close(); try { client.close(); } catch { /* closed */ } }
}

test("v4 flag off (the default): a chat_pending_request.v3 is answered exactly as any unknown kind is, and the v3 kind is unchanged", async () => {
  const request = docs.pendingRequestTuple({ persona: "eureka", afterSeq: 0 });
  for (const flag of [undefined, false]) {
    const { document, logs } = await askOnce({ replyLink: flag, kind: docs.kinds.pendingRequestV4, tuple: request });
    assert.deepEqual([document.schemaId, docs.parseRefusal(msgpack.decode(document.payload))], [docs.kinds.refusal, { persona: "eureka", refusal: "UnknownKind" }]);
    assert.ok(logs.some(l => l.includes("unknown kind: gamecult.persona.chat_pending_request.v3")));
    const same = await askOnce({ replyLink: flag, kind: "gamecult.persona.chat_pending_request.v9", tuple: request });
    assert.deepEqual(Buffer.from(same.document.payload), Buffer.from(document.payload), "byte for byte the answer to a kind that never existed");
  }
  const v3 = await askOnce({ replyLink: false, kind: docs.kinds.pendingRequestV3, tuple: request });
  assert.equal(v3.document.schemaId, docs.kinds.pendingV3);
  assert.equal(msgpack.decode(v3.document.payload)[1][0].length, 14, "a v3 request never gets reply_to, flag or no");
});

test("v4 flag on: the v3 request kind is answered with chat_pending.v4 over RUDP, reply_to in field 14 of each delivery; v1 and v3 requests are unchanged", async () => {
  const request = docs.pendingRequestTuple({ persona: "eureka", afterSeq: 0 });
  const { document } = await askOnce({ replyLink: true, kind: docs.kinds.pendingRequestV4, tuple: request });
  assert.equal(document.schemaId, docs.kinds.pendingV4);
  const wire = msgpack.decode(document.payload);
  assert.deepEqual(wire[1][0][14], [quoted.eventId, quoted.sender, quoted.excerpt]);
  assert.equal(docs.parsePendingV4(wire).deliveries[0].replyTo.sender, "@alice:gamecult.org");
  const v3 = await askOnce({ replyLink: true, kind: docs.kinds.pendingRequestV3, tuple: request });
  assert.equal(msgpack.decode(v3.document.payload)[1][0].length, 14);
  const v1 = await askOnce({ replyLink: true, kind: docs.kinds.pendingRequest, tuple: request });
  assert.equal(msgpack.decode(v1.document.payload)[1][0].length, 12);
});

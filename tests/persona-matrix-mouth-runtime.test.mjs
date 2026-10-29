// The parts of the Matrix mouth that sit on CultLib: the chat_* wire documents, the
// CultCache cursor and brake stores, and the CultNet RUDP endpoint. Needs the built
// CultLib TypeScript packages (CULTLIB_ROOT, else ../CultLib), like the other Bifrost tests.
import test from "node:test";
import assert from "node:assert/strict";
import dgram from "node:dgram";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import * as docs from "../tools/persona-chat-documents.mjs";
import { cultcacheState, connectionIdFor, createBrakeReader, loadRuntime, startChatEndpoint } from "../tools/persona-matrix-mouth.mjs";

const root = resolve(import.meta.dirname, "..");
const runtime = loadRuntime(resolve(process.env.CULTLIB_ROOT ?? resolve(root, "..", "CultLib")));
const { msgpack } = runtime;
const fixtureDir = resolve(import.meta.dirname, "fixtures", "persona-chat-v1");

const delivery = {
  seq: 7, roomAlias: "#operator:gamecult.org", roomId: "!abc:gamecult.org", eventId: "$e1", sender: "@meta:gamecult.org", senderDisplay: "Meta",
  text: "hello eureka", atUnixMs: 1761700000000,
  context: [{ sender: "@eureka:gamecult.org", text: "earlier", atUnixMs: 1761699999000 }],
};
const samples = {
  [docs.kinds.pendingRequest]: { tuple: docs.pendingRequestTuple({ persona: "eureka", afterSeq: 3 }), parse: docs.parsePendingRequest },
  [docs.kinds.pending]: { tuple: docs.pendingTuple({ persona: "eureka", deliveries: [delivery] }), parse: docs.parsePending },
  [docs.kinds.ack]: { tuple: docs.ackTuple({ persona: "eureka", throughSeq: 7 }), parse: docs.parseAck },
  [docs.kinds.sendRequest]: { tuple: docs.sendRequestTuple({ requestId: "req-1", persona: "eureka", roomAlias: "#operator:gamecult.org", text: "hi" }), parse: docs.parseSendRequest },
  [docs.kinds.sendReceipt]: { tuple: docs.sendReceiptTuple({ requestId: "req-1", eventId: "$sent" }), parse: docs.parseSendReceipt },
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
  assert.throws(() => docs.parseSendReceipt(["r", "$e", "Brake"]), /exactly one/);
  assert.throws(() => docs.parseSendReceipt(["r", null, null]), /exactly one/);
  assert.throws(() => docs.sendReceiptTuple({ requestId: "r", refusal: "Invented" }));
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
  const server = await startChatEndpoint({ mouth, persona: "eureka", runtime, port: 0, connectionKey: key, runtimeId: "mouth-test", log: line => rejected.push(line) });
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
    assert.deepEqual(pending.deliveries[0].context, delivery.context);

    put(docs.kinds.ack, "eureka:7", docs.ackTuple({ persona: "eureka", throughSeq: 7 }));
    reply = nextReply();
    put(docs.kinds.sendRequest, "req-2", docs.sendRequestTuple({ requestId: "req-2", persona: "eureka", roomAlias: "#operator:gamecult.org", text: "muted" }));
    document = await reply;
    assert.equal(document.schemaId, docs.kinds.sendReceipt);
    assert.deepEqual(docs.parseSendReceipt(msgpack.decode(document.payload)), { requestId: "req-2", eventId: null, refusal: "Brake" });
    put(docs.kinds.ack, "other:9", docs.ackTuple({ persona: "other", throughSeq: 9 }));
    assert.deepEqual(calls.acks, [7]);

    reply = nextReply();
    put(docs.kinds.pendingRequest, "other", docs.pendingRequestTuple({ persona: "other", afterSeq: 0 }));
    put(docs.kinds.sendRequest, "req-3", docs.sendRequestTuple({ requestId: "req-3", persona: "eureka", roomAlias: "#operator:gamecult.org", text: "speak" }));
    document = await reply;
    assert.equal(document.schemaId, docs.kinds.sendReceipt, "a request naming another Persona gets no answer; the next request is still served");
    assert.equal(docs.parseSendReceipt(msgpack.decode(document.payload)).eventId, "$sent");
    assert.deepEqual(calls.acks, [7], "an ack naming another Persona is not applied");
  } finally {
    server.close();
    connection.close();
    try { client.close(); } catch { /* the connection already closed its socket */ }
  }
});

test("the RUDP endpoint refuses to listen beyond loopback", async () => {
  await assert.rejects(startChatEndpoint({ mouth: {}, persona: "eureka", runtime, port: 0, host: "0.0.0.0", connectionKey: "k", runtimeId: "m" }), /loopback/);
});

test("loopback means exactly an IPv4 127.x.y.z address", async () => {
  const refuses = host => assert.rejects(startChatEndpoint({ mouth: {}, persona: "eureka", runtime, port: 0, host, connectionKey: "k", runtimeId: "m" }), /loopback/);
  for (const host of ["9127.0.0.1", "127.0.0.1x", "10.127.0.0.1", "127.0.0", ""]) await refuses(host);
  const ok = await startChatEndpoint({ mouth: {}, persona: "eureka", runtime, port: 0, host: "127.10.20.30", connectionKey: "k", runtimeId: "m" }).catch(e => e);
  assert.ok(!/loopback/.test(ok?.message ?? ""), "a multi-digit loopback address is accepted by the check");
  ok.close?.();
});

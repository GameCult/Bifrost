import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { resolve, join } from "node:path";
import { createMouth, createBrakeReader, createRing, redact } from "../tools/persona-matrix-mouth.mjs";

const TOKEN = "syt_SECRET_TOKEN_9f3";
const HS_NAME = "gamecult.test";
const ROOMS = { [`#swarm:${HS_NAME}`]: "!swarm:gamecult.test", [`#operator:${HS_NAME}`]: "!operator:gamecult.test" };
const SELF = `@eureka:${HS_NAME}`;

const message = (id, sender, body, ts) => ({ type: "m.room.message", event_id: id, sender, origin_server_ts: ts, content: { msgtype: "m.text", body } });
const tick = () => new Promise(r => setImmediate(r));

// A fake Matrix client-server API: just the endpoints the mouth uses.
async function fakeHomeserver({ sync, failWith } = {}) {
  const seen = [];
  const sent = new Map(); // txnId -> event_id
  const server = createServer((req, res) => {
    let raw = "";
    req.on("data", c => (raw += c));
    req.on("end", () => {
      const url = new URL(req.url, "http://x");
      const record = { method: req.method, path: decodeURIComponent(url.pathname), query: url.searchParams, auth: req.headers.authorization, body: raw ? JSON.parse(raw) : null };
      seen.push(record);
      const reply = (status, body) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
      if (failWith) return reply(failWith, { errcode: "M_UNKNOWN_TOKEN", error: `bad token ${record.auth}` });
      const p = record.path.replace("/_matrix/client/v3", "");
      if (p === "/account/whoami") return reply(200, { user_id: SELF });
      if (p.startsWith("/directory/room/")) return reply(200, { room_id: ROOMS[p.slice("/directory/room/".length)] });
      if (p.startsWith("/join/")) return reply(200, { room_id: p.slice(6) });
      if (p === "/sync") return reply(200, sync(record.query.get("since")));
      const m = p.match(/^\/rooms\/(.+)\/send\/m\.room\.message\/(.+)$/);
      if (m && req.method === "PUT") {
        if (!sent.has(m[2])) sent.set(m[2], `$event${sent.size + 1}`);
        return reply(200, { event_id: sent.get(m[2]) });
      }
      reply(404, { errcode: "M_NOT_FOUND" });
    });
  });
  await new Promise(ok => server.listen(0, "127.0.0.1", ok));
  return {
    url: `http://127.0.0.1:${server.address().port}`,
    seen,
    puts: () => seen.filter(r => r.method === "PUT"),
    close: () => new Promise(ok => server.close(ok)),
  };
}

function memoryState(initial = null) {
  const box = { value: initial };
  return { box, load: async () => box.value, save: async v => { box.value = v; } };
}

const joined = events => ({ rooms: { join: events } });
function mouthFor(hs, overrides = {}) {
  return createMouth({
    persona: "eureka", homeserver: hs.url, rooms: Object.keys(ROOMS), token: TOKEN, state: memoryState(),
    brake: async () => true, ring: async () => {}, log: () => {}, syncTimeoutMs: 1, ...overrides,
  });
}

// The scripted timeline: the sync with no position is the baseline; later positions advance it.
const timeline = {
  null: { next_batch: "b0", ...joined({ "!swarm:gamecult.test": { timeline: { events: [message("$old", `@alice:${HS_NAME}`, "history before the ear", 1)] } } }) },
  b0: {
    next_batch: "b1",
    ...joined({
      "!swarm:gamecult.test": {
        state: { events: [{ type: "m.room.member", state_key: `@alice:${HS_NAME}`, content: { displayname: "Alice" } }] },
        timeline: { events: [
          message("$m1", `@alice:${HS_NAME}`, "hello eureka", 1000),
          message("$m2", SELF, "my own words", 1001),
          { type: "m.room.topic", event_id: "$t", sender: `@alice:${HS_NAME}`, content: { topic: "x" } },
        ] },
      },
      "!elsewhere:gamecult.test": { timeline: { events: [message("$x", `@bob:${HS_NAME}`, "not my room", 1002)] } },
    }),
  },
  b1: { next_batch: "b2", ...joined({ "!operator:gamecult.test": { timeline: { events: [message("$m3", `@bob:${HS_NAME}`, "second batch", 2000)] } } }) },
};
const script = since => timeline[since] ?? { next_batch: since };

test("sync_message_becomes_delivery: another sender's message in a joined room is one delivery with context; own and foreign-room messages are none", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const mouth = mouthFor(hs);
    assert.deepEqual(await mouth.syncOnce(), [], "history at first sync is context, not mail");
    const made = await mouth.syncOnce();
    assert.equal(made.length, 1);
    const [d] = made;
    assert.deepEqual({ roomAlias: d.roomAlias, roomId: d.roomId, eventId: d.eventId, sender: d.sender, senderDisplay: d.senderDisplay, text: d.text, atUnixMs: d.atUnixMs }, {
      roomAlias: `#swarm:${HS_NAME}`, roomId: "!swarm:gamecult.test", eventId: "$m1", sender: `@alice:${HS_NAME}`, senderDisplay: "Alice", text: "hello eureka", atUnixMs: 1000,
    });
    assert.deepEqual(d.context, [{ sender: `@alice:${HS_NAME}`, text: "history before the ear", atUnixMs: 1 }]);
    assert.equal(d.seq, 1);
    assert.deepEqual(mouth.pendingAfter(0).map(x => x.eventId), ["$m1"]);
    assert.deepEqual(mouth.pendingAfter(1), []);
  } finally { await hs.close(); }
});

test("context holds the last 20 messages before the delivery, oldest first", async () => {
  const many = Array.from({ length: 25 }, (_, i) => message(`$h${i}`, `@alice:${HS_NAME}`, `m${i}`, 10 + i));
  const hs = await fakeHomeserver({
    sync: since => since === null
      ? { next_batch: "b0", ...joined({ "!swarm:gamecult.test": { timeline: { events: many } } }) }
      : { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: [message("$n", `@bob:${HS_NAME}`, "now", 99)] } } }) },
  });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    const [d] = await mouth.syncOnce();
    assert.equal(d.context.length, 20);
    assert.equal(d.context[0].text, "m5");
    assert.equal(d.context[19].text, "m24");
  } finally { await hs.close(); }
});

test("next_batch_persisted_only_after_ack: restart before ack re-delivers; after ack does not", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const state = memoryState();
    const first = mouthFor(hs, { state });
    await first.syncOnce(); // baseline b0, nothing pending: persisted at once
    assert.equal(state.box.value.nextBatch, "b0");
    const [d] = await first.syncOnce(); // delivery in the batch ending b1
    assert.equal(state.box.value.nextBatch, "b0", "b1 must not be persisted before an ack");

    const restarted = mouthFor(hs, { state }); // crash before the ack
    const [again] = await restarted.syncOnce();
    assert.equal(again.eventId, "$m1", "the unacked message arrives again");
    assert.ok(again.seq > d.seq, "sequence numbers never repeat across a restart");

    await restarted.ack(again.seq);
    assert.equal(state.box.value.nextBatch, "b1");

    const afterAck = mouthFor(hs, { state });
    assert.deepEqual((await afterAck.syncOnce()).map(x => x.eventId), ["$m3"], "an acked batch is not delivered again: only the newer message arrives");
    assert.equal(hs.seen.filter(r => r.path.endsWith("/sync")).at(-1).query.get("since"), "b1");
  } finally { await hs.close(); }
});

test("an ack covers only the batches whose deliveries it reaches", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const state = memoryState();
    const mouth = mouthFor(hs, { state });
    await mouth.syncOnce();
    const [a] = await mouth.syncOnce(); // batch ending b1 holds seq 1
    const [b] = await mouth.syncOnce(); // batch ending b2 holds seq 2
    assert.deepEqual([a.seq, b.seq], [1, 2]);
    await mouth.ack(1);
    assert.equal(state.box.value.nextBatch, "b1", "acking seq 1 persists the batch that ends at seq 1, not the later one");
    await mouth.ack(2);
    assert.equal(state.box.value.nextBatch, "b2");
    assert.equal(mouth.pendingCount, 0);
  } finally { await hs.close(); }
});

test("send_uses_request_id_txn: the PUT carries txnId = request_id and a repeat sends the same txnId", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const mouth = mouthFor(hs);
    const request = { requestId: "req 7f", persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: "hello world" };
    const one = await mouth.send(request);
    const two = await mouth.send(request);
    assert.equal(one.refusal, null);
    assert.equal(one.eventId, two.eventId, "the homeserver deduplicates by txnId, so a retry is the same event");
    const puts = hs.puts();
    assert.equal(puts.length, 2);
    for (const put of puts) {
      assert.equal(put.path, "/_matrix/client/v3/rooms/!swarm:gamecult.test/send/m.room.message/req 7f");
      assert.deepEqual(put.body, { msgtype: "m.text", body: "hello world" });
    }
    assert.notEqual((await mouth.send({ ...request, requestId: "req-8" })).eventId, one.eventId, "a different request is a different txn");
  } finally { await hs.close(); }
});

test("send refuses another Persona's request, an unknown room and empty text, without a PUT", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const mouth = mouthFor(hs);
    assert.equal((await mouth.send({ requestId: "a", persona: "other", roomAlias: `#swarm:${HS_NAME}`, text: "x" })).refusal, "PersonaMismatch");
    assert.equal((await mouth.send({ requestId: "b", persona: "eureka", roomAlias: "#nowhere:gamecult.test", text: "x" })).refusal, "UnknownRoom");
    assert.equal((await mouth.send({ requestId: "c", persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: "   " })).refusal, "EmptyText");
    assert.equal(hs.puts().length, 0);
  } finally { await hs.close(); }
});

const payload = value => Buffer.from(JSON.stringify(value));
const store = (...envelopes) => ({ absent: false, envelopes });
const brakeEnvelope = value => ({ type: "eureka.control.brake.v1", key: "brake", payload: payload(value) });
const jsonDecode = b => JSON.parse(Buffer.from(b).toString());
const readerFor = read => createBrakeReader({ controlPath: "x", readStore: () => read, decode: jsonDecode });

test("held_brake_refuses_send: held, throwing, missing and non-boolean brakes refuse with Brake and nothing reaches the homeserver", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const request = { requestId: "r1", persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: "speak" };
    const brakes = [
      async () => false,
      async () => { throw new Error("control store unreadable"); },
      readerFor({ absent: true, envelopes: [] }),
      readerFor(store(brakeEnvelope([false, "t", "op"]))),
      async () => "yes",
    ];
    for (const brake of brakes) {
      const receipt = await mouthFor(hs, { brake }).send(request);
      assert.deepEqual({ eventId: receipt.eventId, refusal: receipt.refusal }, { eventId: null, refusal: "Brake" });
    }
    assert.equal(hs.puts().length, 0);
    assert.equal(hs.seen.length, 0, "a refused send does not even touch the homeserver");
    const released = await mouthFor(hs, { brake: readerFor(store(brakeEnvelope([true, "t", "op"]))) }).send(request);
    assert.equal(released.refusal, null, "the same request goes through once the brake is released");
  } finally { await hs.close(); }
});

test("brake reader: only a decoded released brake counts as released", async () => {
  assert.equal(await readerFor(store(brakeEnvelope([true, "t", "op"])))(), true);
  assert.equal(await readerFor(store(brakeEnvelope([false, "t", "op"])))(), false);
  assert.equal(await readerFor(store(brakeEnvelope({ 0: true })))(), true, "integer-keyed map form");
  assert.equal(await readerFor(store(brakeEnvelope({ 0: false })))(), false);
  assert.equal(await readerFor(store(brakeEnvelope(["true"])))(), false, "a non-boolean is not a release");
  assert.equal(await readerFor(store())(), false, "no brake document");
  assert.equal(await readerFor(store({ ...brakeEnvelope([true]), key: "dial" }))(), false, "wrong key");
  assert.equal(await readerFor(store({ ...brakeEnvelope([true]), type: "other.v1" }))(), false, "wrong type");
  assert.equal(await createBrakeReader({ controlPath: "x", readStore: () => { throw new Error("corrupt"); }, decode: jsonDecode })(), false);
  assert.equal(await readerFor(store({ type: "eureka.control.brake.v1", key: "brake", payload: Buffer.from("not json") }))(), false, "undecodable bytes");
});

test("ring_starts_persona_unit_once: two deliveries in one idle window cause one start; a drained mailbox rings again", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    let rings = 0;
    const mouth = mouthFor(hs, { ring: async () => { rings++; } });
    await mouth.syncOnce(); // baseline
    await mouth.syncOnce(); // delivery 1 rings
    await mouth.syncOnce(); // delivery 2, same window, still unacked
    await tick();
    assert.equal(rings, 1);
  } finally { await hs.close(); }

  const every = await fakeHomeserver({ sync: since => ({ next_batch: `n${since}`, ...joined({ "!swarm:gamecult.test": { timeline: { events: [message(`$e${since}`, `@alice:${HS_NAME}`, "again", 5)] } } }) }) });
  try {
    let clock = 0, rings = 0;
    const mouth = mouthFor(every, { ring: async () => { rings++; }, now: () => clock });
    await mouth.syncOnce(); // baseline
    await mouth.syncOnce(); await tick();
    assert.equal(rings, 1);
    await mouth.syncOnce(); await tick();
    assert.equal(rings, 1, "inside the window and unacked: no second start");
    await mouth.ack(2);
    await mouth.syncOnce(); await tick();
    assert.equal(rings, 2, "after a drain a new delivery rings again");
    clock = 6 * 60 * 1000;
    await mouth.syncOnce(); await tick();
    assert.equal(rings, 3, "a delivery left unanswered past the window rings again");
  } finally { await every.close(); }
});

test("a failed ring is logged once and never retried", async () => {
  const logs = [];
  let calls = 0;
  const hs = await fakeHomeserver({ sync: script });
  try {
    const mouth = mouthFor(hs, { ring: async () => { calls++; throw new Error("systemctl refused"); }, log: l => logs.push(l) });
    await mouth.syncOnce(); await mouth.syncOnce(); await tick(); await tick();
    assert.equal(calls, 1);
    assert.equal(logs.filter(l => l.includes("ring failed")).length, 1);
  } finally { await hs.close(); }
});

test("ring runs exactly systemctl start --no-block on the instance's unit, with no token in the child's environment", async () => {
  const calls = [];
  await createRing({ instance: "eureka", run: (cmd, args, options, cb) => { calls.push({ cmd, args, options }); cb(null); } })();
  assert.equal(calls.length, 1);
  assert.deepEqual([calls[0].cmd, calls[0].args], ["systemctl", ["start", "--no-block", "mind-persona@eureka.service"]]);
  assert.deepEqual(Object.keys(calls[0].options.env), ["PATH"]);
  const failed = [];
  await createRing({ instance: "eureka", run: (c, a, o, cb) => cb(new Error("denied")), log: l => failed.push(l) })();
  assert.deepEqual(failed, ["ring failed: denied"]);
});

test("the Matrix token goes only in the Authorization header: never in logs, errors, deliveries or receipts", async () => {
  const logs = [];
  const bad = await fakeHomeserver({ sync: script, failWith: 401 });
  try {
    const mouth = mouthFor(bad, { log: l => logs.push(l) });
    await assert.rejects(mouth.syncOnce(), err => { assert.ok(!err.message.includes(TOKEN)); return true; });
    const receipt = await mouth.send({ requestId: "r", persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: "x" }).catch(e => ({ thrown: e.message }));
    assert.ok(!JSON.stringify(receipt).includes(TOKEN));
    const controller = new AbortController();
    const running = mouthFor(bad, { log: l => logs.push(l) }).run(controller.signal);
    await new Promise(r => setTimeout(r, 30));
    controller.abort();
    void running; // the run loop is left to its backoff; only its log lines matter here
  } finally { await bad.close(); }

  const unreachable = mouthFor({ url: "http://127.0.0.1:1" }, {
    log: l => logs.push(l),
    fetch: async (url, init) => { throw new Error(`connect failed with ${init.headers.authorization} to ${url}`); },
  });
  await assert.rejects(unreachable.syncOnce(), err => { assert.ok(!err.message.includes(TOKEN), "transport errors are redacted"); return true; });

  const hs = await fakeHomeserver({ sync: script });
  try {
    const mouth = mouthFor(hs, { log: l => logs.push(l) });
    await mouth.syncOnce();
    const made = await mouth.syncOnce();
    const receipt = await mouth.send({ requestId: "ok", persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: "hi" });
    assert.ok(!JSON.stringify([made, receipt, mouth.pendingAfter(0)]).includes(TOKEN));
    assert.ok(hs.seen.every(r => r.auth === `Bearer ${TOKEN}`), "the token is presented to the homeserver on every call");
  } finally { await hs.close(); }

  assert.ok(logs.length > 0, "failures were logged");
  assert.ok(logs.every(l => !l.includes(TOKEN)), `a log line carried the token: ${logs.find(l => l.includes(TOKEN))}`);
  assert.equal(redact(`x ${TOKEN} y`, TOKEN), "x [redacted] y");
});

test("a homeserver error is reported with its status and none of its body", async () => {
  const hs = await fakeHomeserver({ sync: script, failWith: 500 });
  try {
    await assert.rejects(mouthFor(hs).syncOnce(), err => /answered 500/.test(err.message) && !/bad token/.test(err.message));
  } finally { await hs.close(); }
});

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    if (name === ".git" || name === "node_modules" || name === "bin" || name === "obj") continue;
    const full = join(dir, name);
    statSync(full).isDirectory() ? walk(full, out) : out.push(full);
  }
  return out;
}

test("the Discord posting actuator is gone and the Matrix path names no Discord", () => {
  const root = resolve(import.meta.dirname, "..");
  for (const file of ["tools/persona-matrix-mouth.mjs", "tools/persona-chat-documents.mjs"]) {
    assert.ok(!/discord/i.test(readFileSync(resolve(root, file), "utf8")), `${file} mentions Discord`);
  }
  const survivors = walk(root).filter(f => /\.(mjs|cs|json|ya?ml|ps1|sh)$/.test(f) && /persona-discord-delivery/.test(readFileSync(f, "utf8")) && !/persona-matrix-mouth\.test\.mjs$/.test(f));
  assert.deepEqual(survivors, []);
  for (const gone of ["persona-discord-delivery.mjs", "persona-discord-rudp.mjs", "persona-discord-delivery-documents.mjs", "persona-discord-permit.mjs"]) {
    assert.throws(() => statSync(resolve(root, "tools", gone)));
  }
});

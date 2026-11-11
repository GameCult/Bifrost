import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { resolve, join } from "node:path";
import { existsSync, mkdtempSync, statSync as stat } from "node:fs";
import { tmpdir } from "node:os";
import { createHash } from "node:crypto";
import { createImageStore, RETAIN_MS } from "../tools/persona-chat-images.mjs";
import { personaStoreRoot } from "./image-bytes.mjs";
import * as docs from "../tools/persona-chat-documents.mjs";
import { gif, png, svg } from "./image-bytes.mjs";
import { createMouth, replyLinkFromEnv, pageOf, bodyText, createBrakeReader, guarded, redact, roomsFor, projectRoomsVariable } from "../tools/persona-matrix-mouth.mjs";

const TOKEN = "syt_SECRET_TOKEN_9f3";
const HS_NAME = "gamecult.test";
const ROOMS = { [`#swarm:${HS_NAME}`]: "!swarm:gamecult.test", [`#operator:${HS_NAME}`]: "!operator:gamecult.test" };
const SELF = `@eureka:${HS_NAME}`;

const message = (id, sender, body, ts) => ({ type: "m.room.message", event_id: id, sender, origin_server_ts: ts, content: { msgtype: "m.text", body } });
// The one write a round makes by design: her presence state (any state; the brake and the grace decide which).
const offlinePresence = r => r.method === "PUT" && r.path.includes("/presence/");
const tick = () => new Promise(r => setImmediate(r));

// A fake Matrix client-server API: just the endpoints the mouth uses.
// It is also the record of every room: events the sync script emits and messages sent are kept, and
// `preload` (room id -> events, oldest first) stands for history from before the mouth's process began.
async function fakeHomeserver({ sync, failWith, users, member, direct, preload = {}, failContext = false, media, failReceipts = false, deferReceipts = null, failPresence = 0 } = {}) {
  const seen = [];
  const record_ = new Map(Object.entries(preload).map(([room, events]) => [room, [...events]]));
  const remember = (room, event) => { const list = record_.get(room) ?? []; if (!list.some(e => e.event_id === event.event_id)) list.push(event); record_.set(room, list); };
  const sent = new Map(); // txnId -> event_id
  // The homeserver is answered in-process (no socket): the suite runs under a capped address space, where undici's HTTP client cannot start.
  const handler = (req, res) => {
    let raw = "";
    req.on("data", c => (raw += c));
    req.on("end", () => {
      const url = new URL(req.url, "http://x");
      const record = { method: req.method, rawPath: url.pathname, path: decodeURIComponent(url.pathname), query: url.searchParams, auth: req.headers.authorization, body: raw ? JSON.parse(raw) : null };
      seen.push(record);
      const reply = (status, body) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
      if (failWith) return reply(failWith, { errcode: "M_UNKNOWN_TOKEN", error: `bad token ${record.auth}` });
      const p = record.path.replace("/_matrix/client/v3", "").replace("/_matrix/client/v1", "");
      const bearer = (record.auth ?? "").replace("Bearer ", "");
      if (req.method === "POST" && p.includes("/receipt/m.read/")) {
        if (deferReceipts) return deferReceipts.then(() => reply(200, {}));
        return failReceipts ? reply(500, { errcode: "M_UNKNOWN", error: `down ${record.auth}` }) : reply(200, {});
      }
      if (req.method === "PUT" && p.includes("/presence/")) return failPresence-- > 0 ? reply(500, { errcode: "M_UNKNOWN" }) : reply(200, {});
      if (p.startsWith("/media/download/")) {
        const served = media?.(bearer, ...p.slice("/media/download/".length).split("/")) ?? { status: 404, body: "" };
        res.writeHead(served.status ?? 200, served.headers ?? {});
        return res.end(served.body);
      }
      if (p === "/account/whoami") return reply(200, { user_id: users?.[bearer] ?? SELF });
      if (p === "/joined_rooms") return reply(200, { joined_rooms: member ?? Object.values(ROOMS) });
      if (req.method === "PUT" && p.includes("/account_data/m.direct")) return reply(200, {});
      if (p.endsWith("/leave")) return reply(200, {});
      if (p.includes("/account_data/m.direct")) return direct ? reply(200, direct) : reply(404, { errcode: "M_NOT_FOUND" });
      if (p.startsWith("/directory/room/")) return reply(200, { room_id: ROOMS[p.slice("/directory/room/".length)] });
      if (p.startsWith("/join/")) return reply(200, { room_id: p.slice(6) });
      if (p === "/sync") {
        const out = sync(record.query.get("since"), bearer);
        for (const [room, r] of Object.entries(out.rooms?.join ?? {})) for (const e of r.timeline?.events ?? []) remember(room, e);
        return reply(200, out);
      }
      const ctx = p.match(/^\/rooms\/(.+?)\/(messages|event\/.+|relations\/.+)$/);
      if (ctx && req.method === "GET") {
        if (failContext) return reply(500, { errcode: "M_UNKNOWN", error: `down ${record.auth}` });
        const events = record_.get(ctx[1]) ?? [];
        const limit = Number(record.query.get("limit") ?? 10);
        const threadOf = e => (e.content?.["m.relates_to"]?.rel_type === "m.thread" ? e.content["m.relates_to"].event_id : null);
        const newestFirst = list => list.slice().reverse().slice(0, limit);
        if (ctx[2] === "messages") return reply(200, { chunk: newestFirst(events) });
        if (ctx[2].startsWith("event/")) { const e = events.find(x => x.event_id === ctx[2].slice(6)); return e ? reply(200, e) : reply(404, { errcode: "M_NOT_FOUND" }); }
        const root = ctx[2].match(/^relations\/(.+)\/m\.thread$/)?.[1];
        return reply(200, { chunk: newestFirst(events.filter(e => threadOf(e) === root)) });
      }
      const m = p.match(/^\/rooms\/(.+)\/send\/m\.room\.message\/(.+)$/);
      if (m && req.method === "PUT") {
        if (!sent.has(m[2])) { sent.set(m[2], `$event${sent.size + 1}`); remember(m[1], { type: "m.room.message", event_id: sent.get(m[2]), sender: users?.[bearer] ?? SELF, origin_server_ts: 5000 + sent.size, content: record.body }); }
        return reply(200, { event_id: sent.get(m[2]) });
      }
      reply(404, { errcode: "M_NOT_FOUND" });
    });
  };
  const fetch = (url, init = {}) => new Promise((resolve, reject) => {
    if (init.signal?.aborted) return reject(init.signal.reason);
    init.signal?.addEventListener("abort", () => reject(init.signal.reason), { once: true });
    const u = new URL(url);
    const req = Object.assign(new EventEmitter(), { method: init.method ?? "GET", url: u.pathname + u.search, headers: init.headers ?? {} });
    const res = {
      writeHead(status, headers) { this.status = status; this.headers = headers; },
      end(body) { resolve(new Response(body === "" ? null : body, { status: this.status, headers: this.headers })); },
    };
    handler(req, res);
    process.nextTick(() => { if (init.body !== undefined) req.emit("data", init.body); req.emit("end"); });
  });
  return {
    url: "http://hs.test",
    fetch,
    seen,
    puts: () => seen.filter(r => r.method === "PUT" && !offlinePresence(r)),
    receipts: () => seen.filter(r => r.path.includes("/receipt/")),
    presencePuts: () => seen.filter(r => r.path.includes("/presence/")),
    downloads: () => seen.filter(r => r.path.includes("/media/download/")),
    close: async () => {},
  };
}

function memoryState(initial = null) {
  const box = { value: initial };
  return { box, load: async () => box.value, save: async v => { box.value = v; } };
}

const joined = events => ({ rooms: { join: events } });
function mouthFor(hs, overrides = {}) {
  return createMouth({
    persona: "eureka", homeserver: hs.url, rooms: Object.keys(ROOMS), token: TOKEN, state: memoryState(), fetch: hs.fetch,
    serverName: HS_NAME, brake: async () => true, log: () => {}, syncTimeoutMs: 1, ...overrides,
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
  assert.equal(redact("a1 b2 a1", ["a1", "b2"]), "[redacted] [redacted] [redacted]");
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

// ---- one mouth, many Personas ----------------------------------------------------

const NOVA = `@nova:${HS_NAME}`;
const TOKEN_NOVA = "syt_OTHER_TOKEN_77x";

test("two_personas_two_queues: one message in a room both joined yields one delivery per Persona with separate seqs and only its own token", async () => {
  const both = since => since === null
    ? { next_batch: "b0", ...joined({ "!swarm:gamecult.test": { timeline: { events: [] } } }) }
    : { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: [message("$m1", `@alice:${HS_NAME}`, "hello both", 1000)] } } }) };
  const hs = await fakeHomeserver({ sync: both, users: { [TOKEN]: SELF, [TOKEN_NOVA]: NOVA } });
  try {
    const eureka = mouthFor(hs);
    const nova = mouthFor(hs, { persona: "nova", token: TOKEN_NOVA });
    await eureka.syncOnce(); await nova.syncOnce();
    await eureka.syncOnce();
    await eureka.syncOnce(); // a second delivery for eureka only
    await nova.syncOnce();
    assert.deepEqual(eureka.pendingAfter(0).map(d => d.seq), [1, 2]);
    assert.deepEqual(nova.pendingAfter(0).map(d => d.seq), [1], "separate queue, separate seq");
    await eureka.ack(2);
    assert.equal(eureka.pendingCount, 0);
    assert.equal(nova.pendingCount, 1, "one Persona's ack leaves the other's mail alone");
    for (const r of hs.seen) assert.ok([`Bearer ${TOKEN}`, `Bearer ${TOKEN_NOVA}`].includes(r.auth));
    const novaCalls = hs.seen.filter(r => r.auth === `Bearer ${TOKEN_NOVA}`);
    assert.ok(novaCalls.length >= 3 && novaCalls.every(r => !JSON.stringify(r).includes(TOKEN)), "a token never appears in another account's request");
  } finally { await hs.close(); }
});

test("a Persona's own words are not mail for it, but are for the other Persona", async () => {
  const hs = await fakeHomeserver({
    users: { [TOKEN]: SELF, [TOKEN_NOVA]: NOVA },
    sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: [message("$e", SELF, "eureka speaking", 5)] } } }) },
  });
  try {
    const eureka = mouthFor(hs);
    const nova = mouthFor(hs, { persona: "nova", token: TOKEN_NOVA });
    for (const m of [eureka, nova]) { await m.syncOnce(); await m.syncOnce(); }
    assert.equal(eureka.pendingCount, 0);
    assert.deepEqual(nova.pendingAfter(0).map(d => d.sender), [SELF]);
  } finally { await hs.close(); }
});

test("mentions_carried: m.mentions user ids and the exact sender id are on the delivery; absent or malformed mentions are empty", async () => {
  const withMentions = (id, mentions) => ({ ...message(id, `@meta:${HS_NAME}`, "hi", 7), content: { msgtype: "m.text", body: "hi", ...(mentions === undefined ? {} : { "m.mentions": mentions }) } });
  const hs = await fakeHomeserver({
    sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: [
      withMentions("$a", { user_ids: [SELF, NOVA] }), withMentions("$b", undefined), withMentions("$c", { user_ids: "nope" }), withMentions("$d", { room: true }),
    ] } } }) },
  });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    const made = await mouth.syncOnce();
    assert.deepEqual(made.map(d => d.mentions), [[SELF, NOVA], [], [], []]);
    assert.ok(made.every(d => d.sender === `@meta:${HS_NAME}`));
    assert.ok(made.every(d => d.isDirect === false));
  } finally { await hs.close(); }
});

test("dm_flag: a joined room the account marks in m.direct is delivered as a DM, answerable by its room id; other rooms are not", async () => {
  const DM = "!dm:gamecult.test";
  const hs = await fakeHomeserver({
    direct: { [`@meta:${HS_NAME}`]: [DM] },
    sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", account_data: { events: [{ type: "m.direct", content: { [`@meta:${HS_NAME}`]: [DM, "!dm2:gamecult.test"] } }] }, ...joined({
      [DM]: { timeline: { events: [message("$dm", `@meta:${HS_NAME}`, "private", 9)] } },
      "!dm2:gamecult.test": { timeline: { events: [message("$dm2", `@meta:${HS_NAME}`, "second", 10)] } },
      "!swarm:gamecult.test": { timeline: { events: [message("$pub", `@meta:${HS_NAME}`, "public", 11)] } },
    }) },
  });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    const made = await mouth.syncOnce();
    assert.deepEqual(made.map(d => [d.eventId, d.roomAlias, d.isDirect]), [["$dm", DM, true], ["$dm2", "!dm2:gamecult.test", true], ["$pub", `#swarm:${HS_NAME}`, false]]);
    assert.equal((await mouth.send({ requestId: "dm-1", persona: "eureka", roomAlias: DM, text: "reply" })).refusal, null);
    assert.equal(hs.puts().at(-1).path, `/_matrix/client/v3/rooms/${DM}/send/m.room.message/dm-1`);
  } finally { await hs.close(); }
});

test("pending_is_not_ack: repeated reads return the same deliveries until an ack", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce(); await mouth.syncOnce();
    const first = mouth.pendingAfter(0), second = mouth.pendingAfter(0);
    assert.deepEqual(first, second);
    assert.equal(first.length, 1);
    await mouth.ack(first[0].seq);
    assert.deepEqual(mouth.pendingAfter(0), []);
  } finally { await hs.close(); }
});

test("join_while_braked: no join and no send while the brake is held; joining happens on the round after release; a failing join is logged, redacted, and never throws", async () => {
  let open = false;
  const logs = [];
  const hs = await fakeHomeserver({ sync: script, member: [] });
  try {
    const mouth = mouthFor(hs, { brake: async () => open, log: l => logs.push(l) });
    await mouth.syncOnce();
    await mouth.send({ requestId: "r", persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: "speak" });
    assert.equal(hs.seen.filter(r => (r.method === "POST" || r.method === "PUT") && !offlinePresence(r)).length, 0, "braked: nothing is joined or sent");
    open = true;
    await mouth.syncOnce();
    assert.deepEqual(hs.seen.filter(r => r.method === "POST").map(r => r.path.split("/join/")[1]).sort(), ["!operator:gamecult.test", "!swarm:gamecult.test"]);
    await mouth.syncOnce();
    assert.equal(hs.seen.filter(r => r.method === "POST").length, 2, "joined rooms are not joined again");
  } finally { await hs.close(); }

  const failing = await fakeHomeserver({ sync: script, member: [] });
  try {
    const mouth = mouthFor(failing, { fetch: (url, init) => String(url).includes("/join/") ? Promise.reject(new Error(`join refused for ${init.headers.authorization}`)) : failing.fetch(url, init), log: l => logs.push(l) });
    await mouth.syncOnce(); // no throw: the unit does not crash-loop on a failed join
    assert.ok(logs.some(l => l.includes("not joined")) && logs.every(l => !l.includes(TOKEN)));
  } finally { await failing.close(); }
});

test("a freshly joined room's history is context, not mail; an already joined room's backlog still is mail after a restart", async () => {
  const hs = await fakeHomeserver({
    member: ["!operator:gamecult.test"],
    sync: since => since === "b0"
      ? { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: [message("$hist", `@alice:${HS_NAME}`, "before I joined", 1)] } }, "!operator:gamecult.test": { timeline: { events: [message("$mail", `@alice:${HS_NAME}`, "waiting for me", 2)] } } }) }
      : { next_batch: "b0" },
  });
  try {
    const mouth = mouthFor(hs, { state: memoryState({ nextBatch: "b0", seq: 4 }) });
    const made = await mouth.syncOnce();
    assert.deepEqual(made.map(d => d.eventId), ["$mail"]);
    assert.equal(made[0].seq, 5);
  } finally { await hs.close(); }
});

test("pending is bounded: at the bound the mouth stops reading, keeps its position, and resumes after an ack", async () => {
  const hs = await fakeHomeserver({ sync: since => ({ next_batch: `n${since}`, ...joined({ "!swarm:gamecult.test": { timeline: { events: [message(`$e${since}`, `@alice:${HS_NAME}`, "again", 5)] } } }) }) });
  try {
    const mouth = mouthFor(hs, { maxPending: 2 });
    await mouth.syncOnce(); // baseline
    await mouth.syncOnce(); await mouth.syncOnce();
    const syncs = () => hs.seen.filter(r => r.path.endsWith("/sync")).length;
    const before = syncs();
    assert.deepEqual(await mouth.syncOnce(), []);
    assert.equal(syncs(), before, "no /sync while the mailbox is at the bound");
    assert.equal(mouth.pendingCount, 2);
    await mouth.ack(2);
    assert.equal((await mouth.syncOnce()).length, 1);
  } finally { await hs.close(); }
});

test("guarded: a failure leaves with every secret redacted, however many tokens the process holds", async () => {
  await assert.rejects(guarded([TOKEN, TOKEN_NOVA], async () => { throw new Error(`boom ${TOKEN} then ${TOKEN_NOVA}`); }), err => err.message === "boom [redacted] then [redacted]");
  assert.equal(await guarded([TOKEN], async () => 5), 5);
});

test("the ring is gone: the mouth names no unit manager and spawns no child", () => {
  const source = readFileSync(resolve(import.meta.dirname, "..", "tools", "persona-matrix-mouth.mjs"), "utf8");
  assert.ok(!/systemctl|child_process|execFile|mind-persona@/.test(source));
});

test("delivery_poison_pill: a non-string display name or malformed event fields never reach a delivery; the sender falls back to its localpart", async () => {
  const hs = await fakeHomeserver({
    sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ "!swarm:gamecult.test": {
      state: { events: [{ type: "m.room.member", state_key: `@alice:${HS_NAME}`, content: { displayname: 123 } }] },
      timeline: { events: [
        message("$a", `@alice:${HS_NAME}`, "numeric name", "not-a-number"),
        { type: "m.room.message", event_id: "$b", sender: 5, content: { msgtype: "m.text", body: "numeric sender" } },
        { type: "m.room.message", sender: `@alice:${HS_NAME}`, content: { msgtype: "m.text", body: "no event id" } },
      ] },
    } }) },
  });
  try {
    const mouth = mouthFor(hs, { now: () => 42 });
    await mouth.syncOnce();
    const made = await mouth.syncOnce();
    assert.deepEqual(made.map(d => [d.eventId, d.senderDisplay, d.atUnixMs]), [["$a", "alice", 42]]);
  } finally { await hs.close(); }
});

test("delivery size: body, context lines and mentions are bounded per delivery, on a character boundary", async () => {
  const big = "é".repeat(30000);
  const hs = await fakeHomeserver({
    sync: since => since === null ? { next_batch: "b0", ...joined({ "!swarm:gamecult.test": { timeline: { events: [message("$h", `@alice:${HS_NAME}`, big, 1)] } } }) }
      : { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: [{ ...message("$n", `@bob:${HS_NAME}`, big, 2), content: { msgtype: "m.text", body: big, "m.mentions": { user_ids: Array.from({ length: 500 }, (_, i) => `@u${i}:x`) } } }] } } }) },
  });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    const [d] = await mouth.syncOnce();
    assert.ok(Buffer.byteLength(d.text) <= 4000 && d.text === "é".repeat(2000));
    assert.ok(d.context.every(c => Buffer.byteLength(c.text) <= 1000));
    assert.equal(d.mentions.length, 64);
  } finally { await hs.close(); }
});

test("bound_is_a_hard_cap: a round that would pass the cap enqueues only what fits, keeps its position, and the rest arrives once after an ack", async () => {
  const four = ["$w", "$x", "$y", "$z"].map((id, i) => message(id, `@alice:${HS_NAME}`, id, 10 + i));
  const state = memoryState();
  const hs = await fakeHomeserver({
    sync: since => since === null ? { next_batch: "b0" } : since === "b0" ? { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: four } } }) } : { next_batch: since },
  });
  try {
    const mouth = mouthFor(hs, { maxPending: 3, state });
    await mouth.syncOnce();
    assert.deepEqual((await mouth.syncOnce()).map(d => d.eventId), ["$w", "$x", "$y"]);
    assert.equal(mouth.pendingCount, 3, "never above the cap");
    assert.deepEqual(await mouth.syncOnce(), [], "full: no read");
    await mouth.ack(2);
    const rest = await mouth.syncOnce();
    assert.deepEqual(rest.map(d => d.eventId), ["$z"], "the four-message round is read again; the three already taken are not delivered twice");
    assert.deepEqual(mouth.pendingAfter(0).map(d => d.eventId), ["$y", "$z"]);
    assert.equal(state.box.value.nextBatch, "b0", "the position moves only once the whole round is acked");
    await mouth.ack(4);
    assert.equal(state.box.value.nextBatch, "b1");
  } finally { await hs.close(); }
});

// ---- DM invitations ----------------------------------------------------------------

const DMROOM = "!newdm:gamecult.test";
const invite = (inviter, isDirect = true) => ({ [DMROOM]: { invite_state: { events: [{ type: "m.room.member", state_key: SELF, sender: inviter, content: { membership: "invite", is_direct: isDirect } }] } } });
const inviteSync = (inviter, isDirect) => since => since === null ? { next_batch: "b0" }
  : since === "b0" ? { next_batch: "b1", rooms: { invite: invite(inviter, isDirect) } }
  : since === "b1" ? { next_batch: "b2", ...joined({ [DMROOM]: { timeline: { events: [message("$first", `@meta:${HS_NAME}`, "hello in private", 3)] } } }) }
  : { next_batch: since };
const joinsOf = hs => hs.seen.filter(r => r.method === "POST" && r.path.includes("/join/")).map(r => r.path.split("/join/")[1]);
const leavesOf = hs => hs.seen.filter(r => r.method === "POST" && r.path.endsWith("/leave"));

test("dm_invite_accepted: a same-server DM invitation is joined, remembered in m.direct, and its messages arrive as is_direct deliveries", async () => {
  const hs = await fakeHomeserver({ sync: inviteSync(`@meta:${HS_NAME}`) });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce(); await mouth.syncOnce();
    assert.deepEqual(joinsOf(hs), [DMROOM]);
    const saved = hs.puts().find(r => r.path.endsWith("/account_data/m.direct"));
    assert.deepEqual(saved.body, { [`@meta:${HS_NAME}`]: [DMROOM] });
    const [d] = await mouth.syncOnce();
    assert.deepEqual([d.eventId, d.roomAlias, d.isDirect, d.sender], ["$first", DMROOM, true, `@meta:${HS_NAME}`], "the message sent before the join is mail, not history");
    assert.equal((await mouth.send({ requestId: "dm", persona: "eureka", roomAlias: DMROOM, text: "hi" })).refusal, null);
  } finally { await hs.close(); }
});

test("dm_invite_waits_for_release: an invitation received while braked is not joined until the brake is released", async () => {
  let open = false;
  const hs = await fakeHomeserver({ sync: inviteSync(`@meta:${HS_NAME}`) });
  try {
    const mouth = mouthFor(hs, { brake: async () => open });
    await mouth.syncOnce(); await mouth.syncOnce(); await mouth.syncOnce();
    assert.deepEqual(hs.seen.filter(r => (r.method === "POST" || r.method === "PUT") && !offlinePresence(r)), [], "braked: no join, no leave, nothing written");
    open = true;
    await mouth.syncOnce();
    assert.deepEqual(joinsOf(hs), [DMROOM]);
  } finally { await hs.close(); }
});

test("dm_invite_other_server_declined: an invitation from another server is declined, never joined, brake or no brake", async () => {
  const hs = await fakeHomeserver({ sync: inviteSync("@mallory:evil.example") });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce(); await mouth.syncOnce();
    assert.deepEqual(joinsOf(hs), []);
    assert.deepEqual(leavesOf(hs).map(r => r.path), [`/_matrix/client/v3/rooms/${DMROOM}/leave`]);
    await mouth.syncOnce();
    assert.equal(leavesOf(hs).length, 1, "answered once");
  } finally { await hs.close(); }
  const lookalike = await fakeHomeserver({ sync: inviteSync(`@mallory:${HS_NAME}.evil.example`) });
  try {
    const mouth = mouthFor(lookalike);
    await mouth.syncOnce(); await mouth.syncOnce();
    assert.deepEqual(joinsOf(lookalike), [], "the server name must match exactly");
  } finally { await lookalike.close(); }
});

test("a same-server invitation to a room that is not a DM is neither joined nor declined", async () => {
  const hs = await fakeHomeserver({ sync: inviteSync(`@meta:${HS_NAME}`, false) });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce(); await mouth.syncOnce();
    assert.deepEqual([joinsOf(hs), leavesOf(hs)], [[], []]);
  } finally { await hs.close(); }
});

test("project rooms: slug mapping, per-Persona lists, absent variable, malformed alias", () => {
  assert.equal(projectRoomsVariable("eureka"), "MATRIX_ROOMS_EUREKA");
  assert.equal(projectRoomsVariable("nova-2.x"), "MATRIX_ROOMS_NOVA_2_X");
  const env = { MATRIX_ROOMS: "#swarm:t, #ops:t", MATRIX_ROOMS_EUREKA: "#eureka-proj:t,#ops:t", MATRIX_ROOMS_NOVA_2: "#nova-proj:t" };
  assert.deepEqual(roomsFor(env, "eureka"), ["#swarm:t", "#ops:t", "#eureka-proj:t"], "shared plus own, deduplicated");
  assert.deepEqual(roomsFor(env, "nova-2"), ["#swarm:t", "#ops:t", "#nova-proj:t"], "dash maps to underscore");
  assert.ok(!roomsFor(env, "nova-2").includes("#eureka-proj:t") && !roomsFor(env, "eureka").includes("#nova-proj:t"));
  assert.deepEqual(roomsFor(env, "other"), ["#swarm:t", "#ops:t"], "absent variable: shared rooms only");
  assert.throws(() => roomsFor({ ...env, MATRIX_ROOMS_EUREKA: "#ok:t,nope" }, "eureka"), /MATRIX_ROOMS_EUREKA.*malformed/);
});

test("send to another Persona's project room is refused as UnknownRoom", async () => {
  const hs = await fakeHomeserver({ sync: script });
  try {
    const env = { MATRIX_ROOMS: `#swarm:${HS_NAME}`, MATRIX_ROOMS_EUREKA: `#eureka-proj:${HS_NAME}`, MATRIX_ROOMS_NOVA: `#nova-proj:${HS_NAME}` };
    const mouth = mouthFor(hs, { rooms: roomsFor(env, "eureka") });
    assert.equal((await mouth.send({ requestId: "p", persona: "eureka", roomAlias: `#nova-proj:${HS_NAME}`, text: "x" })).refusal, "UnknownRoom");
    assert.equal(hs.puts().length, 0);
  } finally { await hs.close(); }
});

// Context is read from the homeserver, not remembered: these probes start the mouth with a
// room history the process never saw.
const ROOM = "!swarm:gamecult.test";
const DM = "!dm:gamecult.test";
const ALICE = `@alice:${HS_NAME}`;
const onlyAfterCursor = (roomId, events) => since => since === "c0" ? { next_batch: "c1", ...joined({ [roomId]: { timeline: { events } } }) } : { next_batch: since };
const persistedCursor = () => memoryState({ nextBatch: "c0", seq: 7 });

test("context_survives_restart: a mouth that never saw the room delivers with the exchange read back from the homeserver", async () => {
  const past = [message("$p1", ALICE, "first", 10), message("$p2", SELF, "my answer", 11), message("$p3", ALICE, "second", 12)];
  const hs = await fakeHomeserver({ preload: { [ROOM]: past }, sync: onlyAfterCursor(ROOM, [message("$n", ALICE, "now", 20)]) });
  try {
    const [d] = await mouthFor(hs, { state: persistedCursor() }).syncOnce();
    assert.deepEqual(d.context.map(c => [c.sender, c.text]), [[ALICE, "first"], [SELF, "my answer"], [ALICE, "second"]]);
    const asked = hs.seen.find(r => r.path.endsWith("/messages"));
    assert.equal(asked.query.get("dir"), "b");
    assert.equal(asked.auth, `Bearer ${TOKEN}`);
  } finally { await hs.close(); }
});

test("dm_context_holds_the_whole_exchange: her own earlier replies are in it, and it ends before the delivered message", async () => {
  const past = [message("$d1", ALICE, "hi", 1), message("$d2", SELF, "hello, Deru", 2), message("$d3", ALICE, "do you remember", 3)];
  const hs = await fakeHomeserver({ preload: { [DM]: past }, direct: { [ALICE]: [DM] }, member: [...Object.values(ROOMS), DM],
    sync: onlyAfterCursor(DM, [message("$d4", ALICE, "the thing", 4), message("$d5", ALICE, "after it", 5)]) });
  try {
    const made = await mouthFor(hs, { state: persistedCursor() }).syncOnce();
    assert.equal(made.length, 2);
    assert.ok(made[0].isDirect);
    assert.deepEqual(made[0].context.map(c => c.text), ["hi", "hello, Deru", "do you remember"]);
    assert.deepEqual(made[1].context.map(c => c.text), ["hi", "hello, Deru", "do you remember", "the thing"]);
    assert.equal(hs.seen.filter(r => r.path.endsWith("/messages")).length, 1, "one fetch serves every delivery of the room in a round");
  } finally { await hs.close(); }
});

test("thread_context_is_the_thread: root and replies before the delivered reply, not the room's other messages", async () => {
  const thread = { rel_type: "m.thread", event_id: "$root" };
  const inThread = (id, sender, body, ts) => ({ ...message(id, sender, body, ts), content: { msgtype: "m.text", body, "m.relates_to": thread } });
  const past = [message("$root", ALICE, "thread root", 1), message("$x", ALICE, "elsewhere in the room", 2), inThread("$t1", ALICE, "in thread", 3), inThread("$t2", SELF, "my thread reply", 4)];
  const hs = await fakeHomeserver({ preload: { [ROOM]: past }, sync: onlyAfterCursor(ROOM, [inThread("$t3", ALICE, "latest", 5)]) });
  try {
    const [d] = await mouthFor(hs, { state: persistedCursor() }).syncOnce();
    assert.deepEqual(d.context.map(c => c.text), ["thread root", "in thread", "my thread reply"]);
    assert.ok(hs.seen.some(r => r.path.includes("/relations/$root/m.thread")));
    assert.ok(!hs.seen.some(r => r.path.endsWith("/messages")));
  } finally { await hs.close(); }
});

test("context_byte_budget: the newest lines that fit 16 KiB, each clipped to a context line, at most 20", async () => {
  const wide = Array.from({ length: 40 }, (_, i) => message(`$w${i}`, ALICE, `${String(i).padStart(2, "0")}${"é".repeat(2000)}`, 10 + i));
  const hs = await fakeHomeserver({ preload: { [ROOM]: wide }, sync: onlyAfterCursor(ROOM, [message("$n", ALICE, "now", 99)]) });
  try {
    const [d] = await mouthFor(hs, { state: persistedCursor() }).syncOnce();
    const bytes = d.context.reduce((n, c) => n + Buffer.byteLength(c.text) + Buffer.byteLength(c.sender), 0);
    assert.ok(bytes <= 16 * 1024, `${bytes} bytes`);
    assert.ok(d.context.every(c => Buffer.byteLength(c.text) <= 1000));
    assert.ok(d.context.length > 0 && d.context.length < 20, "bytes, not count, bound it");
    assert.ok(d.context.at(-1).text.startsWith("39"), "the newest line is kept");
    assert.ok(d.context.at(0).text.startsWith(String(40 - d.context.length)), "the lines are contiguous to the newest");
    const short = Array.from({ length: 40 }, (_, i) => message(`$s${i}`, ALICE, `s${i}`, 10 + i));
    const hs2 = await fakeHomeserver({ preload: { [ROOM]: short }, sync: onlyAfterCursor(ROOM, [message("$n", ALICE, "now", 99)]) });
    try { assert.equal((await mouthFor(hs2, { state: persistedCursor() }).syncOnce())[0].context.length, 20, "the wire carries 20 lines"); } finally { await hs2.close(); }
  } finally { await hs.close(); }
});

test("context_fetch_failure: empty context, one logged line without the token, and the delivery still goes out", async () => {
  const lines = [];
  const hs = await fakeHomeserver({ failContext: true, preload: { [ROOM]: [message("$p", ALICE, "past", 1)] }, sync: onlyAfterCursor(ROOM, [message("$n", ALICE, "now", 9)]) });
  try {
    const [d] = await mouthFor(hs, { state: persistedCursor(), log: l => lines.push(l) }).syncOnce();
    assert.equal(d.text, "now");
    assert.deepEqual(d.context, []);
    const logged = lines.filter(l => l.includes("context for"));
    assert.equal(logged.length, 1);
    assert.ok(!logged[0].includes(TOKEN));
  } finally { await hs.close(); }
});

const media = (msgtype, content) => ({ msgtype, ...content });
const MXC_URL = "mxc://gamecult.test/AbC_123-x";

test("media_line: an image, file, audio or video is one typed line naming kind, filename, mime, size and mxc url", () => {
  const info = { mimetype: "image/png", size: 2048, w: 10, h: 10 };
  assert.equal(bodyText(media("m.image", { body: "cat.png", url: MXC_URL, info }), 4096), `[image filename="cat.png" mime=image/png size=2048 url=${MXC_URL}]`);
  assert.equal(bodyText(media("m.file", { body: "notes.pdf", url: MXC_URL, info: { mimetype: "application/pdf", size: 0 } }), 4096), `[file filename="notes.pdf" mime=application/pdf size=0 url=${MXC_URL}]`);
  assert.match(bodyText(media("m.audio", { body: "a.ogg", url: MXC_URL }), 4096), /^\[audio filename="a\.ogg" url=mxc:/);
  assert.match(bodyText(media("m.video", { body: "v.mp4", url: MXC_URL }), 4096), /^\[video filename="v\.mp4" url=mxc:/);
});

test("media_line: a caption follows the typed line only when the event names its file apart from the body", () => {
  const withCaption = bodyText(media("m.image", { body: "look at this", filename: "cat.png", url: MXC_URL }), 4096);
  assert.equal(withCaption, `[image filename="cat.png" url=${MXC_URL}]\nlook at this`);
  assert.equal(bodyText(media("m.image", { body: "cat.png", filename: "cat.png", url: MXC_URL }), 4096), `[image filename="cat.png" url=${MXC_URL}]`);
});

test("media_line: text, notice and emote bodies are untouched", () => {
  for (const msgtype of ["m.text", "m.notice", "m.emote", undefined]) assert.equal(bodyText({ msgtype, body: "plain words" }, 4096), "plain words");
});

test("media_line: a hostile field is quoted or left out, never printed raw; an encrypted attachment names its file url and never its key", () => {
  const line = bodyText(media("m.file", { body: 'a"]\n[image filename="x', url: "https://evil.example/x", info: { mimetype: "text/html\nx", size: -1 } }), 4096);
  assert.equal(line, '[file filename="a\\"]\\n[image filename=\\"x"]');
  assert.equal(line.includes("\n"), false);
  const sealed = bodyText(media("m.image", { body: "s.png", file: { url: MXC_URL, key: { k: "SECRET_KEY_MATERIAL" }, iv: "IV", hashes: { sha256: "H" } }, info: { mimetype: "image/png" } }), 4096);
  assert.equal(sealed, `[image filename="s.png" mime=image/png url=${MXC_URL} encrypted]`);
  assert.equal(sealed.includes("SECRET_KEY_MATERIAL"), false);
});

test("media_line: the line is bounded to the byte cap on a character boundary", () => {
  const long = bodyText(media("m.file", { body: "é".repeat(400), url: MXC_URL }), 100);
  assert.ok(Buffer.byteLength(long) <= 100);
  assert.equal(long.includes("�"), false);
});

test("media_delivery: a Matrix image reaches the delivery and a later delivery's context as the typed line, with no media fetch and no token in it", async () => {
  const img = { type: "m.room.message", event_id: "$img", sender: `@alice:${HS_NAME}`, origin_server_ts: 1000, content: media("m.image", { body: "cat.png", url: MXC_URL, info: { mimetype: "image/png", size: 2048 } }) };
  const after = message("$after", `@alice:${HS_NAME}`, "did you see it", 2000);
  const tl = {
    null: { next_batch: "b0", ...joined({ "!swarm:gamecult.test": { timeline: { events: [] } } }) },
    b0: { next_batch: "b1", ...joined({ "!swarm:gamecult.test": { timeline: { events: [img] } } }) },
    b1: { next_batch: "b2", ...joined({ "!swarm:gamecult.test": { timeline: { events: [after] } } }) },
  };
  const hs = await fakeHomeserver({ sync: since => tl[since] ?? { next_batch: since } });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    const [d] = await mouth.syncOnce();
    const line = `[image filename="cat.png" mime=image/png size=2048 url=${MXC_URL}]`;
    assert.equal(d.text, line);
    const [next] = await mouth.syncOnce();
    assert.equal(next.context.at(-1).text, line);
    assert.deepEqual(hs.seen.filter(r => r.path.includes("/media/") || r.path.includes("/download")), []);
    assert.equal(JSON.stringify(mouth.pendingAfter(0)).includes(TOKEN), false);
  } finally { await hs.close(); }
});

// ---- chat images ----

const IMG_ROOM = "!swarm:gamecult.test";
const sha = data => createHash("sha256").update(data).digest("hex");
const imageEvent = (id, content, extra = {}) => ({ type: "m.room.message", event_id: id, sender: `@alice:${HS_NAME}`, origin_server_ts: 1000, content: media("m.image", { body: "cat.png", url: MXC_URL, info: { mimetype: "image/png", size: 2048 }, ...content }), ...extra });
const imageTimeline = (events, room = IMG_ROOM) => ({
  null: { next_batch: "b0", ...joined({ [IMG_ROOM]: { timeline: { events: [] } } }) },
  b0: { next_batch: "b1", ...joined({ [room]: { timeline: { events } } }) },
});
const served = bytes => (bearer, server, id) => (bearer === TOKEN && server === "gamecult.test" && id === "AbC_123-x" ? { status: 200, body: bytes } : { status: 401, body: "" });
function imageSetup(clock = { t: Date.now() }) {
  const root = personaStoreRoot();
  return { root, clock, images: createImageStore({ root, now: () => clock.t }) };
}
async function receive(events, { media: mediaFn, room, images, mouthOptions = {} } = {}) {
  const tl = imageTimeline(events, room);
  const hs = await fakeHomeserver({ sync: since => tl[since] ?? { next_batch: since }, media: mediaFn });
  const mouth = mouthFor(hs, { images, ...mouthOptions });
  await mouth.syncOnce();
  const made = await mouth.syncOnce();
  return { hs, mouth, made };
}

test("image_delivery: an m.image event is fetched as this Persona, stored by hash, and the delivery carries the reference in the v3 tuple", async () => {
  const bytes = png(40, 30);
  const { root, images } = imageSetup();
  const { hs, made } = await receive([imageEvent("$img", {})], { media: served(bytes), images });
  try {
    assert.equal(made.length, 1);
    const [d] = made;
    assert.deepEqual(d.image, { path: `${sha(IMG_ROOM)}/${sha(bytes)}.png`, mime: "image/png", size: bytes.length, sha256: sha(bytes) });
    assert.ok(existsSync(join(root, d.image.path)));
    assert.deepEqual(hs.downloads().map(r => [r.method, r.path, r.auth]), [["GET", "/_matrix/client/v1/media/download/gamecult.test/AbC_123-x", `Bearer ${TOKEN}`]]);
    assert.equal(d.text, `[image filename="cat.png" mime=image/png size=2048 url=${MXC_URL}]`, "the text line is unchanged when the image is stored");
    const tuple = docs.deliveryTuple(d);
    assert.equal(tuple.length, 12);
    assert.deepEqual(tuple[11], [d.image.path, "image/png", bytes.length, sha(bytes)]);
    assert.ok(!d.image.path.startsWith("/") && !d.image.path.includes(".."));
    assert.equal(stat(join(root, d.image.path)).mode & 0o777, 0o640);
    assert.equal(stat(join(root, d.image.path, "..")).mode & 0o777, 0o750);
  } finally { await hs.close(); }
});

test("image_delivery: the file name is ours, never the sender's filename, body or info", async () => {
  const bytes = png(5, 5);
  const { root, images } = imageSetup();
  const { hs, made } = await receive([imageEvent("$img", { body: "../../etc/passwd", filename: "/abs/evil.sh", info: { mimetype: "text/x-shellscript", size: 1 } })], { media: served(bytes), images });
  try {
    assert.equal(made[0].image.path, `${sha(IMG_ROOM)}/${sha(bytes)}.png`);
    assert.equal(made[0].image.mime, "image/png", "the sniffed type, not the declared one");
    assert.ok(!JSON.stringify(made[0].image).includes("evil"));
  } finally { await hs.close(); }
});

test("image_delivery: only an event the Persona is delivered is fetched: not her own, not another room's, not history, not a plain message naming an mxc url", async () => {
  const { images } = imageSetup();
  const events = [
    imageEvent("$own", {}, { sender: SELF }),
    imageEvent("$x", {}),
    { ...message("$text", `@alice:${HS_NAME}`, `see ${MXC_URL} and https://evil.example/a.png`, 1100), content: { msgtype: "m.text", body: `see ${MXC_URL}` } },
  ];
  const tl = {
    null: { next_batch: "b0", ...joined({ [IMG_ROOM]: { timeline: { events: [imageEvent("$hist", {})] } } }) },
    b0: { next_batch: "b1", ...joined({ [IMG_ROOM]: { timeline: { events: [events[0], events[2]] } }, "!elsewhere:gamecult.test": { timeline: { events: [events[1]] } } }) },
  };
  const hs = await fakeHomeserver({ sync: since => tl[since] ?? { next_batch: since }, media: served(png()) });
  try {
    const mouth = mouthFor(hs, { images });
    await mouth.syncOnce();
    const made = await mouth.syncOnce();
    assert.deepEqual(made.map(d => d.eventId), ["$text"]);
    assert.equal(made[0].image, null);
    assert.deepEqual(hs.downloads(), []);
  } finally { await hs.close(); }
});

test("image_delivery: the url is the event's own content.url: a body or caption that names an mxc is not followed", async () => {
  const { images } = imageSetup();
  const { hs, made } = await receive([imageEvent("$img", { url: undefined, body: MXC_URL })], { media: served(png()), images });
  try {
    assert.equal(made[0].image, null);
    assert.match(made[0].text, /refused=no-url\]/);
    assert.deepEqual(hs.downloads(), []);
  } finally { await hs.close(); }
});

test("image_delivery: an encrypted attachment is never fetched, whether or not it also names a url", async () => {
  const { images } = imageSetup();
  const file = { url: MXC_URL, key: { k: "SECRET_KEY_MATERIAL" }, iv: "IV", hashes: { sha256: "H" } };
  const { hs, made } = await receive([imageEvent("$sealed", { url: undefined, file }), imageEvent("$both", { file }, { origin_server_ts: 1001 })], { media: served(png()), images });
  try {
    assert.deepEqual(made.map(d => d.image), [null, null]);
    assert.ok(made.every(d => d.text.includes("refused=encrypted") && !d.text.includes("SECRET_KEY_MATERIAL")));
    assert.deepEqual(hs.downloads(), []);
  } finally { await hs.close(); }
});

test("image_delivery: a refused or failed fetch gives a nil image and today's line with a fixed-word reason", async () => {
  const cases = [
    ["wrong-type", served(svg)],
    ["fetch-failed", () => ({ status: 404, body: "" })],
    ["too-big", (b, s, i) => ({ ...served(png())(b, s, i), headers: { "content-length": String(6 * 1024 * 1024) } })],
    ["too-many-pixels", served(png(9000, 9000))],
    ["bad-header", served(png(0, 0))],
  ];
  for (const [reason, mediaFn] of cases) {
    const { images } = imageSetup();
    const { hs, made } = await receive([imageEvent("$img", { filename: "evil\nrefused=fake" })], { media: mediaFn, images });
    try {
      assert.equal(made[0].image, null, reason);
      assert.equal(made[0].text.split("\n")[0].endsWith(`refused=${reason}]`), true, `${reason}: ${made[0].text}`);
      assert.ok(made[0].text.startsWith("[image filename="));
      assert.equal(docs.deliveryTuple(made[0])[11], null);
    } finally { await hs.close(); }
  }
});

test("image_delivery: with no image store the line is today's and nothing is fetched", async () => {
  const { hs, made } = await receive([imageEvent("$img", {})], { media: served(png()), images: null });
  try {
    assert.equal(made[0].image, null);
    assert.equal(made[0].text, `[image filename="cat.png" mime=image/png size=2048 url=${MXC_URL}]`);
    assert.deepEqual(hs.downloads(), []);
  } finally { await hs.close(); }
});

test("image_delivery: a stored image stays until its delivery is acked and it is 24 h old, and nothing but a reason word is logged", async () => {
  const bytes = gif(3, 3);
  const { root, clock, images } = imageSetup();
  const lines = [];
  const { hs, mouth, made } = await receive([imageEvent("$img", {})], { media: served(bytes), images, mouthOptions: { log: l => lines.push(l) } });
  try {
    const file = join(root, made[0].image.path);
    clock.t += RETAIN_MS * 2;
    images.sweep();
    assert.ok(existsSync(file), "a day old but unacked: kept");
    await mouth.ack(made[0].seq);
    assert.ok(!existsSync(file), "acked and a day old: gone");
    assert.deepEqual(lines.filter(l => l.includes(MXC_URL) || l.includes(root) || l.includes(sha(bytes))), []);
  } finally { await hs.close(); }
});

test("image_delivery: an acked image under 24 h stays", async () => {
  const { root, clock, images } = imageSetup();
  const { hs, mouth, made } = await receive([imageEvent("$img", {})], { media: served(png()), images });
  try {
    await mouth.ack(made[0].seq);
    clock.t += RETAIN_MS - 5000;
    images.sweep();
    assert.ok(existsSync(join(root, made[0].image.path)));
  } finally { await hs.close(); }
});

test("image_delivery: a store that cannot write leaves the line and a nil image, and the sync goes on", async () => {
  const dead = { put() { throw Object.assign(new Error("no space"), { code: "ENOSPC" }); }, hold() {}, acked() {} };
  const lines = [];
  const { hs, made } = await receive([imageEvent("$img", {})], { media: served(png()), images: dead, mouthOptions: { log: l => lines.push(l) } });
  try {
    assert.equal(made[0].image, null);
    assert.match(made[0].text, /refused=store-failed\]/);
    assert.ok(lines.some(l => l.includes("ENOSPC")));
  } finally { await hs.close(); }
});

// Read receipts and presence: Huginn's chat_read report posts her m.read; an ack posts nothing; her presence follows a
// turn and her brake.
const A = "!swarm:gamecult.test", B = "!operator:gamecult.test";
const receiptScript = since => since === null ? { next_batch: "b0", ...joined({}) } : since === "b0" ? {
  next_batch: "b1",
  ...joined({
    [A]: { timeline: { events: [message("$a1", `@alice:${HS_NAME}`, "one", 10), message("$a2", `@alice:${HS_NAME}`, "two", 11)] } },
    [B]: { timeline: { events: [message("$b1", `@bob:${HS_NAME}`, "three", 12)] } },
  }),
} : { next_batch: since };
const until = async check => { for (let i = 0; i < 200 && !check(); i++) await new Promise(r => setImmediate(r)); };
const withMail = async (hs, overrides) => {
  const mouth = mouthFor(hs, overrides);
  await mouth.syncOnce();
  assert.equal((await mouth.syncOnce()).map(d => d.seq).join(), "1,2,3");
  return mouth;
};
const receiptsOf = hs => hs.receipts().map(r => [r.path.split("/rooms/")[1].split("/receipt/")[0], r.path.split("/m.read/")[1], r.auth, r.body]);
const presencesOf = hs => hs.presencePuts().map(r => r.body.presence);
const syncsOf = hs => hs.seen.filter(r => r.path.endsWith("/sync")).map(r => r.query.get("set_presence"));
const reportRead = (mouth, roomAlias, eventId, persona = "eureka") => mouth.read({ persona, roomAlias, eventId, atUnixMs: 1 });

test("delivery, pending and an ack post no receipt: she has not read what the model has not seen", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs);
  for (const after of [0, 1, 2, 3]) mouth.pendingAfter(after);
  await mouth.ack(3);
  await new Promise(r => setTimeout(r, 30));
  assert.deepEqual(hs.receipts(), [], "mail handed to her queue, polled and acked: still no m.read");
  assert.equal(mouth.pendingCount, 0);
});

test("a read report posts m.read at exactly that event id, as that Persona", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs);
  assert.equal(await reportRead(mouth, A, "$a1"), null);
  assert.deepEqual(receiptsOf(hs), [[A, "$a1", `Bearer ${TOKEN}`, {}]], "the reported event, not the newest in the room");
  assert.equal(await reportRead(mouth, B, "$b1"), null);
  assert.deepEqual(receiptsOf(hs).slice(1), [[B, "$b1", `Bearer ${TOKEN}`, {}]]);
  assert.equal(hs.receipts().length, 2);
});

test("a read report for an event the mouth never delivered, or in another room, posts nothing", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs);
  assert.equal(await reportRead(mouth, A, "$made-up"), "UnknownEvent");
  assert.equal(await reportRead(mouth, B, "$a1"), "RoomMismatch");
  assert.deepEqual(hs.receipts(), []);
});

test("a read report never moves her receipt backward or repeats it in a room", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs);
  assert.equal(await reportRead(mouth, A, "$a2"), null);
  assert.equal(await reportRead(mouth, A, "$a1"), "Behind");
  assert.equal(await reportRead(mouth, A, "$a2"), "Behind");
  assert.equal(hs.receipts().length, 1);
});

test("a read report posts nothing while braked, and nothing is queued for the release", async () => {
  let open = false;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const lines = [];
  const mouth = await withMail(hs, { brake: async () => open, log: l => lines.push(l) });
  assert.equal(await reportRead(mouth, A, "$a1"), "Brake");
  assert.ok(lines.some(l => l.includes("receipt not posted: brake held")));
  open = true;
  await mouth.syncOnce();
  await new Promise(r => setTimeout(r, 20));
  assert.deepEqual(hs.receipts(), []);
  assert.equal(await reportRead(mouth, A, "$a1"), null, "a fresh report after the release posts");
});

test("a read report names her persona: another persona's report posts nothing", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs);
  assert.equal(await reportRead(mouth, A, "$a1", "someone-else"), "PersonaMismatch");
  assert.deepEqual(hs.receipts(), []);
});

test("the receipt goes to the delivered room id, not to the alias the report named", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs);
  assert.equal(await reportRead(mouth, `#swarm:${HS_NAME}`, "$a1"), null);
  assert.deepEqual(receiptsOf(hs), [[A, "$a1", `Bearer ${TOKEN}`, {}]]);
});

test("concurrent reports for one room are judged in arrival order: the newer lands and the older is behind", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs);
  const [newer, older] = await Promise.all([reportRead(mouth, A, "$a2"), reportRead(mouth, A, "$a1")]);
  assert.deepEqual([newer, older], [null, "Behind"]);
  assert.deepEqual(receiptsOf(hs).map(r => r[1]), ["$a2"], "never a1 after a2");
  const [first, second] = await Promise.all([reportRead(mouth, B, "$b1"), reportRead(mouth, B, "$b1")]);
  assert.deepEqual([first, second], [null, "Behind"], "the same event twice posts once");
});

test("a failed receipt leaves the last landed receipt in place: an older event is still behind", async () => {
  let receiptCalls = 0;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const fetch = (url, init) => String(url).includes("/receipt/") && ++receiptCalls === 2 ? Promise.resolve(new Response("{}", { status: 500 })) : hs.fetch(url, init);
  const mouth = await withMail(hs, { fetch });
  assert.equal(await reportRead(mouth, A, "$a1"), null);
  assert.equal(await reportRead(mouth, A, "$a2"), "Failed");
  assert.equal(await reportRead(mouth, A, "$a1"), "Behind", "a1 landed; the failure of a2 does not reopen the room to a backward post");
  assert.equal(await reportRead(mouth, A, "$a2"), null, "a2 itself can still land");
  assert.deepEqual(receiptsOf(hs).map(r => r[1]), ["$a1", "$a2"]);
});

test("a brake placed after the first brake read stops the receipt and no present state is PUT while braked", async () => {
  let used = 0, allowed = Infinity;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs, { brake: async () => used++ < allowed });
  used = 0; allowed = 1; // read() sees it released once; every read after that finds it held
  assert.equal(await reportRead(mouth, A, "$a1"), "Brake");
  assert.deepEqual(hs.receipts(), [], "the receipt re-reads the brake after the presence write");
  assert.ok(!presencesOf(hs).includes("online"), "online is never PUT on a stale brake read");
  assert.equal(presencesOf(hs).at(-1), "offline");
});
test("a slow presence write after a send holds no other send in the room", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  const fetch = (url, init) => String(url).includes("/presence/") && init?.method === "PUT"
    ? new Promise((_, reject) => init.signal.addEventListener("abort", () => reject(new Error("aborted")))) : hs.fetch(url, init);
  const mouth = await withMail(hs, { fetch, sendTimeoutMs: 400 });
  const send = id => mouth.send({ requestId: id, persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: id });
  void send("s1"); // its presence write hangs
  const second = await Promise.race([send("s2").then(r => r.refusal), new Promise(r => setTimeout(() => r("held behind the first send's presence write"), 150))]);
  assert.equal(second, null);
});

test("the grace ends exactly at its boundary: one millisecond inside is online, the boundary is unavailable", async () => {
  let t = 9_000_000;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs, { now: () => t, presenceGraceMs: 60_000 });
  await mouth.ack(3); // touched at t
  t += 59_999;
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "online");
  t += 1;
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "unavailable");
});

test("the state a poll carries is fixed before the poll: a grace that passes while it is open is PUT next round", async () => {
  let t = 3_000_000;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const fetch = async (url, init) => { const res = await hs.fetch(url, init); if (String(url).includes("/sync")) t += 100_000; return res; };
  const mouth = mouthFor(hs, { now: () => t, presenceGraceMs: 60_000, fetch });
  await mouth.ack(0); // a turn: online
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "online");
  await mouth.syncOnce();
  assert.deepEqual(presencesOf(hs), ["online", "unavailable"], "the homeserver was told online by the poll; the next round must tell it unavailable");
});

test("a failed receipt is logged once, not retried, and the next report for the room posts", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript, failReceipts: true });
  const lines = [];
  const mouth = await withMail(hs, { log: l => lines.push(l) });
  assert.equal(await reportRead(mouth, A, "$a1"), "Failed");
  const failures = lines.filter(l => l.startsWith("receipt in "));
  assert.equal(failures.length, 1);
  assert.ok(failures[0].includes("not posted") && !failures[0].includes(TOKEN));
  await new Promise(r => setTimeout(r, 20));
  assert.equal(hs.receipts().length, 1, "no retry");
  assert.equal(await reportRead(mouth, A, "$a1"), "Failed", "the claim was released: the room is not stuck behind a failed post");
});

test("a receipt that never answers is abandoned after sendTimeoutMs", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript, deferReceipts: new Promise(() => {}) });
  const lines = [];
  const mouth = await withMail(hs, { sendTimeoutMs: 40, log: l => lines.push(l) });
  assert.equal(await reportRead(mouth, A, "$a1"), "Failed");
  assert.ok(lines.some(l => l.startsWith("receipt in ") && l.includes("not posted")));
  assert.equal(await reportRead(mouth, B, "$b1"), "Failed", "the next room's receipt went out after the first was abandoned");
  assert.equal(hs.receipts().length, 2);
});

test("a throwing logger on a failed receipt causes no unhandled rejection", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript, failReceipts: true });
  const mouth = await withMail(hs, { log: () => { throw new Error("logger down"); } });
  const rejected = [];
  const note = e => rejected.push(e);
  process.on("unhandledRejection", note);
  try {
    await reportRead(mouth, A, "$a1").catch(() => {});
    await new Promise(r => setTimeout(r, 50));
  } finally { process.off("unhandledRejection", note); }
  assert.deepEqual(rejected, []);
});

test("the receipt path is percent-encoded on the wire", async () => {
  const ROOM = "!od d/room:gamecult.test", EVENT = "$ev/with+odd:chars";
  const hs = await fakeHomeserver({ sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", account_data: { events: [{ type: "m.direct", content: { [`@meta:${HS_NAME}`]: [ROOM] } }] }, ...joined({ [ROOM]: { timeline: { events: [message(EVENT, `@meta:${HS_NAME}`, "x", 9)] } } }) } });
  const mouth = mouthFor(hs);
  await mouth.syncOnce(); await mouth.syncOnce();
  assert.equal(await reportRead(mouth, ROOM, EVENT), null);
  assert.equal(hs.receipts()[0].rawPath, `/_matrix/client/v3/rooms/${encodeURIComponent(ROOM)}/receipt/m.read/${encodeURIComponent(EVENT)}`);
});

test("presence: unavailable while released and idle, online through a turn, unavailable after the grace", async () => {
  let t = 1_000_000;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs, { now: () => t, presenceGraceMs: 60_000 });
  assert.deepEqual(syncsOf(hs), ["unavailable", "unavailable"], "mail alone does not make her present");
  assert.deepEqual(presencesOf(hs), ["unavailable"], "one PUT, not one per round");
  await reportRead(mouth, A, "$a1");
  assert.deepEqual(presencesOf(hs), ["unavailable", "online"], "the report puts her online at once, not at the next poll");
  t += 59_000;
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "online", "inside the grace");
  t += 2_000;
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "unavailable", "past the grace");
  assert.deepEqual(presencesOf(hs), ["unavailable", "online", "unavailable"]);
});

test("a send or an ack within the grace keeps her online", async () => {
  let t = 5_000_000;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs, { now: () => t, presenceGraceMs: 60_000 });
  await mouth.ack(3);
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "online", "an ack is a sign of a turn");
  t += 61_000;
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "unavailable");
  const sent = await mouth.send({ requestId: "p1", persona: "eureka", roomAlias: `#swarm:${HS_NAME}`, text: "hi" });
  assert.equal(sent.refusal, null);
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "online", "a send is a sign of a turn");
});

test("presence under the brake is offline whatever the turn: one PUT on the transition, and a report cannot lift it", async () => {
  let open = true;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = await withMail(hs, { brake: async () => open });
  await reportRead(mouth, A, "$a1");
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "online");
  open = false;
  await mouth.syncOnce(); await mouth.syncOnce(); await mouth.syncOnce();
  assert.deepEqual(syncsOf(hs).slice(-3), ["offline", "offline", "offline"]);
  assert.equal(presencesOf(hs).at(-1), "offline");
  assert.equal(presencesOf(hs).filter(p => p === "offline").length, 1, "exactly one offline PUT");
  assert.equal(hs.presencePuts().at(-1).rawPath, `/_matrix/client/v3/presence/${encodeURIComponent(SELF)}/status`, "the user id is percent-encoded on the wire");
  assert.equal(await reportRead(mouth, B, "$b1"), "Brake");
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "offline");
  open = true;
  await mouth.syncOnce();
  assert.equal(syncsOf(hs).at(-1), "online", "still inside the grace of the earlier report");
});

test("the first braked round of a fresh mouth PUTs offline once; a failed PUT is tried again next round", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript, failPresence: 1 });
  const lines = [];
  const mouth = mouthFor(hs, { brake: async () => false, log: l => lines.push(l) });
  await mouth.syncOnce();
  assert.equal(hs.presencePuts().length, 1);
  assert.ok(lines.some(l => l.startsWith("presence not set")), "one log line");
  await mouth.syncOnce();
  assert.equal(hs.presencePuts().length, 2, "retried the next round");
  await mouth.syncOnce();
  assert.equal(hs.presencePuts().length, 2, "then settled");
});

test("two Personas on one homeserver: receipts and presence carry only her own bearer and user id", async () => {
  const ALICE = `@pa:${HS_NAME}`, BOB = `@pb:${HS_NAME}`;
  const hs = await fakeHomeserver({ sync: receiptScript, users: { tokA: ALICE, tokB: BOB } });
  const a = await withMail(hs, { persona: "pa", token: "tokA" });
  let bOpen = false;
  const b = await withMail(hs, { persona: "pb", token: "tokB", brake: async () => bOpen });
  await reportRead(a, A, "$a1", "pa");
  assert.equal(await reportRead(b, A, "$a1", "pb"), "Brake");
  assert.ok(hs.receipts().every(r => r.auth === "Bearer tokA"), "only A read");
  bOpen = true; // her receipts go out only while released
  assert.equal(await reportRead(b, A, "$a1", "pb"), null);
  assert.deepEqual([...new Set(hs.receipts().map(r => r.auth))].sort(), ["Bearer tokA", "Bearer tokB"]);
  assert.ok(hs.presencePuts().some(r => r.path.endsWith(`/presence/${BOB}/status`) && r.auth === "Bearer tokB"));
  assert.ok(hs.presencePuts().filter(r => r.path.endsWith(`/presence/${BOB}/status`)).every(r => r.auth === "Bearer tokB"));
});

// ---- review fixes ------------------------------------------------------------------

test("a brake placed during the long-poll stops the join the poll's invite asks for", async () => {
  let open = true;
  const script = inviteSync(`@meta:${HS_NAME}`);
  const hs = await fakeHomeserver({ sync: since => { const out = script(since); if (since === "b0") open = false; return out; } }); // the brake lands while this poll is open
  const mouth = mouthFor(hs, { brake: async () => open });
  await mouth.syncOnce(); // baseline, released
  await mouth.syncOnce(); // the poll that carries the invite
  assert.deepEqual(joinsOf(hs), [], "no join");
  assert.equal(hs.seen.filter(r => r.path.endsWith("/account_data/m.direct") && r.method === "PUT").length, 0, "no m.direct write");
});

test("a full backlog still sets presence from the brake", async () => {
  let open = true;
  const hs = await fakeHomeserver({ sync: receiptScript });
  const mouth = mouthFor(hs, { brake: async () => open, maxPending: 3 });
  await mouth.syncOnce();
  assert.equal((await mouth.syncOnce()).length, 3);
  const syncsBefore = hs.seen.filter(r => r.path.endsWith("/sync")).length;
  open = false;
  assert.deepEqual(await mouth.syncOnce(), [], "backlog full: no /sync");
  assert.equal(hs.seen.filter(r => r.path.endsWith("/sync")).length, syncsBefore);
  assert.deepEqual(hs.presencePuts().map(r => r.body), [{ presence: "unavailable" }, { presence: "offline" }]);
});

test("a brake placed during the first of two joins stops the second join and its m.direct write", async () => {
  const DM2 = "!newdm2:gamecult.test";
  const invites2 = { ...invite(`@meta:${HS_NAME}`), [DM2]: invite(`@meta:${HS_NAME}`)[DMROOM] };
  const hs = await fakeHomeserver({ sync: since => since === null ? { next_batch: "b0" } : since === "b0" ? { next_batch: "b1", rooms: { invite: invites2 } } : { next_batch: since } });
  let open = true;
  const fetch = async (url, init) => { const res = await hs.fetch(url, init); if (init?.method === "POST" && String(url).includes("/join/")) open = false; return res; }; // the brake lands during the first join
  const mouth = mouthFor(hs, { fetch, brake: async () => open });
  await mouth.syncOnce(); await mouth.syncOnce();
  assert.deepEqual(joinsOf(hs), [DMROOM], "only the join already started");
  assert.equal(hs.seen.filter(r => r.method === "PUT" && r.path.endsWith("/account_data/m.direct")).length, 1, "one m.direct, for the first join");
  open = true;
  await mouth.syncOnce();
  assert.deepEqual(joinsOf(hs), [DMROOM, DM2], "the second waits for the release");
});

test("chat_read round-trips and refuses a malformed tuple", () => {
  const tuple = docs.readTuple({ persona: "eureka", roomAlias: "#swarm:gamecult.test", eventId: "$e1", atUnixMs: 12 });
  assert.deepEqual(tuple, ["eureka", "#swarm:gamecult.test", "$e1", 12]);
  assert.deepEqual(docs.parseRead(tuple), { persona: "eureka", roomAlias: "#swarm:gamecult.test", eventId: "$e1", atUnixMs: 12 });
  assert.throws(() => docs.parseRead(["eureka", "#r", "", 1]), /event_id/);
  assert.throws(() => docs.parseRead(["eureka", "#r", "$e", -1]), /at_unix_ms/);
  assert.throws(() => docs.parseRead(["eureka", "#r", "$e"]));
});

test("a read report that throws does not wedge the room: the next report still posts and nothing is left unhandled", async () => {
  const hs = await fakeHomeserver({ sync: receiptScript });
  let boom = false;
  const fetch = (url, init) => boom && String(url).includes("/presence/") ? Promise.reject(new Error("net")) : hs.fetch(url, init);
  const mouth = await withMail(hs, { fetch, log: line => { if (boom && line.startsWith("presence not set")) throw new Error("logger down"); } }); // the presence failure and a throwing logger make run() reject
  const unhandled = [], note = error => unhandled.push(String(error));
  process.on("unhandledRejection", note);
  try {
    boom = true;
    await assert.rejects(reportRead(mouth, A, "$a1"), /logger down/);
    boom = false;
    assert.equal(await reportRead(mouth, A, "$a2"), null, "the room's next report is judged and posted");
    await new Promise(resolve => setTimeout(resolve, 50));
  } finally { process.off("unhandledRejection", note); }
  assert.deepEqual(unhandled, [], "no rejection escapes the tail's cleanup");
  assert.equal(hs.receipts().length, 1);
});

// Addressing facts (docs/bridge.md, "Addressing facts"): why a delivery is for this Persona, as the heartbeat reads it.
// Each case is one message as a room member sends it, with the basis and form the mouth hands the consumer.
const META = `@meta:${HS_NAME}`;
const withContent = (id, sender, content, ts = 3000) => ({ type: "m.room.message", event_id: id, sender, origin_server_ts: ts, content: { msgtype: "m.text", ...content } });
async function deliver(events, { persona = {}, preload } = {}) {
  const hs = await fakeHomeserver({
    preload,
    sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events } } }) },
  });
  try {
    const mouth = mouthFor(hs, persona);
    await mouth.syncOnce();
    return { made: await mouth.syncOnce(), hs, mouth };
  } finally { await hs.close(); }
}
const facts = made => made.map(d => [d.eventId, d.basis, d.form]);

test("addressing facts: the operator speaking untagged proves nothing, tagged proves a tag, and the tag outranks @room", async () => {
  const { made } = await deliver([
    withContent("$plain", META, { body: "everyone, status?" }),
    withContent("$tag", META, { body: "eureka, status?", "m.mentions": { user_ids: [SELF] } }),
    withContent("$both", META, { body: "all hands", "m.mentions": { user_ids: [SELF], room: true } }),
    withContent("$room", META, { body: "@room standup", "m.mentions": { room: true } }),
  ]);
  assert.deepEqual(facts(made), [["$plain", null, "message"], ["$tag", "tag", "message"], ["$both", "tag", "message"], ["$room", "room", "message"]]);
});

test("addressing facts: a tag of another Mind is no basis for this one, and is one for that one", async () => {
  const hs = await fakeHomeserver({
    users: { [TOKEN]: SELF, [TOKEN_NOVA]: NOVA },
    sync: since => since === null ? { next_batch: "b0", ...joined({ [ROOM]: { timeline: { events: [] } } }) } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events: [withContent("$n", META, { body: "nova?", "m.mentions": { user_ids: [NOVA] } })] } } }) },
  });
  try {
    const eureka = mouthFor(hs);
    const nova = mouthFor(hs, { persona: "nova", token: TOKEN_NOVA });
    for (const m of [eureka, nova]) await m.syncOnce();
    assert.deepEqual([facts(await eureka.syncOnce()), facts(await nova.syncOnce())], [[["$n", null, "message"]], [["$n", "tag", "message"]]]);
  } finally { await hs.close(); }
});

test("addressing facts: a name in the text is not a basis; the mouth does not know names, so every casing and alias reaches the heartbeat with null", async () => {
  const bodies = ["Eureka, look", "EUREKA", "eureka's PR", "@eureka:gamecult.test please", "Bifrost needs a look"];
  const { made } = await deliver(bodies.map((body, i) => withContent(`$n${i}`, META, { body })));
  assert.deepEqual(made.map(d => d.basis), [null, null, null, null, null]);
  assert.deepEqual(made.map(d => d.text), bodies, "the text is delivered whole for the heartbeat's own name match");
});

test("addressing facts: a DM is direct whatever it says or carries, a notice in it included", async () => {
  const DM = "!dm:gamecult.test";
  const hs = await fakeHomeserver({
    direct: { [META]: [DM] },
    sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [DM]: { timeline: { events: [withContent("$dm", META, { body: "hi" }), withContent("$dmn", META, { msgtype: "m.notice", body: "automated" })] } } }) },
  });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    assert.deepEqual(facts(await mouth.syncOnce()), [["$dm", "direct", "message"], ["$dmn", "direct", "notice"]]);
  } finally { await hs.close(); }
});

test("addressing facts: a notice that tags her carries the tag and the notice form, so the consumer can refuse to wake on it", async () => {
  const { made } = await deliver([withContent("$w", `@root-watch:${HS_NAME}`, { msgtype: "m.notice", body: "disk 91%", "m.mentions": { user_ids: [SELF] } })]);
  assert.deepEqual(facts(made), [["$w", "tag", "notice"]]);
});

test("addressing facts: an edit is its own form and is never read as a new message", async () => {
  const { made } = await deliver([withContent("$e", META, { body: "* fixed", "m.new_content": { msgtype: "m.text", body: "fixed" }, "m.relates_to": { rel_type: "m.replace", event_id: "$orig" } })]);
  assert.deepEqual(facts(made), [["$e", null, "edit"]]);
});

test("addressing facts: a reply to her own words is a reply, in a room and in a thread, and her Self run's words are hers", async () => {
  const mine = withContent("$mine", SELF, { body: "my earlier say" }, 1000);
  const theirs = withContent("$theirs", `@nibu:${HS_NAME}`, { body: "someone else's" }, 1001);
  const reply = to => ({ "m.relates_to": { "m.in_reply_to": { event_id: to } } });
  const thread = (root, fallback) => ({ "m.relates_to": { rel_type: "m.thread", event_id: root, "m.in_reply_to": { event_id: fallback }, is_falling_back: true } });
  const { made } = await deliver([
    withContent("$r1", META, { body: "yes", ...reply("$mine") }),
    withContent("$r2", META, { body: "no", ...reply("$theirs") }),
    withContent("$r3", META, { body: "in thread under mine", ...thread("$mine", "$theirs") }),
    withContent("$r4", META, { body: "in thread under theirs", ...thread("$theirs", "$theirs") }),
    withContent("$r5", META, { body: "reply, and tagged", ...reply("$mine"), "m.mentions": { user_ids: [SELF] } }),
  ], { preload: { [ROOM]: [mine, theirs] } });
  assert.deepEqual(facts(made), [["$r1", "reply", "message"], ["$r2", null, "message"], ["$r3", "reply", "message"], ["$r4", null, "message"], ["$r5", "tag", "message"]]);
});

test("addressing facts: a reply target the homeserver will not give is null and logged without the token, never a guess", async () => {
  const logs = [];
  const { made } = await deliver([withContent("$r", META, { body: "re", "m.relates_to": { "m.in_reply_to": { event_id: "$gone" } } })], { persona: { log: l => logs.push(l) } });
  assert.deepEqual(facts(made), [["$r", null, "message"]]);
  assert.ok(logs.some(l => l.includes("reply target $gone not read")) && logs.every(l => !l.includes(TOKEN)));
});

test("addressing facts: a client that sends no m.mentions is a tag when its body links her matrix.to pill; one that sends m.mentions without her is not", async () => {
  const pill = id => `<a href="https://matrix.to/#/${id}">Eureka</a>: look`;
  const { made } = await deliver([
    withContent("$p1", META, { body: "Eureka: look", format: "org.matrix.custom.html", formatted_body: pill(SELF) }),
    withContent("$p2", META, { body: "Eureka: look", format: "org.matrix.custom.html", formatted_body: pill(encodeURIComponent(SELF)) }),
    withContent("$p3", META, { body: "Nova: look", format: "org.matrix.custom.html", formatted_body: pill(NOVA) }),
    withContent("$p4", META, { body: "Eureka: look", format: "org.matrix.custom.html", formatted_body: pill(SELF), "m.mentions": {} }),
  ]);
  assert.deepEqual(made.map(d => d.basis), ["tag", "tag", null, null]);
});

test("addressing facts: her own messages are no delivery at all, so no basis can wake her on her own words", async () => {
  const { made } = await deliver([withContent("$self", SELF, { body: "my own", "m.mentions": { user_ids: [SELF] } })]);
  assert.deepEqual(made, []);
});

test("addressing facts: the v3 answer carries basis and form; the v2 answer a v1 request gets is the unchanged 12 fields", async () => {
  const { made } = await deliver([withContent("$t", META, { body: "x", "m.mentions": { user_ids: [SELF] } })]);
  const pack = { encode: v => Buffer.from(JSON.stringify(v)) };
  assert.equal(docs.parsePendingV3(pageOf("eureka", made, pack, () => {}, true)).deliveries[0].basis, "tag");
  const v2 = pageOf("eureka", made, pack);
  assert.equal(v2[1][0].length, 12);
  assert.equal(docs.parsePending(v2).deliveries[0].basis, undefined);
  assert.throws(() => docs.parseDeliveryV3([...docs.deliveryTuple(made[0]), "tag", "poem"]), /unknown delivery form/);
  assert.throws(() => docs.parseDeliveryV3([...docs.deliveryTuple(made[0]), "everyone", "message"]), /unknown delivery basis/);
  assert.throws(() => docs.parseDeliveryV3(docs.deliveryTuple(made[0])), /14-field/);
});

test("addressing facts: a hit read for a reply in one room is not lent to a reply in another room, and a target the server places elsewhere is null", async () => {
  const mine = { ...withContent("$mine", SELF, { body: "mine" }, 1000), room_id: ROOM };
  const reply = id => withContent(id, META, { body: "re", "m.relates_to": { "m.in_reply_to": { event_id: "$mine" } } });
  const hs = await fakeHomeserver({
    preload: { [ROOM]: [mine], "!operator:gamecult.test": [{ ...mine, room_id: "!elsewhere:gamecult.test" }] },
    sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events: [reply("$prime")] } }, "!operator:gamecult.test": { timeline: { events: [reply("$spoof")] } } }) },
  });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    assert.deepEqual(facts(await mouth.syncOnce()), [["$prime", "reply", "message"], ["$spoof", null, "message"]]);
  } finally { await hs.close(); }
});

test("addressing facts: a pill is her exact matrix.to anchor: a longer id, a non-link attribute and a quoted reply are no tag", async () => {
  const html = h => ({ body: "x", format: "org.matrix.custom.html", formatted_body: h });
  const { made } = await deliver([
    withContent("$a", META, html(`<a href="https://matrix.to/#/${SELF}.evil.org">x</a>`)),
    withContent("$b", META, html(`<span data-x="https://matrix.to/#/${SELF}">x</span>`)),
    withContent("$c", META, html(`<a class="x" href='https://matrix.to/#/${SELF}'>Eureka</a>`)),
    withContent("$d", META, html(`<mx-reply><blockquote><a href="https://matrix.to/#/${SELF}">her</a> said</blockquote></mx-reply>ok`)),
  ]);
  assert.deepEqual(made.map(d => d.basis), [null, null, "tag", null]);
});

test("addressing facts: an edit or a notice replying to her costs no target fetch", async () => {
  const to = id => ({ "m.relates_to": { "m.in_reply_to": { event_id: id } } });
  const { made, hs } = await deliver([
    withContent("$n", META, { msgtype: "m.notice", body: "n", ...to("$noticed") }),
    withContent("$e", META, { body: "* e", "m.relates_to": { rel_type: "m.replace", event_id: "$orig", "m.in_reply_to": { event_id: "$edited" } } }),
  ]);
  assert.deepEqual(facts(made), [["$n", null, "notice"], ["$e", null, "edit"]]);
  assert.equal(hs.seen.filter(r => r.path.includes("/event/")).length, 0);
});

test("addressing facts: a target that failed is not refetched within a minute, and is after it", async () => {
  let t = 1_000_000;
  const rel = { "m.relates_to": { "m.in_reply_to": { event_id: "$gone" } } };
  const round = id => joined({ [ROOM]: { timeline: { events: [withContent(id, META, { body: id, ...rel })] } } });
  const script = { null: { next_batch: "b0" }, b0: { next_batch: "b1", ...round("$r1") }, b1: { next_batch: "b2", ...round("$r2") }, b2: { next_batch: "b3", ...round("$r3") } };
  const hs = await fakeHomeserver({ sync: since => script[since] ?? { next_batch: since } });
  const fetched = () => hs.seen.filter(r => r.path.includes("/event/")).length;
  try {
    const mouth = mouthFor(hs, { now: () => t });
    await mouth.syncOnce(); await mouth.syncOnce();
    t += 59_000; await mouth.syncOnce();
    assert.equal(fetched(), 1, "inside the minute: not refetched");
    t += 2_000; await mouth.syncOnce();
    assert.equal(fetched(), 2, "past the minute: read again");
  } finally { await hs.close(); }
});

// The room tail (docs/bridge.md, "Room tail"): her rooms' recent talk past her ack cursor, bounded, as data on the v3 answer.
const OPERATOR = "!operator:gamecult.test";
const lines = (room, n, from = 0, sender = META) => Array.from({ length: n }, (_, i) => message(`$${room.slice(1, 4)}${from + i}`, sender, `line ${from + i}`, 10 + from + i));
async function tailed({ preload, direct, member, events = [], act = async () => {} } = {}) {
  const hs = await fakeHomeserver({ preload, direct, member, sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined(events.length ? { [ROOM]: { timeline: { events } } } : {}) } });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    await act(mouth);
    const read = await mouth.tail();
    return { tail: read.lines, gaps: read.gaps, rooms: read.rooms, mouth, hs };
  } finally { await hs.close(); }
}

test("room tail: 600 lines in a room keep the newest 20, oldest first", async () => {
  const { tail } = await tailed({ preload: { [ROOM]: lines(ROOM, 600) } });
  assert.equal(tail.length, 20);
  assert.deepEqual(tail.map(l => l.text), Array.from({ length: 20 }, (_, i) => `line ${580 + i}`));
  assert.deepEqual(Object.keys(tail[0]).sort(), ["atUnixMs", "basis", "eventId", "form", "roomAlias", "sender", "text"]);
  assert.equal(tail[0].roomAlias, `#swarm:${HS_NAME}`);
  assert.equal(tail[0].sender, META);
  assert.equal(tail[0].form, "message");
  assert.equal(tail[0].basis, null, "unproven, not 'not for her'");
});

test("room tail: a quiet room, with nothing delivered, still returns its recent lines", async () => {
  const { tail } = await tailed({ preload: { [OPERATOR]: lines(OPERATOR, 3) } });
  assert.deepEqual(tail.map(l => l.text), ["line 0", "line 1", "line 2"]);
  assert.ok(tail.every(l => l.roomAlias === `#operator:${HS_NAME}`));
});

test("room tail: a room she is not in the speaking set of is absent, and a DM she holds is present with basis direct", async () => {
  const STRANGER = "!stranger:gamecult.test", DM = "!mydm:gamecult.test";
  const { tail } = await tailed({
    preload: { [STRANGER]: lines(STRANGER, 2), [DM]: lines(DM, 2), [ROOM]: lines(ROOM, 1) },
    member: [...Object.values(ROOMS), STRANGER, DM],
    direct: { [META]: [DM] },
  });
  assert.deepEqual([...new Set(tail.map(l => l.roomAlias))].sort(), [DM, `#swarm:${HS_NAME}`]);
  assert.ok(tail.filter(l => l.roomAlias === DM).every(l => l.basis === "direct"));
  assert.ok(!tail.some(l => l.eventId.startsWith("$str")));
});

test("room tail: about 60 lines over all rooms, the newest, whatever the room count", async () => {
  const dms = ["!d1:gamecult.test", "!d2:gamecult.test", "!d3:gamecult.test", "!d4:gamecult.test"];
  const preload = { [ROOM]: lines(ROOM, 30, 0), [OPERATOR]: lines(OPERATOR, 30, 30) };
  dms.forEach((dm, i) => { preload[dm] = lines(dm, 30, 60 + 30 * i); });
  const { tail } = await tailed({ preload, direct: { [META]: dms }, member: [...Object.values(ROOMS), ...dms] });
  assert.equal(tail.length, 60);
  assert.deepEqual(tail.map(l => l.atUnixMs), tail.map(l => l.atUnixMs).sort((a, b) => a - b), "oldest first");
  assert.equal(Math.min(...tail.map(l => l.atUnixMs)), 110, "the oldest kept is the 60th newest of the 120 offered");
});

test("room tail: acking a delivery does not hide it, so an unaddressed line she has acked is still found, and mention basis rides on the line", async () => {
  const events = [
    withContent("$a", META, { body: "first" }, 3000),
    withContent("$b", META, { body: "tag", "m.mentions": { user_ids: [SELF] } }, 3001),
  ];
  const hs = await fakeHomeserver({ sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events } } }) } });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    const made = await mouth.syncOnce();
    assert.deepEqual((await mouth.tail()).lines.map(l => [l.eventId, l.basis]), [["$a", null], ["$b", "tag"]]);
    await mouth.ack(made[1].seq);
    assert.equal(mouth.pendingAfter(0).length, 0);
    assert.deepEqual((await mouth.tail()).lines.map(l => l.eventId), ["$a", "$b"], "acked lines are the ones no delivery will bring again");
  } finally { await hs.close(); }
});

test("room tail: a room that never answers is absent after the timeout and does not hold the others", async () => {
  const hs = await fakeHomeserver({ preload: { [OPERATOR]: lines(OPERATOR, 2) }, sync: since => ({ next_batch: since ?? "b0" }) });
  const hang = (url, init) => String(url).includes(encodeURIComponent(ROOM)) && String(url).includes("/messages") ? new Promise(() => {}) : hs.fetch(url, init);
  try {
    const mouth = mouthFor(hs, { fetch: hang, sendTimeoutMs: 50 });
    await mouth.syncOnce();
    const read = await mouth.tail();
    assert.deepEqual([...new Set(read.lines.map(l => l.roomAlias))], [`#operator:${HS_NAME}`]);
    assert.deepEqual(read.gaps, [`#swarm:${HS_NAME}`], "the room that timed out is a gap; the one that answered is not");
  } finally { await hs.close(); }
});

test("room tail: a mouth that has not started reads no room", async () => {
  const hs = await fakeHomeserver({ preload: { [ROOM]: lines(ROOM, 2) }, sync: since => ({ next_batch: since ?? "b0" }) });
  try {
    assert.deepEqual(await mouthFor(hs).tail(), { lines: [], gaps: Object.keys(ROOMS), rooms: Object.keys(ROOMS) }, "no room is known before start, so every configured room is a gap");
    assert.equal(hs.seen.filter(r => r.path.includes("/messages")).length, 0);
  } finally { await hs.close(); }
});

test("room tail: a body is clipped like a context line, and an unreadable room is absent without silencing the others", async () => {
  const { tail } = await tailed({ preload: { [ROOM]: [message("$big", META, "x".repeat(5000), 10)] } });
  assert.equal(Buffer.byteLength(tail[0].text), docs.limits.contextBytes);
  const hs = await fakeHomeserver({ failContext: true, sync: since => ({ next_batch: since ?? "b0" }) });
  try {
    const mouth = mouthFor(hs);
    await mouth.syncOnce();
    const read = await mouth.tail();
    assert.deepEqual(read.lines, []);
    assert.deepEqual(read.gaps, Object.keys(ROOMS).filter(a => read.rooms.includes(a)), "an unreadable room is a gap, not quiet");
    assert.ok(read.gaps.length > 0);
  } finally { await hs.close(); }
});

test("room tail: only the v3 answer carries it; the v2 answer a v1 request gets is unchanged", async () => {
  const { tail, gaps, rooms } = await tailed({ preload: { [ROOM]: lines(ROOM, 2) } });
  const pack = { encode: v => Buffer.from(JSON.stringify(v)) };
  assert.deepEqual(docs.parsePendingV3(pageOf("eureka", [], pack, () => {}, true, { lines: tail, gaps, rooms })).tail.map(l => l.text), ["line 0", "line 1"]);
  const v2 = pageOf("eureka", [], pack, () => {}, false, { lines: tail, gaps: rooms, rooms });
  assert.equal(v2.length, 2);
  assert.deepEqual(docs.parsePending(v2), { persona: "eureka", deliveries: [] });
  assert.throws(() => docs.parsePendingV3(["eureka", []]), /4-field/);
});

test("room tail: the reply byte budget trims the oldest tail lines, never a delivery", async () => {
  const pack = { encode: v => Buffer.from(JSON.stringify(v)) };
  const big = Array.from({ length: 60 }, (_, i) => ({ roomAlias: "#r:x", eventId: `$${i}`, atUnixMs: i, sender: "@a:x", form: "message", basis: null, text: "y".repeat(5000) }));
  const kept = docs.parsePendingV3(pageOf("eureka", [], pack, () => {}, true, { lines: big, gaps: [], rooms: ["#r:x"] })).tail;
  assert.ok(kept.length > 0 && kept.length < 60);
  assert.equal(kept.at(-1).eventId, "$59", "the newest survive");
});

// tail_gaps (docs/bridge.md, "Gaps in the tail"): the rooms whose lines are not all in the tail, so a reader says "unknown", not "quiet".
// Each case goes through the composition the RUDP endpoint serves: mouth.tail() into pageOf into the strict v3 parser.
const replied = (read, pack = { encode: v => Buffer.from(JSON.stringify(v)) }) => docs.parsePendingV3(pageOf("eureka", [], pack, () => {}, true, read));
const SWARM_ALIAS = `#swarm:${HS_NAME}`, OPERATOR_ALIAS = `#operator:${HS_NAME}`;

test("tail gaps: a room whose read times out is a gap; the room that answered, quiet or not, is not", async () => {
  const hs = await fakeHomeserver({ preload: { [OPERATOR]: lines(OPERATOR, 2) }, sync: since => ({ next_batch: since ?? "b0" }) });
  const hang = (url, init) => String(url).includes(encodeURIComponent(ROOM)) && String(url).includes("/messages") ? new Promise(() => {}) : hs.fetch(url, init);
  try {
    const mouth = mouthFor(hs, { fetch: hang, sendTimeoutMs: 50 });
    await mouth.syncOnce();
    const got = replied(await mouth.tail());
    assert.deepEqual(got.tailGaps, [SWARM_ALIAS]);
    assert.deepEqual(got.tail.map(l => l.roomAlias), [OPERATOR_ALIAS, OPERATOR_ALIAS]);
  } finally { await hs.close(); }
});

test("tail gaps: a failed read is a gap, a quiet room (no lines) and a normal room (a few lines) are not, and exactly the per-room cap is not a trim", async () => {
  const failing = await fakeHomeserver({ failContext: true, sync: since => ({ next_batch: since ?? "b0" }) });
  try {
    const mouth = mouthFor(failing);
    await mouth.syncOnce();
    assert.deepEqual(replied(await mouth.tail()).tailGaps, [SWARM_ALIAS, OPERATOR_ALIAS], "every read failed: both rooms unknown, in room order");
  } finally { await failing.close(); }

  const { gaps } = await tailed({ preload: { [ROOM]: lines(ROOM, docs.limits.tailPerRoom) } });
  assert.deepEqual(gaps, [], "20 lines in one room and none in the other: nothing was left out");
  const some = await tailed({ preload: { [ROOM]: lines(ROOM, 3), [OPERATOR]: [] } });
  assert.deepEqual(replied({ lines: some.tail, gaps: some.gaps, rooms: some.rooms }).tailGaps, []);
});

test("tail gaps: a room with more lines than the per-room cap is a gap, once, and only that room", async () => {
  const { tail, gaps } = await tailed({ preload: { [ROOM]: lines(ROOM, docs.limits.tailPerRoom + 1), [OPERATOR]: lines(OPERATOR, 3) } });
  assert.equal(tail.filter(l => l.roomAlias === SWARM_ALIAS).length, docs.limits.tailPerRoom);
  assert.deepEqual(gaps, [SWARM_ALIAS]);
});

test("tail gaps: the total cap names the rooms it dropped lines from, in room order, never a room whose lines all fit", async () => {
  const dms = ["!d1:gamecult.test", "!d2:gamecult.test", "!d3:gamecult.test"];
  const preload = { [ROOM]: lines(ROOM, 25, 100), [OPERATOR]: lines(OPERATOR, 20, 0) };
  dms.forEach((dm, i) => { preload[dm] = lines(dm, 20, 200 + 20 * i); });
  const { tail, gaps } = await tailed({ preload, direct: { [META]: dms }, member: [...Object.values(ROOMS), ...dms] });
  assert.equal(tail.length, 60);
  assert.deepEqual(gaps, [SWARM_ALIAS, OPERATOR_ALIAS], "swarm is gapped by both caps and named once; the three DMs kept all 20 lines");
});

test("tail gaps: the reply byte budget and a line that will not encode gap their rooms, and only those", () => {
  const pack = { encode: v => Buffer.from(JSON.stringify(v)) };
  const mk = (room, i, extra = {}) => ({ roomAlias: room, eventId: `$${room}${i}`, atUnixMs: i, sender: "@a:x", form: "message", basis: null, text: "y".repeat(5000), ...extra });
  const old = Array.from({ length: 30 }, (_, i) => mk("#old:x", i)), fresh = Array.from({ length: 30 }, (_, i) => mk("#new:x", 100 + i));
  const got = replied({ lines: [...old, ...fresh], gaps: [], rooms: ["#old:x", "#new:x", "#empty:x"] }, pack);
  assert.ok(got.tail.length < 60);
  assert.deepEqual(got.tailGaps, ["#old:x"], "the oldest lines were trimmed; the newer room and the quiet room are whole");
  const bad = replied({ lines: [mk("#a:x", 1), mk("#b:x", 2, { sender: 123 })], gaps: [], rooms: ["#a:x", "#b:x"] }, pack);
  assert.deepEqual(bad.tailGaps, ["#b:x"]);
});

test("tail gaps: the parser refuses a repeated room, and a pre-gap decoder is stopped by arity, as the tail was", () => {
  assert.throws(() => docs.parsePendingV3(["eureka", [], [], ["#a:x", "#a:x"]]), /repeats/);
  assert.deepEqual(docs.parsePendingV3(["eureka", [], [], ["#a:x", "#b:x"]]).tailGaps, ["#a:x", "#b:x"]);
  assert.throws(() => docs.parsePendingV3(["eureka", [], []]), /4-field/);
  assert.throws(() => docs.parsePendingV3(["eureka", [], [], "#a:x"]), /list/);
});

test("tail gaps: a line the mouth drops as malformed (no event id) gaps its room; a clean room beside it does not", async () => {
  const { tail, gaps } = await tailed({ preload: { [ROOM]: [message("", META, "no id", 10), message("$ok", META, "fine", 11)], [OPERATOR]: lines(OPERATOR, 1) } });
  assert.deepEqual(tail.map(l => l.eventId).sort(), ["$ok", "$ope0"].sort());
  assert.deepEqual(gaps, [SWARM_ALIAS]);
});

const edit = (id, ts) => ({ type: "m.room.message", event_id: id, sender: META, origin_server_ts: ts, content: { msgtype: "m.text", body: "* edited", "m.relates_to": { rel_type: "m.replace", event_id: "$x" } } });
const threaded = (id, ts) => ({ type: "m.room.message", event_id: id, sender: META, origin_server_ts: ts, content: { msgtype: "m.text", body: "in thread", "m.relates_to": { rel_type: "m.thread", event_id: "$root" } } });
const noise = (n, from) => Array.from({ length: n }, (_, i) => (i % 2 ? threaded : edit)(`$n${from + i}`, 1000 + from + i));

test("tail gaps: a full read window hides older real lines behind edits and thread replies, so the room is a gap; a short chunk is exhausted history, not a gap", async () => {
  const hidden = await tailed({ preload: { [ROOM]: [...lines(ROOM, 5), ...noise(120, 0)] } });
  assert.deepEqual(hidden.tail, [], "the five real lines lie beyond the 100-event window");
  assert.deepEqual(hidden.gaps, [SWARM_ALIAS], "so the room is unknown, not quiet");
  const whole = await tailed({ preload: { [ROOM]: [...lines(ROOM, 5), ...noise(60, 0)] } });
  assert.equal(whole.tail.length, 5);
  assert.deepEqual(whole.gaps, [], "a chunk shorter than the window reached the room's start");
  const at99 = await tailed({ preload: { [ROOM]: [...lines(ROOM, 5), ...noise(94, 0)] } });
  assert.deepEqual(at99.gaps, [], "99 raw events: one short of the window, history exhausted");
  const at100 = await tailed({ preload: { [ROOM]: [...lines(ROOM, 5), ...noise(95, 0)] } });
  assert.deepEqual(at100.gaps, [SWARM_ALIAS], "exactly 100 raw events fill the window, so older history cannot be ruled out");
});

test("tail gaps: the total cap boundary: exactly 60 lines over all rooms is no gap, a 61st names the room that lost its oldest line", async () => {
  const dms = ["!d1:gamecult.test", "!d2:gamecult.test"];
  const rooms60 = { [ROOM]: lines(ROOM, 20, 0), [OPERATOR]: lines(OPERATOR, 20, 20), [dms[0]]: lines(dms[0], 20, 40) };
  const at60 = await tailed({ preload: rooms60, direct: { [META]: dms }, member: [...Object.values(ROOMS), ...dms] });
  assert.equal(at60.tail.length, 60);
  assert.deepEqual(at60.gaps, []);
  const at61 = await tailed({ preload: { ...rooms60, [dms[1]]: lines(dms[1], 1, 60) }, direct: { [META]: dms }, member: [...Object.values(ROOMS), ...dms] });
  assert.equal(at61.tail.length, 60);
  assert.deepEqual(at61.gaps, [SWARM_ALIAS]);
});

test("tail gaps: a byte-budget cut is contiguous at the oldest end, so an older line that would still fit is cut and its room gapped", () => {
  const pack = { encode: v => Buffer.from(JSON.stringify(v)) };
  const mk = (room, i, text) => ({ roomAlias: room, eventId: `$${room}${i}`, atUnixMs: i, sender: "@a:x", form: "message", basis: null, text });
  const tiny = mk("#tiny:x", 0, "t"), big = Array.from({ length: 70 }, (_, i) => mk("#big:x", 1 + i, "y".repeat(5000)));
  const got = replied({ lines: [tiny, ...big], gaps: [], rooms: ["#tiny:x", "#big:x"] }, pack);
  assert.ok(!got.tail.some(l => l.roomAlias === "#tiny:x"), "nothing older than the cut is kept");
  assert.deepEqual(got.tailGaps, ["#tiny:x", "#big:x"]);
});

test("tail gaps: the read timer is cleared when the read answers, and a mouth that has not started names its configured rooms for an endpoint that cannot read", async () => {
  const delay = 4321, made = new Set(), cleared = new Set();
  const realSet = globalThis.setTimeout, realClear = globalThis.clearTimeout;
  globalThis.setTimeout = (fn, ms, ...rest) => { const t = realSet(fn, ms, ...rest); if (ms === delay) made.add(t); return t; };
  globalThis.clearTimeout = t => { cleared.add(t); return realClear(t); };
  const hs = await fakeHomeserver({ preload: { [ROOM]: lines(ROOM, 2), [OPERATOR]: lines(OPERATOR, 2) }, sync: since => ({ next_batch: since ?? "b0" }) });
  try {
    const mouth = mouthFor(hs, { sendTimeoutMs: delay });
    assert.deepEqual(mouth.speakableRooms(), Object.keys(ROOMS), "before start: the configured rooms");
    await mouth.syncOnce();
    assert.deepEqual(mouth.speakableRooms(), Object.keys(ROOMS), "after start: the rooms she can speak in");
    await mouth.tail();
    assert.ok(made.size >= 2);
    assert.ok([...made].every(t => cleared.has(t)), "no read timer is left running");
  } finally { globalThis.setTimeout = realSet; globalThis.clearTimeout = realClear; await hs.close(); }
});

// ---- Reply link (chat_pending.v4, docs/bridge.md "Reply link"): the quoted event's id, author and excerpt, behind replyLink (default off).
const ALICE_ID = `@alice:${HS_NAME}`;
const quoting = (id, to, extra = {}) => withContent(id, META, { body: "re", "m.relates_to": { "m.in_reply_to": { event_id: to }, ...extra } });
const target = (id, sender, body, extra = {}) => ({ ...withContent(id, sender, { body }, 1000), room_id: ROOM, ...extra });
const quotes = made => made.map(d => [d.eventId, d.basis, d.replyTo]);
async function linked(events, preload, replyLink = true) {
  const hs = await fakeHomeserver({ preload: { [ROOM]: preload }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events } } }) } });
  try {
    const mouth = mouthFor(hs, { replyLink });
    await mouth.syncOnce();
    return { made: await mouth.syncOnce(), hs };
  } finally { await hs.close(); }
}

test("reply link: a reply to anyone carries the quoted id, the author's Matrix id and the text; basis stays reply only for her own words", async () => {
  const { made } = await linked(
    [quoting("$a", "$hers"), quoting("$b", "$alices"), withContent("$c", META, { body: "plain" })],
    [target("$hers", SELF, "my words"), target("$alices", ALICE_ID, "can someone look at the build?")]);
  assert.deepEqual(quotes(made), [
    ["$a", "reply", { eventId: "$hers", sender: SELF, excerpt: "my words" }],
    ["$b", null, { eventId: "$alices", sender: ALICE_ID, excerpt: "can someone look at the build?" }],
    ["$c", null, null],
  ]);
});

test("reply link: flag off (default) delivers no replyTo and the basis is what it always was", async () => {
  const pre = [target("$hers", SELF, "my words"), target("$alices", ALICE_ID, "secret words")];
  const { made } = await linked([quoting("$a", "$hers"), quoting("$b", "$alices")], pre, false);
  assert.deepEqual(quotes(made), [["$a", "reply", null], ["$b", null, null]]);
});

test("reply link: the excerpt is cut at 240 code points, not bytes, and never splits a character", async () => {
  const emoji = "\u{1F600}".repeat(300), ascii = "a".repeat(300), combining = "é".repeat(200);
  const { made } = await linked([quoting("$e", "$t1"), quoting("$a", "$t2"), quoting("$c", "$t3"), quoting("$s", "$t4")],
    [target("$t1", ALICE_ID, emoji), target("$t2", ALICE_ID, ascii), target("$t3", ALICE_ID, combining), target("$t4", ALICE_ID, "short")]);
  const ex = made.map(d => d.replyTo.excerpt);
  assert.deepEqual([...ex[0]].length, 240);
  assert.equal(ex[0], "\u{1F600}".repeat(240), "240 astral characters, 960 bytes");
  assert.equal(ex[1], "a".repeat(240));
  assert.equal([...ex[2]].length, 240, "code points: a combining mark counts as one, as documented");
  assert.equal(ex[3], "short");
  assert.ok(ex.every(e => !e.includes("�")));
});

test("reply link: a redacted quoted event keeps its id and author but no text; so does a non-text one", async () => {
  const { made } = await linked([quoting("$r", "$red"), quoting("$n", "$nobody"), quoting("$m", "$img")], [
    target("$red", ALICE_ID, "the secret that was redacted", { content: {}, unsigned: { redacted_because: { type: "m.room.redaction" } } }),
    target("$nobody", ALICE_ID, "", { content: {} }),
    target("$img", ALICE_ID, "x", { type: "m.sticker", content: { body: "private caption" } }),
  ]);
  assert.deepEqual(made.map(d => d.replyTo), [
    { eventId: "$red", sender: ALICE_ID, excerpt: null },
    { eventId: "$nobody", sender: ALICE_ID, excerpt: null },
    { eventId: "$img", sender: ALICE_ID, excerpt: null },
  ]);
  const redactedStillText = await linked([quoting("$r", "$red")], [target("$red", ALICE_ID, "still has a body", { unsigned: { redacted_because: {} } })]);
  assert.equal(redactedStillText.made[0].replyTo.excerpt, null, "a redaction marker wins even if a body is present");
});

test("reply link: a quoted event in a room she cannot read, an unreadable one and a thread fallback give no replyTo and no text", async () => {
  const { made } = await linked(
    [quoting("$other", "$foreign"), quoting("$gone", "$missing"), quoting("$th", "$alices", { rel_type: "m.thread", event_id: "$root", is_falling_back: true })],
    [{ ...target("$foreign", ALICE_ID, "from another room"), room_id: "!elsewhere:gamecult.test" }, target("$alices", ALICE_ID, "fallback target")]);
  assert.deepEqual(made.map(d => d.replyTo), [null, null, null]);
  assert.ok(!JSON.stringify(made.map(d => d.replyTo)).includes("room") && made.every(d => d.replyTo === null), "no id, author or text of the foreign event travels");
});

test("reply link: the same quoted event is read once, and a lookup cache hit from the basis check serves the quote", async () => {
  const { made, hs } = await linked([quoting("$a", "$hers"), quoting("$b", "$hers")], [target("$hers", SELF, "mine")]);
  assert.deepEqual(made.map(d => d.replyTo?.excerpt), ["mine", "mine"]);
  assert.equal(hs.seen.filter(r => r.path.includes("/event/")).length, 1);
});

test("reply link: an edited target is quoted without its pre-edit text, whether it is the edit or has one bundled; id and author stay", async () => {
  const { made } = await linked([quoting("$a", "$bundled"), quoting("$b", "$edit")], [
    target("$bundled", ALICE_ID, "old wording", { unsigned: { "m.relations": { "m.replace": { event_id: "$x" } } } }),
    target("$edit", ALICE_ID, "* new wording", { content: { msgtype: "m.text", body: "* new wording", "m.relates_to": { rel_type: "m.replace", event_id: "$orig" } } }),
  ]);
  assert.deepEqual(made.map(d => d.replyTo), [{ eventId: "$bundled", sender: ALICE_ID, excerpt: null }, { eventId: "$edit", sender: ALICE_ID, excerpt: null }]);
});

test("reply link: a cached target is re-read after 60 s, so a redaction or edit in between shows; inside 60 s the cached read stands", async () => {
  const clock = { t: 1_000_000 };
  const mine = target("$t", ALICE_ID, "before");
  const hs = await fakeHomeserver({ preload: { [ROOM]: [mine] }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: `b${since}x`, ...joined({ [ROOM]: { timeline: { events: [quoting(`$q${since}`, "$t")] } } }) } });
  try {
    const mouth = mouthFor(hs, { replyLink: true, now: () => clock.t });
    await mouth.syncOnce();
    const first = await mouth.syncOnce();
    mine.content = {}; mine.unsigned = { redacted_because: {} };
    clock.t += 59_000;
    const within = await mouth.syncOnce();
    clock.t += 2_000;
    const after = await mouth.syncOnce();
    assert.deepEqual([first, within, after].map(m => m[0].replyTo.excerpt), ["before", "before", null]);
  } finally { await hs.close(); }
});

test("reply link: the target's own reply fallback is dropped before the cap; a body of only fallback has no excerpt", async () => {
  const inReply = { "m.in_reply_to": { event_id: "$earlier" } };
  const withFallback = body => ({ content: { msgtype: "m.text", body, "m.relates_to": inReply } });
  const { made } = await linked([quoting("$a", "$f1"), quoting("$b", "$f2"), quoting("$c", "$f3"), quoting("$d", "$f4")], [
    target("$f1", ALICE_ID, "", withFallback("> <@bob:gamecult.test> what was it\n> second line\n\nthe real answer")),
    target("$f2", ALICE_ID, "", withFallback("> <@bob:gamecult.test> only a quote\n\n")),
    target("$f3", ALICE_ID, "", { content: { msgtype: "m.text", body: "> a literal quote with no reply\n\nkept" } }),
    target("$f4", ALICE_ID, "", withFallback(`> <@bob:gamecult.test> ${"q".repeat(500)}\n\n${"w".repeat(300)}`)),
  ]);
  assert.deepEqual(made.map(d => d.replyTo.excerpt), ["the real answer", null, "> a literal quote with no reply kept", "w".repeat(240)]);
});

test("reply link: the excerpt is safe in a prompt: control characters, bidi marks and line breaks are gone before the cap", async () => {
  const probe = "a\u0000b\u202ec\r\nd\u001b[31m";
  const { made } = await linked([quoting("$p", "$t1"), quoting("$q", "$t2"), quoting("$r", "$t3")], [
    target("$t1", ALICE_ID, probe),
    target("$t2", ALICE_ID, "  x\t\t y\n\n\nz\u200f\u2066w\u2069\u0085\u007f ok  "),
    target("$t3", ALICE_ID, `\u202e${"\u0000".repeat(500)}${"k".repeat(300)}`),
  ]);
  assert.deepEqual(made.map(d => d.replyTo.excerpt), ["abc d[31m", "x  y zw ok", "k".repeat(240)], "controls removed first, so they do not eat the 240");
});

test("reply link: a mouth built without the option has it off, and only the exact env value \"1\" turns it on", async () => {
  const hs = await fakeHomeserver({ preload: { [ROOM]: [target("$t", ALICE_ID, "words")] }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events: [quoting("$q", "$t")] } } }) } });
  try {
    const mouth = createMouth({ persona: "eureka", homeserver: hs.url, rooms: Object.keys(ROOMS), token: TOKEN, state: memoryState(), fetch: hs.fetch, serverName: HS_NAME, brake: async () => true, log: () => {}, syncTimeoutMs: 1 });
    await mouth.syncOnce();
    assert.equal((await mouth.syncOnce())[0].replyTo, null);
  } finally { await hs.close(); }
  assert.deepEqual(["1", "true", "yes", "0", "", " 1", undefined].map(v => replyLinkFromEnv({ CHAT_PENDING_V4: v })), [true, false, false, false, false, false, false]);
  assert.equal(replyLinkFromEnv({}), false);
});

// ---- Fixes after PR #63: the target cache, the excerpt scrub, the per-round read cap.
const eventGets = (hs, id) => hs.seen.filter(r => r.method === "GET" && r.path.endsWith(`/event/${id}`)).length;
async function replyAcrossTtl(replyLink, failure = 500) {
  const clock = { t: 1_000_000 };
  let down = false, attempts = 0;
  const hs = await fakeHomeserver({ preload: { [ROOM]: [target("$hers", SELF, "my words")] }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: `b${since}x`, ...joined({ [ROOM]: { timeline: { events: [quoting(`$q${since}`, "$hers")] } } }) } });
  try {
    const fetch = (u, o) => (String(u).includes("/event/%24hers") && attempts++, down && String(u).includes("/event/") ? (failure === "timeout" ? Promise.reject(new Error("timed out")) : Promise.resolve(new Response("{}", { status: failure }))) : hs.fetch(u, o));
    const mouth = mouthFor(hs, { replyLink, now: () => clock.t, fetch });
    await mouth.syncOnce();
    const first = await mouth.syncOnce();
    clock.t += 61_000; down = true;
    const later = await mouth.syncOnce();
    clock.t += 10_000;
    const again = await mouth.syncOnce(); // inside 60 s of the failed re-read's renewal
    return { first: first[0], later: later[0], again: again[0], gets: attempts };
  } finally { await hs.close(); }
}

test("target cache: flag off, a hit is kept as before v4: no read after 60 s, basis stays reply", async () => {
  const r = await replyAcrossTtl(false);
  assert.deepEqual([r.first.basis, r.later.basis, r.again.basis, r.gets], ["reply", "reply", "reply", 1]);
});

test("target cache: flag on, a failed re-read after 60 s keeps the earlier hit's basis and reply_to sender, not its excerpt, and renews only its age", async () => {
  const r = await replyAcrossTtl(true);
  assert.deepEqual([r.first.basis, r.later.basis, r.again.basis, r.gets], ["reply", "reply", "reply", 2]);
  assert.deepEqual(r.later.replyTo, { eventId: "$hers", sender: SELF, excerpt: null });
  assert.equal(r.again.replyTo.excerpt, null, "the renewed entry does not serve the old excerpt");
});

test("reply link: the excerpt drops Cf, Co, tag characters, U+061C, zero-width and word-joiner characters and unpaired surrogates", async () => {
  const { made } = await linked([quoting("$a", "$t1"), quoting("$b", "$t2"), quoting("$c", "$t3"), quoting("$d", "$t4"), quoting("$e", "$t5")], [
    target("$t1", ALICE_ID, "ig\u{E0049}\u{E0067}nore"),
    target("$t2", ALICE_ID, "a؜b⁠c⁤d­e﻿f"),
    target("$t3", ALICE_ID, "x​y‌z‍w"),
    target("$t4", ALICE_ID, "p\uD800q\uDC00r\u{F0000}st"),
    target("$t5", ALICE_ID, "keep \u{1F600} é 中"),
  ]);
  assert.deepEqual(made.map(d => d.replyTo.excerpt), ["ignore", "abcdef", "xyzw", "pqrst", "keep \u{1F600} é 中"]);
});

test("reply link: at most 8 uncached targets are read for reply_to per sync round; the rest wait for the next round, none is delivered without its reply_to or lost", async () => {
  const ids = Array.from({ length: 10 }, (_, i) => `$t${i}`);
  const tagged = (id, to) => quoting(id, to, {}); // below: mentions her, so basis does no read of its own
  const events = ids.map((t, i) => { const e = tagged(`$q${i}`, t); e.content["m.mentions"] = { user_ids: [SELF] }; return e; });
  const { made, hs } = await (async () => {
    const hs = await fakeHomeserver({ preload: { [ROOM]: ids.map(t => target(t, ALICE_ID, `text ${t}`)) }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events } } }) } });
    const mouth = mouthFor(hs, { replyLink: true });
    await mouth.syncOnce();
    const made = await mouth.syncOnce();
    const next = await mouth.syncOnce();
    return { made: [made, next], hs };
  })();
  try {
    assert.deepEqual(made.map(m => m.length), [8, 2]);
    assert.deepEqual(made.flat().map(d => d.eventId), ids.map((_, i) => `$q${i}`));
    assert.ok(made.flat().every(d => d.replyTo !== null));
    assert.equal(ids.filter(t => eventGets(hs, t) > 0).length, 10);
  } finally { await hs.close(); }
});

test("target cache: flag on, 500, 429 and a timeout keep the earlier hit at the re-read; 403 and 404 drop it (reply_to nil, basis as the failure case)", async () => {
  for (const failure of [500, 429, "timeout"]) {
    const r = await replyAcrossTtl(true, failure);
    assert.deepEqual([r.later.basis, r.later.replyTo?.sender, r.later.replyTo?.excerpt], ["reply", SELF, null], String(failure));
  }
  for (const failure of [403, 404]) {
    const r = await replyAcrossTtl(true, failure);
    assert.deepEqual([r.first.basis, r.later.basis, r.later.replyTo, r.again.replyTo], ["reply", null, null, null], String(failure));
  }
});

test("target cache: transient failures keep a hit's sender for 10 minutes from the last good read and its excerpt for 60 s, then drop the hit; a good read resets", async () => {
  const clock = { t: 1_000_000 };
  let down = false;
  const hs = await fakeHomeserver({ preload: { [ROOM]: [target("$hers", SELF, "my words")] }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: `b${since}x`, ...joined({ [ROOM]: { timeline: { events: [quoting(`$q${since}`, "$hers")] } } }) } });
  try {
    const fetch = (u, o) => (down && String(u).includes("/event/") ? Promise.resolve(new Response("{}", { status: 503 })) : hs.fetch(u, o));
    const mouth = mouthFor(hs, { replyLink: true, now: () => clock.t, fetch });
    await mouth.syncOnce();
    await mouth.syncOnce(); // good read at t0
    const step = async ms => { clock.t += ms; return (await mouth.syncOnce())[0]; };
    down = true;
    const seen = [];
    for (const ms of [200_000, 200_000, 140_000]) seen.push((await step(ms)).replyTo?.excerpt); // 3.3, 6.7, 9 min after the good read: sender kept, excerpt gone
    assert.equal((await step(0)).replyTo?.sender, SELF);
    seen.push((await step(120_000)).replyTo); // 11 min
    down = false;
    seen.push((await step(61_000)).replyTo?.excerpt); // a good read resets
    down = true;
    seen.push((await step(420_000)).replyTo?.sender); // 7 min after the new good read: the sender is kept again
    assert.deepEqual(seen, [null, null, null, null, "my words", SELF]);
  } finally { await hs.close(); }
});

test("reply link: the per-round read count resets each sync round", async () => {
  const ids = Array.from({ length: 18 }, (_, i) => `$t${i}`);
  const tagged = (i, to) => { const e = quoting(`$q${i}`, to); e.content["m.mentions"] = { user_ids: [SELF] }; return e; };
  const hs = await fakeHomeserver({ preload: { [ROOM]: ids.map(t => target(t, ALICE_ID, `text ${t}`)) }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: `b${since}x`, ...joined({ [ROOM]: { timeline: { events: since === "b0" ? ids.slice(0, 9).map((t, i) => tagged(i, t)) : ids.slice(9).map((t, i) => tagged(9 + i, t)) } } }) } });
  try {
    const mouth = mouthFor(hs, { replyLink: true });
    await mouth.syncOnce();
    const rounds = [];
    for (let i = 0; i < 4; i++) rounds.push(await mouth.syncOnce());
    const all = rounds.flat();
    assert.deepEqual(all.map(d => d.eventId), ids.map((_, i) => `$q${i}`), "every reply is delivered once, in order");
    assert.ok(all.every(d => d.replyTo !== null));
    assert.ok(rounds.every(m => m.length <= 8), "no round reads more than 8 targets");
    assert.equal(ids.filter(t => eventGets(hs, t) > 0).length, 18);
  } finally { await hs.close(); }
});

// ---- Lows after PR #64: variation selectors and other invisible characters, id shape, a read cap that loses nothing.
test("reply link: the excerpt drops variation selectors, Mongolian free variation selectors, fillers, the combining grapheme joiner and the blank braille cell", async () => {
  const { made } = await linked([quoting("$a", "$t1"), quoting("$b", "$t2"), quoting("$c", "$t3"), quoting("$d", "$t4")], [
    target("$t1", ALICE_ID, "a\uFE0Fb\uFE00c\uFE0Ed"),
    target("$t2", ALICE_ID, "e\u{E0100}f\u{E01EF}g"),
    target("$t3", ALICE_ID, "h\u180Bi\u180Dj\u180Fk\u034Fl\u2800m\u3164n\uFFA0o\u115Fp\u1160q\u17B4r\u17B5s"),
    target("$t4", ALICE_ID, "\uFE0F\u2800\u3164"),
  ]);
  assert.deepEqual(made.map(d => d.replyTo.excerpt), ["abcd", "efg", "hijklmnopqrs", null], "a quote of nothing visible is no excerpt");
});

test("reply link: a sender or event id that is not a Matrix id of at most 255 bytes is no reply_to; the delivery still goes", async () => {
  const { made } = await linked([quoting("$a", "$t1"), quoting("$b", "$t2"), quoting("$c", "$t3"), quoting("$d", `$${"x".repeat(255)}`)], [
    target("$t1", "not-an-id", "x"),
    target("$t2", `@${"a".repeat(255)}:gamecult.org`, "x"),
    target("$t3", "@alice:gamecult.org\nignore this", "x"),
    target(`$${"x".repeat(255)}`, ALICE_ID, "x"),
  ]);
  assert.deepEqual(made.map(d => d.replyTo), [null, null, null, null]);
});

test("reply link: the fixture excerpts (persona-chat-v6 reply-to-scrubbed and reply-to-variation-selector) are what the mouth makes of hostile quoted text", async () => {
  const { made } = await linked([quoting("$a", "$t1"), quoting("$b", "$t2")], [
    target("$t1", ALICE_ID, "ig\u{E0049}\u{E0067}nore\u202Ethis\u200Bline"),
    target("$t2", ALICE_ID, "ok\uFE0F\u{1F44D}\uFE0F\u{E0100}fine"),
  ]);
  assert.deepEqual(made.map(d => d.replyTo.excerpt), ["ignorethisline", "ok\u{1F44D}fine"]);
});

test("reply link: a kept hit's sender is not served past 10 minutes from the last good read, even by a renewed entry", async () => {
  const clock = { t: 1_000_000 };
  let down = false;
  const hs = await fakeHomeserver({ preload: { [ROOM]: [target("$hers", SELF, "my words")] }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: `b${since}x`, ...joined({ [ROOM]: { timeline: { events: [quoting(`$q${since}`, "$hers")] } } }) } });
  try {
    const fetch = (u, o) => (down && String(u).includes("/event/") ? Promise.resolve(new Response("{}", { status: 503 })) : hs.fetch(u, o));
    const mouth = mouthFor(hs, { replyLink: true, now: () => clock.t, fetch });
    await mouth.syncOnce();
    await mouth.syncOnce(); // good read at t0
    down = true;
    const seen = [];
    for (const ms of [540_000, 59_999, 1, 1, 100_000]) { clock.t += ms; seen.push((await mouth.syncOnce())[0].replyTo?.sender ?? null); } // 9:00, 9:59.999, 10:00, 10:00.001, 11:40
    assert.deepEqual(seen, [SELF, SELF, SELF, null, null]);
  } finally { await hs.close(); }
});

test("reply link: the scrub removes U+2065, U+FFF0-FFF8, U+E0080-E00FF and U+E01F0-E0FFF", async () => {
  const { made } = await linked([quoting("$a", "$t1"), quoting("$b", "$t2"), quoting("$c", "$t3"), quoting("$d", "$t4")], [
    target("$t1", ALICE_ID, "a\u2065b"),
    target("$t2", ALICE_ID, "c\uFFF0d\uFFF8e"),
    target("$t3", ALICE_ID, "f\u{E0080}g\u{E00FF}h"),
    target("$t4", ALICE_ID, "i\u{E01F0}j\u{E0FFF}k"),
  ]);
  assert.deepEqual(made.map(d => d.replyTo.excerpt), ["ab", "cde", "fgh", "ijk"]);
});

// ---- Lows after PR #68: id shape on every delivery field, mentions bounded in bytes, a read cap on all target GETs.
const ID_OK = { seq: 1, roomAlias: "#swarm:gamecult.test", roomId: ROOM, eventId: "$e", sender: ALICE_ID, senderDisplay: "alice", text: "hi", atUnixMs: 1, context: [{ sender: ALICE_ID, text: "x", atUnixMs: 0 }], mentions: [SELF], isDirect: false, image: null };

test("round cap: 12 plain replies to 12 distinct targets make at most 8 /event/ GETs a round (basis reads count), each delivered once, in order, with reply_to", async () => {
  const ids = Array.from({ length: 12 }, (_, i) => `$t${i}`);
  const hs = await fakeHomeserver({ preload: { [ROOM]: ids.map(t => target(t, ALICE_ID, `text ${t}`)) }, sync: since => since === null ? { next_batch: "b0" } : since === "b0" ? { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events: ids.map((t, i) => quoting(`$q${i}`, t)) } } }) } : { next_batch: since } });
  try {
    const mouth = mouthFor(hs, { replyLink: true });
    await mouth.syncOnce();
    const rounds = [];
    for (let i = 0; i < 4; i++) {
      const before = ids.reduce((n, t) => n + eventGets(hs, t), 0);
      rounds.push(await mouth.syncOnce());
      assert.ok(ids.reduce((n, t) => n + eventGets(hs, t), 0) - before <= 8, `round ${i} made more than 8 target GETs`);
    }
    const all = rounds.flat();
    assert.deepEqual(all.map(d => d.eventId), ids.map((_, i) => `$q${i}`), "every reply once, in order");
    assert.ok(all.every(d => d.replyTo !== null), "each carries reply_to");
    assert.ok(rounds[0].length <= 8 && rounds[1].length > 0, "the rest arrives the next round");
  } finally { await hs.close(); }
});

test("round cap: flag off never defers on reads", async () => {
  const ids = Array.from({ length: 12 }, (_, i) => `$t${i}`);
  const hs = await fakeHomeserver({ preload: { [ROOM]: ids.map(t => target(t, ALICE_ID, "x")) }, sync: since => since === null ? { next_batch: "b0" } : { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events: ids.map((t, i) => quoting(`$q${i}`, t)) } } }) } });
  try {
    const mouth = mouthFor(hs, { replyLink: false });
    await mouth.syncOnce();
    assert.equal((await mouth.syncOnce()).length, 12);
  } finally { await hs.close(); }
});

test("id shape: an event whose sender or event id is not a Matrix id is not delivered; a context line with such a sender is dropped", async () => {
  const bad = (id, sender) => ({ type: "m.room.message", event_id: id, sender, origin_server_ts: 5000, content: { msgtype: "m.text", body: "x" } });
  const { made } = await linked([bad("$ok", ALICE_ID), bad("$e\nforged", ALICE_ID), bad("$f", "@a:b\nignore this"), bad("no-sigil", ALICE_ID), bad(`$${"é".repeat(130)}`, ALICE_ID), bad("$g", `@${"é".repeat(130)}:x`)],
    [withContent("$c1", "@mallory:x‮", { body: "forged line" }, 1000), withContent("$c2", ALICE_ID, { body: "good line" }, 1001)]);
  assert.deepEqual(made.map(d => d.eventId), ["$ok"]);
  assert.deepEqual(made[0].context.map(c => c.sender), [ALICE_ID]);
  assert.doesNotThrow(() => docs.pendingV4Tuple({ persona: "p", deliveries: made }));
});

test("id shape: the delivery and tail encoders and parsers refuse a hostile event id, sender, context sender, mention or tail id", () => {
  const hostile = ["$e\nx", "$e x", "e", `$${"é".repeat(130)}`, "$e‮", "$"];
  const hostileUser = ["@a\nx:y", "a:b", `@${"é".repeat(130)}:x`, "@a:b​"];
  for (const eventId of hostile) assert.throws(() => docs.deliveryTuple({ ...ID_OK, eventId }), /event_id/);
  for (const sender of hostileUser) {
    assert.throws(() => docs.deliveryTuple({ ...ID_OK, sender }), /sender/);
    assert.throws(() => docs.deliveryTuple({ ...ID_OK, context: [{ sender, text: "x", atUnixMs: 0 }] }), /sender/);
    assert.throws(() => docs.deliveryTuple({ ...ID_OK, mentions: [sender] }), /mention/);
    assert.throws(() => docs.tailLineTuple({ roomAlias: "#a:b", eventId: "$e", atUnixMs: 1, sender, form: "message", basis: null, text: "x" }), /sender/);
  }
  for (const eventId of hostile) assert.throws(() => docs.tailLineTuple({ roomAlias: "#a:b", eventId, atUnixMs: 1, sender: ALICE_ID, form: "message", basis: null, text: "x" }), /event_id/);
  const good = docs.deliveryV4Tuple({ ...ID_OK, basis: null, form: "message", replyTo: null });
  assert.doesNotThrow(() => docs.parseDeliveryV4(good));
  const parsed = (i, v) => { const t = structuredClone(good); if (i === 8) t[8][0][0] = v; else t[i] = v; return t; };
  assert.throws(() => docs.parseDeliveryV4(parsed(3, "$e\nx")), /event_id/);
  assert.throws(() => docs.parseDeliveryV4(parsed(4, "@a\nb:c")), /sender/);
  assert.throws(() => docs.parseDeliveryV4(parsed(8, "@a\nb:c")), /sender/);
  const pending = docs.pendingV3Tuple({ persona: "p", deliveries: [], tail: [{ roomAlias: "#a:b", eventId: "$e", atUnixMs: 1, sender: ALICE_ID, form: "message", basis: null, text: "x" }] });
  assert.doesNotThrow(() => docs.parsePendingV3(pending));
  assert.throws(() => docs.parsePendingV3(JSON.parse(JSON.stringify(pending).replace('"$e"', '"$e\\nx"'))), /event_id/);
  assert.throws(() => docs.parsePendingV3(JSON.parse(JSON.stringify(pending).replace(`"${ALICE_ID}"`, '"@a\\nb:c"'))), /sender/);
});

test("id shape: a mention longer than 255 bytes, though under 255 characters, is dropped; the delivery goes", async () => {
  const { made } = await linked([withContent("$m", META, { body: "hi", "m.mentions": { user_ids: [SELF, `@${"é".repeat(200)}:x`, "@a b:x"] } })], []);
  assert.deepEqual(made.map(d => d.mentions), [[SELF]]);
});

const threadOf = (id, root, to) => withContent(id, META, { body: "re", "m.relates_to": { rel_type: "m.thread", event_id: root, "m.in_reply_to": { event_id: to } } });
async function capRun(events, { clock = { t: 1_000_000 }, lapseOnR = 0, steps = 3 } = {}) {
  const ids = [...Array.from({ length: 8 }, (_, i) => `$y${i}`), "$x", "$r"];
  const hs = await fakeHomeserver({ preload: { [ROOM]: ids.map(t => target(t, ALICE_ID, `text ${t}`)) }, sync: since => since === null ? { next_batch: "b0" } : since === "b0" ? { next_batch: "b1", ...joined({ [ROOM]: { timeline: { events: [quoting("$qx", "$x")] } } }) } : since === "b1" ? { next_batch: "b2", ...joined({ [ROOM]: { timeline: { events } } }) } : { next_batch: since } });
  try {
    const fetch = (u, o) => { if (/\/event\/%24r(\?|$)/.test(String(u))) clock.t += lapseOnR; return hs.fetch(u, o); };
    const mouth = mouthFor(hs, { replyLink: true, now: () => clock.t, fetch });
    await mouth.syncOnce();
    await mouth.syncOnce(); // $x read at t0
    clock.t += 59_000;
    const rounds = [];
    for (let i = 0; i < steps; i++) rounds.push(await mouth.syncOnce());
    return { rounds, hs, gets: id => eventGets(hs, id) };
  } finally { await hs.close(); }
}

test("round cap: a thread root counts toward the 8 reads: 8 plain replies then a threaded one defers the threaded one to the next round", async () => {
  const { rounds } = await capRun([...Array.from({ length: 8 }, (_, i) => quoting(`$q${i}`, `$y${i}`)), threadOf("$th", "$r", "$x")]);
  assert.deepEqual(rounds[0].map(d => d.eventId), Array.from({ length: 8 }, (_, i) => `$q${i}`));
  assert.deepEqual(rounds[1].map(d => d.eventId), ["$th"]);
});

test("round cap: a target cached at the check is served from the round even when its 60 s keep lapses between the check and the read", async () => {
  const { rounds, gets } = await capRun([...Array.from({ length: 6 }, (_, i) => quoting(`$q${i}`, `$y${i}`)), threadOf("$th", "$x", "$r")], { lapseOnR: 2000 });
  assert.equal(rounds.flat().length, 7, "the six replies and the threaded one: 6 + 1 counted read");
  assert.equal(gets("$x"), 2, "$x: the read at t0 and the thread context's own root read, none for the lapsed keep");
  assert.ok(rounds[0].every(d => d.replyTo !== null));
});

test("display name: a sender with an empty localpart is delivered under its own id, and the page encodes", async () => {
  const { rounds } = await capRun([withContent("$odd", "@:evil.test", { body: "hi" })], { steps: 1 });
  assert.equal(rounds[0][0].senderDisplay, "@:evil.test");
  assert.doesNotThrow(() => docs.pendingV4Tuple({ persona: "p", deliveries: rounds[0] }));
});

test("round cap: a pin serves only the event whose check made it: a later event's target past its 60 s keep is read again, as counted", async () => {
  const { rounds, gets } = await capRun([quoting("$e1", "$x"), quoting("$e2", "$r"), quoting("$e3", "$x")], { lapseOnR: 2000 });
  assert.deepEqual(rounds.flat().map(d => d.eventId), ["$e1", "$e2", "$e3"]);
  assert.equal(gets("$x"), 2, "the read at t0 and one re-read for $e3");
});

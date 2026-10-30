import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { resolve, join } from "node:path";
import { createMouth, createBrakeReader, guarded, redact } from "../tools/persona-matrix-mouth.mjs";

const TOKEN = "syt_SECRET_TOKEN_9f3";
const HS_NAME = "gamecult.test";
const ROOMS = { [`#swarm:${HS_NAME}`]: "!swarm:gamecult.test", [`#operator:${HS_NAME}`]: "!operator:gamecult.test" };
const SELF = `@eureka:${HS_NAME}`;

const message = (id, sender, body, ts) => ({ type: "m.room.message", event_id: id, sender, origin_server_ts: ts, content: { msgtype: "m.text", body } });
const tick = () => new Promise(r => setImmediate(r));

// A fake Matrix client-server API: just the endpoints the mouth uses.
async function fakeHomeserver({ sync, failWith, users, member, direct } = {}) {
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
      const bearer = (record.auth ?? "").replace("Bearer ", "");
      if (p === "/account/whoami") return reply(200, { user_id: users?.[bearer] ?? SELF });
      if (p === "/joined_rooms") return reply(200, { joined_rooms: member ?? Object.values(ROOMS) });
      if (req.method === "PUT" && p.includes("/account_data/m.direct")) return reply(200, {});
      if (p.endsWith("/leave")) return reply(200, {});
      if (p.includes("/account_data/m.direct")) return direct ? reply(200, direct) : reply(404, { errcode: "M_NOT_FOUND" });
      if (p.startsWith("/directory/room/")) return reply(200, { room_id: ROOMS[p.slice("/directory/room/".length)] });
      if (p.startsWith("/join/")) return reply(200, { room_id: p.slice(6) });
      if (p === "/sync") return reply(200, sync(record.query.get("since"), bearer));
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
    assert.equal(hs.seen.filter(r => r.method === "POST" || r.method === "PUT").length, 0, "braked: nothing is joined or sent");
    open = true;
    await mouth.syncOnce();
    assert.deepEqual(hs.seen.filter(r => r.method === "POST").map(r => r.path.split("/join/")[1]).sort(), ["!operator:gamecult.test", "!swarm:gamecult.test"]);
    await mouth.syncOnce();
    assert.equal(hs.seen.filter(r => r.method === "POST").length, 2, "joined rooms are not joined again");
  } finally { await hs.close(); }

  const failing = await fakeHomeserver({ sync: script, member: [] });
  try {
    const mouth = mouthFor(failing, { fetch: (url, init) => String(url).includes("/join/") ? Promise.reject(new Error(`join refused for ${init.headers.authorization}`)) : fetch(url, init), log: l => logs.push(l) });
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
    assert.deepEqual(hs.seen.filter(r => r.method === "POST" || r.method === "PUT"), [], "braked: no join, no leave, nothing written");
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

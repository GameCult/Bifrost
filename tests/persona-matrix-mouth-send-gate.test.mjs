// The mouth's send gate: a Persona's v2 send is refused, with no PUT, when the room moved past what she held,
// and the check and the PUT are one serial step per room across every Persona. The homeserver here
// answers /messages as Synapse does (start, chunk newest first, end; the types filter and limit honoured)
// and serves the chunk captured from #swarm on 2026-11-03, not a hand-made shape.
import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { createMouth, createRoomGate, movesRoom } from "../tools/persona-matrix-mouth.mjs";

const HS = "gamecult.org";
const ROOM = "!0pZn8mTZ0FdY5-G99iOuD7JWWlF3yA_5p20yO1C8WeA";
const ALIAS = `#swarm:${HS}`;
const captured = JSON.parse(readFileSync(resolve(import.meta.dirname, "fixtures", "persona-chat-v2", "messages-swarm-2026-11-03.json"), "utf8"));
const swarm = captured.chunk.slice().reverse(); // oldest first, 11 real events
const [question] = swarm;
const EUREKA = `@eureka:${HS}`;
const idsBy = (events, pred) => events.filter(pred).map(e => e.event_id);

// A synthetic event, marked as such by its id: the capture holds no threaded reply, edit or notice.
const synthetic = (kind, ts, sender, content) => ({ type: "m.room.message", event_id: `$synthetic-${kind}`, sender, origin_server_ts: ts, content });
const t0 = swarm.at(-1).origin_server_ts;
const threaded = synthetic("thread", t0 + 10, `@nibu:${HS}`, { msgtype: "m.text", body: "in a thread", "m.relates_to": { rel_type: "m.thread", event_id: question.event_id } });
const edit = synthetic("replace", t0 + 20, `@aqua:${HS}`, { msgtype: "m.text", body: "* fixed", "m.new_content": { msgtype: "m.text", body: "fixed" }, "m.relates_to": { rel_type: "m.replace", event_id: swarm[1].event_id } });
const notice = synthetic("notice", t0 + 30, `@root-watch:${HS}`, { msgtype: "m.notice", body: "sudo audit line" });

// users: bearer token -> user id. events: the room's history, oldest first. hooks.delay(kind) -> ms to hold a GET /messages or a PUT.
async function homeserver({ events = swarm, users, headStatus = 200, headHangs = false, delay = () => 0 } = {}) {
  const room = [...events];
  const seen = [];
  const sent = new Map();
  const server = createServer((req, res) => {
    let raw = "";
    req.on("data", c => (raw += c));
    req.on("end", async () => {
      const url = new URL(req.url, "http://x");
      const path = decodeURIComponent(url.pathname).replace("/_matrix/client/v3", "");
      const bearer = (req.headers.authorization ?? "").replace("Bearer ", "");
      const reply = (status, body) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
      const record = { method: req.method, path, body: raw ? JSON.parse(raw) : null, who: users[bearer] };
      seen.push(record);
      if (path === "/account/whoami") return reply(200, { user_id: users[bearer] });
      if (path === "/joined_rooms") return reply(200, { joined_rooms: [ROOM] });
      if (path.includes("/account_data/m.direct")) return reply(404, { errcode: "M_NOT_FOUND", error: "Account data not found" });
      if (path.startsWith("/directory/room/")) return reply(200, { room_id: ROOM, servers: [HS] });
      if (req.method === "GET" && path === `/rooms/${ROOM}/messages`) {
        if (headHangs) return; // never answers
        await new Promise(r => setTimeout(r, delay("get")));
        if (headStatus !== 200) return reply(headStatus, { errcode: "M_UNKNOWN", error: "Internal server error" });
        const types = JSON.parse(url.searchParams.get("filter") ?? "{}").types;
        const page = room.filter(e => !types || types.includes(e.type)).reverse().slice(0, Number(url.searchParams.get("limit") ?? 10));
        return reply(200, { start: "s1", chunk: page, end: "e1" });
      }
      const put = path.match(/^\/rooms\/(.+)\/send\/m\.room\.message\/(.+)$/);
      if (req.method === "PUT" && put) {
        await new Promise(r => setTimeout(r, delay("put")));
        if (!sent.has(put[2])) {
          sent.set(put[2], `$sent-${sent.size + 1}`);
          room.push({ type: "m.room.message", event_id: sent.get(put[2]), sender: users[bearer], origin_server_ts: t0 + 1000 + sent.size, content: record.body });
        }
        return reply(200, { event_id: sent.get(put[2]) });
      }
      reply(404, { errcode: "M_NOT_FOUND" });
    });
  });
  await new Promise(ok => server.listen(0, "127.0.0.1", ok));
  return {
    url: `http://127.0.0.1:${server.address().port}`, room,
    puts: () => seen.filter(r => r.method === "PUT" && r.path.includes("/send/")),
    heads: () => seen.filter(r => r.method === "GET" && r.path.endsWith("/messages")),
    close: () => { server.closeAllConnections(); return new Promise(ok => server.close(ok)); },
  };
}

const memoryState = () => { const box = { value: null }; return { load: async () => box.value, save: async v => { box.value = v; } }; };
const mouth = (hs, persona, token, extra = {}) => createMouth({
  persona, homeserver: hs.url, serverName: HS, rooms: [ALIAS], token, state: memoryState(), brake: async () => true, syncTimeoutMs: 1, ...extra,
});
const send = (m, requestId, basis, persona = "eureka") => m.send({ requestId, persona, roomAlias: ALIAS, text: `line ${requestId}`, basis });
const basisOn = (event, sinceUnixMs = 0) => ({ seenEventId: event.event_id, sinceUnixMs });

test("a stale send makes no PUT and answers RoomMoved with the newer ids, oldest first", async () => {
  const hs = await homeserver({ users: { tok: EUREKA } });
  try {
    const receipt = await send(mouth(hs, "eureka", "tok"), "r1", basisOn(question));
    assert.equal(receipt.refusal, "RoomMoved");
    assert.equal(receipt.eventId, null);
    // Every message after the question except her own three lines.
    assert.deepEqual(receipt.newer, idsBy(swarm.slice(1), e => e.sender !== EUREKA));
    assert.equal(receipt.newer.length, 7);
    assert.equal(hs.puts().length, 0, "a refused send reaches the room never");
  } finally { await hs.close(); }
});

test("a send whose room moved only by m.notice, m.replace or her own account goes out", async () => {
  const quiet = [question, notice, edit, { ...swarm[6], event_id: "$her-own" }];
  const hs = await homeserver({ events: quiet, users: { tok: EUREKA } });
  try {
    const receipt = await send(mouth(hs, "eureka", "tok"), "r2", basisOn(question));
    assert.deepEqual([receipt.refusal, receipt.newer], [null, []]);
    assert.match(receipt.eventId, /^\$sent-/);
    assert.equal(hs.puts().length, 1);
  } finally { await hs.close(); }
});

test("a threaded reply moves the room", async () => {
  const hs = await homeserver({ events: [question, threaded], users: { tok: EUREKA } });
  try {
    const receipt = await send(mouth(hs, "eureka", "tok"), "r3", basisOn(question));
    assert.deepEqual([receipt.refusal, receipt.newer], ["RoomMoved", [threaded.event_id]]);
  } finally { await hs.close(); }
});

test("movesRoom over the captured #swarm chunk and the synthetic kinds", () => {
  const moved = swarm.filter(e => movesRoom(e, EUREKA)).map(e => e.event_id);
  assert.deepEqual(moved, idsBy(swarm, e => e.sender !== EUREKA), "the eleven real events count except her three lines");
  assert.equal(moved.length, 8);
  assert.equal(movesRoom(threaded, EUREKA), true, "a threaded reply counts");
  assert.equal(movesRoom(edit, EUREKA), false, "an m.replace edit does not");
  assert.equal(movesRoom(notice, EUREKA), false, "an m.notice does not");
  assert.equal(movesRoom({ type: "m.room.member", sender: `@x:${HS}`, content: { membership: "join" } }, EUREKA), false, "only messages move a room");
});

test("no seen event: the since floor decides; a seen event outside the page: the whole page counts", async () => {
  const hs = await homeserver({ users: { tok: EUREKA } });
  try {
    const m = mouth(hs, "eureka", "tok");
    const libby = swarm[5];
    const floor = await send(m, "r4", { seenEventId: null, sinceUnixMs: libby.origin_server_ts });
    assert.deepEqual(floor.newer, idsBy(swarm.slice(6), e => e.sender !== EUREKA), "only what came after the since time; the equal-time event is her turn's own start");
    const lost = await send(m, "r5", { seenEventId: "$never-in-the-page", sinceUnixMs: t0 + 1e6 });
    assert.deepEqual(lost.newer, idsBy(swarm, e => e.sender !== EUREKA), "the unreachable seen event leaves the whole page, whatever the since time says");
    const seenWins = await send(m, "r6", { seenEventId: swarm[9].event_id, sinceUnixMs: 0 });
    assert.deepEqual(seenWins.newer, [swarm[10].event_id], "with a seen event, the since time does not matter");
  } finally { await hs.close(); }
});

test("a failed head read makes no PUT and answers SendFailed; so does a head read that hangs", async () => {
  for (const mode of [{ headStatus: 500 }, { headHangs: true }]) {
    const hs = await homeserver({ users: { tok: EUREKA }, ...mode });
    const logs = [];
    try {
      const receipt = await send(mouth(hs, "eureka", "tok", { sendTimeoutMs: 80, log: l => logs.push(l) }), "r7", basisOn(question));
      assert.deepEqual([receipt.eventId, receipt.refusal, receipt.newer], [null, "SendFailed", []], JSON.stringify(mode));
      assert.equal(hs.puts().length, 0, "the guarantee does not fail open");
    } finally { await hs.close(); }
  }
});

test("a failed send does not stop the next one in that room", async () => {
  const gate = createRoomGate();
  await assert.rejects(gate.run(ROOM, async () => { throw new Error("boom"); }), /boom/);
  assert.equal(await gate.run(ROOM, async () => "next"), "next");
});

test("eight Minds answering one message: one PUT, seven RoomMoved naming the first", async () => {
  const names = Array.from({ length: 8 }, (_, i) => `mind${i}`);
  const users = Object.fromEntries(names.map(n => [`tok-${n}`, `@${n}:${HS}`]));
  const hs = await homeserver({ events: [question], users, delay: () => 25 });
  try {
    const gate = createRoomGate();
    const receipts = await Promise.all(names.map(n => send(mouth(hs, n, `tok-${n}`, { gate }), `req-${n}`, basisOn(question), n)));
    assert.equal(hs.puts().length, 1, "exactly one Mind spoke");
    const winner = receipts.filter(r => r.refusal === null);
    assert.equal(winner.length, 1);
    const refused = receipts.filter(r => r.refusal !== null);
    assert.equal(refused.length, 7);
    for (const r of refused) assert.deepEqual([r.refusal, r.newer], ["RoomMoved", [winner[0].eventId]]);
  } finally { await hs.close(); }
});

test("revisions with a nil basis all go out, in the order they reached the gate, and read no head", async () => {
  const hs = await homeserver({ events: [question], users: { tok: EUREKA }, delay: kind => (kind === "put" ? 15 : 0) });
  try {
    const inner = createRoomGate();
    let arrivals = 0;
    const started = [];
    const gate = { run: (room, fn) => { const n = arrivals++; return inner.run(room, () => { started.push(n); return fn(); }); } };
    const m = mouth(hs, "eureka", "tok", { gate });
    const ids = Array.from({ length: 7 }, (_, i) => `rev-${i}`);
    const receipts = await Promise.all(ids.map(id => send(m, id, null)));
    assert.ok(receipts.every(r => r.refusal === null));
    assert.deepEqual(started, [0, 1, 2, 3, 4, 5, 6], "the gate runs them in the order they reached it");
    assert.deepEqual(hs.puts().map(p => p.body.body).sort(), ids.map(id => `line ${id}`), "all seven went out");
    assert.equal(hs.heads().length, 0, "her choice stands: no check");
  } finally { await hs.close(); }
});

test("a v1 send (no basis field at all) is served unchecked through the gate", async () => {
  const hs = await homeserver({ users: { tok: EUREKA } });
  try {
    const receipt = await mouth(hs, "eureka", "tok").send({ requestId: "v1", persona: "eureka", roomAlias: ALIAS, text: "old organ" });
    assert.equal(receipt.refusal, null);
    assert.equal(hs.heads().length, 0);
    assert.equal(hs.puts().length, 1);
  } finally { await hs.close(); }
});

test("a gated send is serial with a gated send in the same room and leaves other rooms alone", async () => {
  const gate = createRoomGate();
  const order = [];
  const hold = (room, name, ms) => gate.run(room, async () => { order.push(`${name}:start`); await new Promise(r => setTimeout(r, ms)); order.push(`${name}:end`); });
  await Promise.all([hold("!a", "a1", 30), hold("!a", "a2", 1), hold("!b", "b1", 1)]);
  assert.deepEqual(order, ["a1:start", "b1:start", "b1:end", "a1:end", "a2:start", "a2:end"]);
});

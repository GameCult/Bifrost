// A Persona asks her mouth for a direct room with one person. The homeserver here keeps room state the way Synapse does
// (create event with its content, one member event per account, encryption event when set), so the mouth's lookup,
// the consent that lives in the person's own membership, and the order of its writes are exercised, not mocked.
import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { createMouth } from "../tools/persona-matrix-mouth.mjs";

const HS = "gamecult.org";
const ME = `@eureka:${HS}`;
const META = `@meta:${HS}`;
const PROJECT = "!project:gamecult.org";
const ALIAS = `#swarm:${HS}`;

async function homeserver({ people = [META, `@nova:${HS}`], now = () => 1000, failInvite: initialFailInvite = false } = {}) {
  const config = { failInvite: initialFailInvite };
  const rooms = new Map(); // room id -> { events }
  const stateReads = [];
  const log = []; // every write, in order, as "METHOD path"
  let counter = 0;
  const direct = { value: null };
  const member = (room, user, membership) => {
    const events = rooms.get(room).events.filter(e => !(e.type === "m.room.member" && e.state_key === user));
    events.push({ type: "m.room.member", state_key: user, origin_server_ts: now(), content: { membership } });
    rooms.get(room).events = events;
  };
  const server = createServer((req, res) => {
    let raw = "";
    req.on("data", c => (raw += c));
    req.on("end", () => {
      const url = new URL(req.url, "http://x");
      const path = decodeURIComponent(url.pathname).replace("/_matrix/client/v3", "");
      const body = raw ? JSON.parse(raw) : null;
      const reply = (status, json) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(json)); };
      const write = () => { if (!path.startsWith("/presence/") && !path.includes("/account_data/")) log.push(`${req.method} ${path}`); };
      if (path === "/account/whoami") return reply(200, { user_id: ME });
      if (path === "/joined_rooms") return reply(200, { joined_rooms: [PROJECT, ...[...rooms].filter(([, r]) => r.events.some(e => e.type === "m.room.member" && e.state_key === ME && e.content.membership === "join")).map(([id]) => id)] });
      if (path.includes("/account_data/m.direct")) {
        if (req.method === "PUT") { direct.value = body; return reply(200, {}); }
        return direct.value ? reply(200, direct.value) : reply(404, { errcode: "M_NOT_FOUND" });
      }
      if (path.startsWith("/directory/room/")) return reply(200, { room_id: PROJECT });
      if (path.startsWith("/profile/")) return people.includes(path.slice("/profile/".length)) ? reply(200, { displayname: "x" }) : reply(404, { errcode: "M_NOT_FOUND" });
      const state = path.match(/^\/rooms\/(.+)\/state$/);
      if (state) stateReads.push(state[1]);
      if (state) return rooms.has(state[1]) ? reply(200, rooms.get(state[1]).events) : reply(403, {});
      if (req.method === "POST" && path === "/createRoom") {
        write();
        const id = `!dm${++counter}:${HS}`;
        rooms.set(id, { events: [{ type: "m.room.create", state_key: "", origin_server_ts: now() + counter, content: { creator: ME, ...body.creation_content } }] });
        if (body.preset !== "private_chat") return reply(400, {});
        member(id, ME, "join");
        rooms.get(id).body = body;
        if (body.initial_state?.some(e => e.type === "m.room.encryption")) rooms.get(id).events.push({ type: "m.room.encryption", state_key: "", content: {} });
        return reply(200, { room_id: id });
      }
      const invite = path.match(/^\/rooms\/(.+)\/invite$/);
      if (invite) { write(); if (config.failInvite) return reply(403, { errcode: "M_FORBIDDEN" }); member(invite[1], body.user_id, "invite"); rooms.get(invite[1]).invited = body; return reply(200, {}); }
      const put = path.match(/^\/rooms\/(.+)\/send\/m\.room\.message\/(.+)$/);
      if (put) {
        write();
        const r = rooms.get(put[1]);
        r.sent ??= new Map();
        if (!r.sent.has(put[2])) r.sent.set(put[2], { id: `$e${counter}-${r.sent.size + 1}`, body });
        return reply(200, { event_id: r.sent.get(put[2]).id });
      }
      reply(404, { errcode: "M_NOT_FOUND" });
    });
  });
  await new Promise(ok => server.listen(0, "127.0.0.1", ok));
  return {
    url: `http://127.0.0.1:${server.address().port}`, rooms, log, direct, stateReads,
    set failInvite(v) { config.failInvite = v; },
    seedDm(id, user, membership, { encrypted = false, created = 5, marker } = {}) {
      rooms.set(id, { events: [{ type: "m.room.create", state_key: "", origin_server_ts: created, content: { creator: ME, ...(marker ? { "org.gamecult.open_request": marker } : {}) } }] });
      member(id, ME, "join"); member(id, user, membership);
      if (encrypted) rooms.get(id).events.push({ type: "m.room.encryption", state_key: "", content: {} });
    },
    set: member,
    writes: pattern => log.filter(l => pattern.test(l)),
    close: () => { server.closeAllConnections(); return new Promise(ok => server.close(ok)); },
  };
}

const memoryState = () => { const box = { value: null }; return { load: async () => box.value, save: async v => { box.value = v; } }; };
const mouthFor = (hs, extra = {}) => createMouth({ persona: "eureka", homeserver: hs.url, serverName: HS, rooms: [ALIAS], token: "tok", state: memoryState(), brake: async () => true, syncTimeoutMs: 1, ...extra });
const ask = (m, requestId, user = META, text = "hello Meta, may I ask you something?", persona = "eureka") => m.openDirect({ requestId, persona, userId: user, text });
const outcome = r => r.outcome;

test("a new person: creates an unencrypted private direct room, invites with the line as the reason, says the line with the not-E2EE notice, and answers Opened", async () => {
  const hs = await homeserver();
  try {
    const m = mouthFor(hs);
    const r = await ask(m, "req-1");
    assert.equal(r.outcome, "Opened");
    const [roomId] = [...hs.rooms.keys()];
    assert.equal(r.roomId, roomId);
    const room = hs.rooms.get(roomId);
    assert.equal(room.body.preset, "private_chat");
    assert.equal(room.body.is_direct, true);
    assert.equal(room.body.creation_content["m.federate"], false);
    assert.equal(room.body.creation_content["org.gamecult.open_request"], "req-1");
    assert.ok(!JSON.stringify(room.body).includes("m.room.encryption"), "the room is created without encryption");
    assert.ok(!room.events.some(e => e.type === "m.room.encryption"));
    assert.deepEqual(room.invited, { user_id: META, reason: "hello Meta, may I ask you something?" });
    const line = room.sent.get("req-1");
    assert.equal(r.eventId, line.id);
    assert.match(line.body.body, /not end-to-end encrypted/, "the first message says so");
    assert.ok(line.body.body.endsWith("hello Meta, may I ask you something?"));
    assert.deepEqual(hs.direct.value, { [META]: [roomId] }, "the room id is stored in m.direct");
    assert.deepEqual(hs.log.map(l => l.split(" ")[0] + " " + l.split("/").pop()), ["POST createRoom", "POST invite", "PUT req-1"], "order of writes");
  } finally { await hs.close(); }
});

test("the invite reason is the line cut to 300 bytes", async () => {
  const hs = await homeserver();
  try {
    await ask(mouthFor(hs), "req-1", META, "x".repeat(900));
    assert.equal(Buffer.byteLength([...hs.rooms.values()][0].invited.reason), 300);
  } finally { await hs.close(); }
});

test("only her own request is served, and only for a person: mismatch, empty text, a foreign server, herself, another Mind, an unknown account", async () => {
  const hs = await homeserver();
  try {
    const nova = `@nova:${HS}`;
    const m = mouthFor(hs, { isServed: id => id === nova });
    assert.equal(outcome(await ask(m, "r1", META, "hi", "nova")), "PersonaMismatch");
    assert.equal(outcome(await ask(m, "r2", META, "   ")), "EmptyText");
    assert.equal(outcome(await ask(m, "r3", "@meta:matrix.org")), "ForeignUser");
    assert.equal(outcome(await ask(m, "r3b", "@meta:gamecult.org.evil.example")), "ForeignUser");
    assert.equal(outcome(await ask(m, "r4", ME)), "NotAPerson");
    assert.equal(outcome(await ask(m, "r5", nova)), "NotAPerson");
    assert.equal(outcome(await ask(m, "r6", `@nobody:${HS}`)), "NoSuchUser");
    assert.deepEqual(hs.log, [], "no write for any refusal");
    assert.equal(hs.rooms.size, 0);
  } finally { await hs.close(); }
});

test("a held brake refuses before any Matrix call", async () => {
  const hs = await homeserver();
  try {
    const m = mouthFor(hs, { brake: async () => false });
    assert.equal(outcome(await ask(m, "r1")), "Brake");
    assert.deepEqual(hs.log, []);
    assert.equal(hs.rooms.size, 0);
  } finally { await hs.close(); }
});

test("the brake is read fresh before createRoom, before the invite and before the line: flipping it at any point stops every later write", async () => {
  for (let free = 1; free <= 5; free++) {
    const hs = await homeserver();
    try {
      let reads = 0;
      const events = [];
      const m = mouthFor(hs, { brake: async () => { const ok = ++reads <= free; events.push(ok ? "free" : "held"); return ok; } });
      const originalLog = hs.log;
      const push = originalLog.push.bind(originalLog);
      originalLog.push = (...x) => { events.push(`write ${x[0]}`); return push(...x); };
      await ask(m, "r1");
      let held = false;
      for (const e of events) {
        if (e === "held") held = true;
        else if (held && e.startsWith("write")) assert.fail(`free=${free}: a write after the brake was held: ${e}`);
      }
      const writes = events.filter(e => e.startsWith("write"));
      for (let i = 0; i < events.length; i++) if (events[i].startsWith("write")) assert.equal(events[i - 1], "free", `free=${free}: ${events[i]} was not immediately preceded by a released brake read`);
      assert.equal(writes.length, Math.min(3, free - 1), `free=${free}: reads 2, 3 and 4 each gate exactly the createRoom, the invite and the line`);
    } finally { await hs.close(); }
  }
});

test("a second request for a person who accepted says the line in the same room and answers Existing, with no new room", async () => {
  const hs = await homeserver();
  try {
    const m = mouthFor(hs);
    const first = await ask(m, "r1");
    hs.set(first.roomId, META, "join"); // Meta accepts
    const again = await ask(m, "r2", META, "second line");
    assert.deepEqual([again.outcome, again.roomId], ["Existing", first.roomId]);
    assert.equal(hs.rooms.size, 1);
    const sent = hs.rooms.get(first.roomId).sent.get("r2");
    assert.equal(again.eventId, sent.id);
    assert.equal(sent.body.body, "second line", "no notice again after the first message");
  } finally { await hs.close(); }
});

test("a repeat of the same request id after a crash resumes in the room it made: one room, one line, Opened again", async () => {
  const hs = await homeserver();
  try {
    const first = await ask(mouthFor(hs), "r1");
    const again = await ask(mouthFor(hs), "r1"); // a restarted process: no memory of the first
    assert.deepEqual([again.outcome, again.roomId, again.eventId], ["Opened", first.roomId, first.eventId]);
    assert.equal(hs.rooms.size, 1);
    assert.equal(hs.writes(/createRoom/).length, 1);
    assert.equal(hs.writes(/\/invite/).length, 1);
  } finally { await hs.close(); }
});

test("an invite the person has not answered is InvitePending: nothing new is written", async () => {
  const hs = await homeserver();
  try {
    const m = mouthFor(hs);
    await ask(m, "r1");
    const before = hs.log.length;
    const again = await ask(m, "r2");
    assert.equal(again.outcome, "InvitePending");
    assert.equal(hs.log.length, before);
  } finally { await hs.close(); }
});

test("a decline stands: leaving or banning is Declined, for a restarted mouth too, until the person invites her", async () => {
  for (const gone of ["leave", "ban"]) {
    const hs = await homeserver();
    try {
      const first = await ask(mouthFor(hs), "r1");
      hs.set(first.roomId, META, gone);
      const before = hs.log.length;
      for (const m of [mouthFor(hs), mouthFor(hs)]) {
        assert.equal(outcome(await ask(m, `again-${Math.random()}`)), "Declined", gone);
      }
      assert.equal(hs.log.length, before, "no room, invite or line");
      // the person writes first: she is invited into a new room and joins; now there is a live DM
      hs.seedDm("!theirs:gamecult.org", META, "join", { created: 99999 });
      const answer = await ask(mouthFor(hs), "r-after");
      assert.deepEqual([answer.outcome, answer.roomId], ["Existing", "!theirs:gamecult.org"]);
    } finally { await hs.close(); }
  }
});

test("an invite the homeserver refuses leaves the room made; the retry invites into that same room instead of making another", async () => {
  const hs = await homeserver({ failInvite: true });
  try {
    assert.equal(outcome(await ask(mouthFor(hs), "r1")), "SendFailed");
    assert.equal(hs.rooms.size, 1);
    hs.failInvite = false;
    const retry = await ask(mouthFor(hs), "r2");
    assert.equal(retry.outcome, "Opened");
    assert.equal(hs.rooms.size, 1, "no second room");
    assert.equal(retry.roomId, [...hs.rooms.keys()][0]);
    assert.equal(hs.rooms.get(retry.roomId).invited.user_id, META);
  } finally { await hs.close(); }
});

test("one creation per person per day: a second creation the same UTC day is DailyLimit, the next UTC day it is allowed", async () => {
  let t = Date.parse("2026-11-10T10:00:00Z");
  const hs = await homeserver({ now: () => t });
  try {
    const m = mouthFor(hs, { now: () => t });
    assert.equal(outcome(await ask(m, "r1")), "Opened");
    hs.rooms.clear(); // she left the room: no DM with them is found now, only the day's record stands
    assert.equal(outcome(await ask(m, "r2")), "DailyLimit");
    assert.equal(hs.writes(/createRoom/).length, 1);
    t += 24 * 3600 * 1000;
    assert.equal(outcome(await ask(m, "r3")), "Opened");
    assert.equal(outcome(await ask(m, "r4", `@nova:${HS}`)), "Opened", "the limit is per person");
  } finally { await hs.close(); }
});

test("an encrypted direct room is not used: the line is never sent into it and a new unencrypted room is made", async () => {
  const hs = await homeserver();
  try {
    hs.seedDm("!old:gamecult.org", META, "join", { encrypted: true });
    const r = await ask(mouthFor(hs), "r1");
    assert.equal(r.outcome, "Opened");
    assert.notEqual(r.roomId, "!old:gamecult.org");
    assert.equal(hs.rooms.get("!old:gamecult.org").sent, undefined);
  } finally { await hs.close(); }
});

test("a homeserver failure answers SendFailed and writes nothing the brake or a refusal should have stopped", async () => {
  const hs = await homeserver();
  try {
    const m = mouthFor(hs, { homeserver: "http://127.0.0.1:9" });
    assert.equal(outcome(await ask(m, "r1")), "SendFailed");
  } finally { await hs.close(); }
});

test("two requests for one person at once make one room: opens for a person run one at a time", async () => {
  const hs = await homeserver();
  try {
    const m = mouthFor(hs);
    const answers = await Promise.all([ask(m, "a"), ask(m, "b")]);
    assert.equal(hs.writes(/createRoom/).length, 1);
    assert.deepEqual(answers.map(outcome).sort(), ["InvitePending", "Opened"]);
    const other = await Promise.all([ask(m, "c", `@nova:${HS}`), ask(m, "d", META)]);
    assert.equal(other[0].outcome, "Opened", "another person is not held behind");
  } finally { await hs.close(); }
});

test("a decline stands in an encrypted room too, and a pending invite there is still pending: no new room, no line", async () => {
  for (const [membership, expected] of [["leave", "Declined"], ["invite", "InvitePending"]]) {
    const hs = await homeserver();
    try {
      hs.seedDm("!enc:gamecult.org", META, membership, { encrypted: true });
      assert.equal(outcome(await ask(mouthFor(hs), "r1")), expected);
      assert.deepEqual(hs.log, []);
    } finally { await hs.close(); }
  }
});

test("the not-E2EE notice reaches the person even when the first line was held back: a later line into the room this path made carries it once", async () => {
  const hs = await homeserver();
  try {
    let reads = 0;
    const m = mouthFor(hs, { brake: async () => ++reads <= 3 }); // top, createRoom, invite: then the brake is held before the line
    assert.equal(outcome(await ask(m, "r1")), "Brake");
    const roomId = [...hs.rooms.keys()][0];
    assert.equal(hs.rooms.get(roomId).sent, undefined, "the invite went out, the line did not");
    hs.set(roomId, META, "join");
    const answer = await ask(mouthFor(hs), "r2", META, "second");
    assert.deepEqual([answer.outcome, answer.roomId], ["Existing", roomId]);
    assert.match(hs.rooms.get(roomId).sent.get("r2").body.body, /^This room is not end-to-end encrypted.*\n\nsecond$/s);
    await ask(mouthFor(hs), "r3", META, "third"); // another process: the notice is repeated at worst once, never lost
    const sameProcess = mouthFor(hs);
    await ask(sameProcess, "r4", META, "fourth");
    await ask(sameProcess, "r5", META, "fifth");
    assert.equal(hs.rooms.get(roomId).sent.get("r5").body.body, "fifth", "once the notice went out in this process it is not repeated");
  } finally { await hs.close(); }
});

test("the lookup finds a DM only: a group room with the person is not one, and project rooms are never scanned", async () => {
  const hs = await homeserver();
  try {
    hs.seedDm("!group:gamecult.org", META, "join");
    hs.set("!group:gamecult.org", `@third:${HS}`, "join"); // three members
    const r = await ask(mouthFor(hs), "r1");
    assert.equal(r.outcome, "Opened");
    assert.notEqual(r.roomId, "!group:gamecult.org");
    assert.equal(hs.rooms.get("!group:gamecult.org").sent, undefined);
    assert.ok(!hs.stateReads.includes("!project:gamecult.org"), "a configured project room is not read as a DM candidate");
  } finally { await hs.close(); }
});

test("another Mind this process serves is not a person, even before that Mind's own mouth has started", async () => {
  const hs = await homeserver();
  try {
    const nova = `@nova:${HS}`;
    let asked = null;
    const m = mouthFor(hs, { isServed: async id => { asked = id; return id === nova; } });
    assert.equal(outcome(await ask(m, "r1", nova)), "NotAPerson");
    assert.equal(asked, nova);
  } finally { await hs.close(); }
});

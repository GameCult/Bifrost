// Follow (docs/bridge.md, "Follow"): her own post in a public room anchors it for W; those who spoke there in the W before it (and whoever it tagged or
// replied to) are counted, and their later messages are basis "follow", at most K an hour per (peer, room), and only with the mouth's follow flag. The homeserver is an in-process fetch port:
// a scripted /sync (one reply per round), the room state and the events a reply target is read from.
import test from "node:test";
import assert from "node:assert/strict";
import { createMouth, followFromEnv } from "../tools/persona-matrix-mouth.mjs";
import * as docs from "../tools/persona-chat-documents.mjs";

const HS = "gamecult.test";
const ALIAS = `#swarm:${HS}`;
const ROOM = "!swarm:gamecult.test";
const OTHER_ALIAS = `#side:${HS}`;
const OTHER_ROOM = "!side:gamecult.test";
const ME = `@eureka:${HS}`;
const KIKO = `@kiko:${HS}`;
const STRANGER = `@stranger:${HS}`;
const LENA = `@lena:${HS}`;
const PLATFORM = ["disc", "ord"].join("");
const SEC = 1000, W = 600 * SEC, T0 = 1_000_000_000_000;
const msg = (id, sender, ts, content = {}) => ({ type: "m.room.message", event_id: id, sender, origin_server_ts: ts, content: { msgtype: "m.text", body: `line ${id}`, ...content } });
const tagKiko = { "m.mentions": { user_ids: [KIKO] } };

// rounds: sync replies after the baseline, each { [roomId]: events }. state: room id -> state events.
function world({ rounds, state = {}, stateStatus = {}, hangState = false, stateDelay = 0, stateFailures = 0, baselineEvents = null, onSync = null, follow = true, rooms = [ALIAS, OTHER_ALIAS], direct } = {}) {
  const seen = [];
  const known = new Map();
  let round = 0;
  const reply = (status, body) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
  const hsFetch = async (url, init = {}) => {
    const u = new URL(url);
    const p = decodeURIComponent(u.pathname).replace(/^\/_matrix\/client\/v[13]/, "");
    seen.push(`${init.method ?? "GET"} ${p}`);
    if (p === "/account/whoami") return reply(200, { user_id: ME });
    if (p === "/joined_rooms") return reply(200, { joined_rooms: [ROOM, OTHER_ROOM] });
    if (p.includes("/account_data/m.direct")) return direct ? reply(200, direct) : reply(404, { errcode: "M_NOT_FOUND" });
    if (p.startsWith("/directory/room/")) return reply(200, { room_id: { [ALIAS]: ROOM, [OTHER_ALIAS]: OTHER_ROOM }[p.slice(16)] });
    if (p.startsWith("/presence/") || p.includes("/receipt/")) return reply(200, {});
    const st = p.match(/^\/rooms\/([^/]+)\/state$/);
    if (st && stateDelay > 0) await new Promise(r => setTimeout(r, stateDelay));
    if (st && hangState) return new Promise(() => {});
    if (st && stateFailures > 0) { stateFailures--; return reply(500, { errcode: "M_UNKNOWN" }); }
    if (st) return stateStatus[st[1]] ? reply(stateStatus[st[1]], { errcode: "M_UNKNOWN" }) : reply(200, state[st[1]] ?? []);
    if (init.method === "PUT" && p.includes("/send/m.room.message/")) return reply(200, { event_id: "$sent1" });
    const ev = p.match(/^\/rooms\/([^/]+)\/event\/(.+)$/);
    if (ev) return known.has(ev[2]) ? reply(200, known.get(ev[2])) : reply(404, { errcode: "M_NOT_FOUND" });
    if (p === "/sync") {
      if (u.searchParams.get("since") === null) return reply(200, { next_batch: "b0", ...(baselineEvents ? { rooms: { join: Object.fromEntries(Object.entries(baselineEvents).map(([r, list]) => [r, { timeline: { events: list } }])) } } : {}) });
      onSync?.(setClock);
      const events = rounds[round++] ?? {};
      for (const list of Object.values(events)) for (const e of list) known.set(e.event_id, e);
      return reply(200, { next_batch: `b${round}`, rooms: { join: Object.fromEntries(Object.entries(events).map(([r, list]) => [r, { timeline: { events: list } }])) } });
    }
    if (p.includes("/messages")) return reply(200, { chunk: [] });
    return reply(404, { errcode: "M_NOT_FOUND" });
  };
  let clock = T0;
  const setClock = t => { clock = t; };
  const mouth = createMouth({
    persona: "eureka", homeserver: "http://hs.test", serverName: HS, rooms, token: "tok", fetch: hsFetch, follow,
    state: { load: async () => null, save: async () => {} }, brake: async () => true, log: () => {}, syncTimeoutMs: 1, now: () => clock,
  });
  return { mouth, seen, clock: () => clock, at: t => { clock = t; }, basisOfRound: async () => (await mouth.syncOnce()).map(d => [d.eventId, d.basis]) };
}
const run = async (w, n) => { await w.mouth.syncOnce(); const out = []; for (let i = 0; i < n; i++) out.push(await w.basisOfRound()); return out; };

const go = async (w, t) => { w.at(t); return w.basisOfRound(); }; // the process clock moves with the round
const mine = (id, ts, content = {}) => msg(id, ME, ts, content);
const MIN = 60 * SEC;

test("follow: a counted sender is one who spoke in the 10 minutes before her post; 9 minutes counts, 11 does not; one who first speaks after does not", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$old", KIKO, T0 - 11 * MIN), msg("$edge", `@edge:${HS}`, T0 - 10.5 * MIN), msg("$recent", LENA, T0 - 9 * MIN)] },
    { [ROOM]: [mine("$mine", T0)] },
    { [ROOM]: [msg("$k", KIKO, T0 + MIN), msg("$l", LENA, T0 + 2 * MIN), msg("$s", STRANGER, T0 + 3 * MIN), msg("$e", `@edge:${HS}`, T0 + 3 * MIN)] },
  ] });
  const out = await run(w, 3);
  assert.deepEqual(out[2].sort(), [["$e", null], ["$k", null], ["$l", "follow"], ["$s", null]]);
});

test("follow: a post's tag and reply target are counted though they never spoke; she herself never is", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$theirs", LENA, T0 - 5 * MIN)] },
    { [ROOM]: [mine("$mine", T0, { "m.mentions": { user_ids: [KIKO, ME] }, "m.relates_to": { "m.in_reply_to": { event_id: "$theirs" } } })] },
    { [ROOM]: [msg("$k", KIKO, T0 + MIN), msg("$s", STRANGER, T0 + MIN)] },
  ] });
  assert.deepEqual((await run(w, 3))[2], [["$k", "follow"], ["$s", null]]);
});


test("follow: the chain dies when she stops speaking; a follow does not extend W; speaking again re-anchors and re-snapshots", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - MIN)] },
    { [ROOM]: [mine("$mine", T0)] },
    { [ROOM]: [msg("$a", KIKO, T0 + 9 * MIN)] },        // inside W: follow
    { [ROOM]: [msg("$b", KIKO, T0 + 10 * MIN + SEC)] }, // past W from her post though a follow came at 9 min
    { [ROOM]: [mine("$again", T0 + 11 * MIN)] },
    { [ROOM]: [msg("$c", KIKO, T0 + 20 * MIN), msg("$d", LENA, T0 + 20 * MIN)] },
  ] });
  await w.mouth.syncOnce();
  assert.deepEqual(await go(w, T0 - MIN), [["$k0", null]]);
  assert.deepEqual(await go(w, T0), []);
  assert.deepEqual(await go(w, T0 + 9 * MIN), [["$a", "follow"]]);
  assert.deepEqual(await go(w, T0 + 10 * MIN + SEC), [["$b", null]]);
  assert.deepEqual(await go(w, T0 + 11 * MIN), []);
  assert.deepEqual(await go(w, T0 + 20 * MIN), [["$c", "follow"], ["$d", null]]); // lena never spoke before the second post either
});

test("follow: the window needs this process's clock too: a message stamped inside it but read long after is not a follow", async () => {
  const w = world({ rounds: [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [mine("$mine", T0)] }, { [ROOM]: [msg("$forged", KIKO, T0 + MIN)] }] });
  await w.mouth.syncOnce();
  await go(w, T0 - MIN); await go(w, T0);
  assert.deepEqual(await go(w, T0 + 30 * MIN), [["$forged", null]]);
});

test("follow: her mouth send anchors at send time, and its /sync echo does not anchor again", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - MIN)] },
    { [ROOM]: [msg("$sent1", ME, T0 + 8 * MIN, { body: "echo" })] },
    { [ROOM]: [msg("$late", KIKO, T0 + 12 * MIN)] },
  ] });
  await w.mouth.syncOnce(); await w.basisOfRound();
  const sent = await w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  assert.equal(sent.refusal, null);
  w.at(T0 + 8 * MIN);
  await w.basisOfRound(); // the echo, 8 minutes on: a second anchor would carry the window to 18 minutes
  w.at(T0 + 12 * MIN);
  assert.deepEqual(await w.basisOfRound(), [["$late", null]]);
});

test("follow: a send from the mouth anchors; a send into a DM or an unreadable room does not", async () => {
  const mk = opts => world({ ...opts, rounds: [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [msg("$k1", KIKO, T0 + MIN)] }] });
  const ok = mk({});
  await ok.mouth.syncOnce(); await ok.basisOfRound();
  await ok.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  assert.deepEqual(await ok.basisOfRound(), [["$k1", "follow"]]);
  const dm = mk({ direct: { [KIKO]: [ROOM] } });
  await dm.mouth.syncOnce(); await dm.basisOfRound();
  await dm.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ROOM, text: "hello" });
  assert.deepEqual(await dm.basisOfRound(), [["$k1", "direct"]]);
});

test("follow: a DM, an encrypted room and a bridged room anchor nothing; an unreadable or non-array state is not public", async () => {
  const rounds = () => [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [mine("$mine", T0)] }, { [ROOM]: [msg("$k", KIKO, T0 + MIN)] }];
  for (const state of [[{ type: "m.room.encryption", content: {} }], [{ type: "m.bridge", content: {} }], [{ type: "uk.half-shot.bridge", content: {} }], { not: "an array" }]) {
    assert.deepEqual((await run(world({ state: { [ROOM]: state }, rounds: rounds() }), 3))[2], [["$k", null]]);
  }
  assert.deepEqual((await run(world({ stateStatus: { [ROOM]: 500 }, rounds: rounds() }), 3))[2], [["$k", null]]);
  assert.deepEqual((await run(world({ direct: { [KIKO]: [ROOM] }, rounds: rounds() }), 3))[2], [["$k", "direct"]]);
  // plaintext and unbridged is public, whatever else it is (an invite-only room included): the state holds no join rule the mouth reads
  assert.deepEqual((await run(world({ state: { [ROOM]: [{ type: "m.room.join_rules", content: { join_rule: "invite" } }] }, rounds: rounds() }), 3))[2], [["$k", "follow"]]);
});

test("follow: a bridged sender or event is neither counted nor followed", async () => {
  const puppet = `@_${PLATFORM}_123:${HS}`;
  const w = world({ rounds: [{ [ROOM]: [msg("$p0", puppet, T0 - MIN), msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [mine("$mine", T0, { "m.mentions": { user_ids: [puppet] } })] },
    { [ROOM]: [msg("$p", puppet, T0 + MIN), msg("$k", KIKO, T0 + MIN, { [`${PLATFORM}.relay`]: true })] }] });
  assert.deepEqual((await run(w, 3))[2].sort(), [["$k", null], ["$p", null]]);
});

test("follow: at most three an hour per room and peer, counted independently; a half hour does not reset it; an hour later it is follow again", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - SEC), msg("$l0", LENA, T0 - SEC)], [OTHER_ROOM]: [msg("$ko0", KIKO, T0 - SEC)] },
    { [ROOM]: [mine("$m1", T0 + MIN)], [OTHER_ROOM]: [msg("$mo1", ME, T0 + MIN)] },
    { [ROOM]: [msg("$k1", KIKO, T0 + MIN + SEC)], [OTHER_ROOM]: [msg("$ko1", KIKO, T0 + MIN + SEC)] },
    { [ROOM]: [mine("$m2", T0 + 2 * MIN)] }, { [ROOM]: [msg("$k2", KIKO, T0 + 2 * MIN + SEC)] },
    { [ROOM]: [mine("$m3", T0 + 3 * MIN)] }, { [ROOM]: [msg("$k3", KIKO, T0 + 3 * MIN + SEC)] },
    { [ROOM]: [mine("$m4", T0 + 4 * MIN)] }, { [ROOM]: [msg("$k4", KIKO, T0 + 4 * MIN + SEC), msg("$l4", LENA, T0 + 4 * MIN + SEC)] },
    { [ROOM]: [msg("$k30", KIKO, T0 + 30 * MIN), mine("$m31", T0 + 31 * MIN)] }, { [ROOM]: [msg("$k31", KIKO, T0 + 31 * MIN + SEC)] },
    { [ROOM]: [msg("$k60", KIKO, T0 + 61 * MIN), mine("$m62", T0 + 62 * MIN)] }, { [ROOM]: [msg("$k62", KIKO, T0 + 62 * MIN + SEC)] },
  ] });
  await w.mouth.syncOnce();
  const seen = [];
  const steps = [0, 1, 1, 2, 2, 3, 3, 4, 4, 31, 31, 62, 62];
  for (const m of steps) seen.push(...await go(w, T0 + m * MIN + 2 * SEC));
  const got = Object.fromEntries(seen.filter(([id]) => /^\$(k|ko|l)\d/.test(id)).map(([id, b]) => [id, b]));
  assert.deepEqual(got, { $k0: null, $l0: null, $ko0: null, $k1: "follow", $ko1: "follow", $k2: "follow", $k3: "follow", $k4: null, $l4: "follow", $k30: null, $k31: null, $k60: null, $k62: "follow" });
});

test("follow: an edit and a notice are never follow; a room-basis message from a counted sender is; a tag keeps its own basis", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - MIN)] },
    { [ROOM]: [mine("$mine", T0)] },
    { [ROOM]: [
      msg("$edit", KIKO, T0 + SEC, { "m.new_content": { msgtype: "m.text", body: "x" }, "m.relates_to": { rel_type: "m.replace", event_id: "$k0" } }),
      msg("$notice", KIKO, T0 + SEC, { msgtype: "m.notice" }),
      msg("$room", KIKO, T0 + SEC, { "m.mentions": { room: true } }),
      msg("$tag", KIKO, T0 + SEC, { "m.mentions": { user_ids: [ME] } }),
    ] },
  ] });
  assert.deepEqual((await run(w, 3))[2].sort(), [["$edit", null], ["$notice", null], ["$room", "follow"], ["$tag", "tag"]]);
});

test("follow: flag off emits nothing new and reads nothing extra; a fresh mouth has no engagement", async () => {
  const rounds = [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [mine("$mine", T0)] }, { [ROOM]: [msg("$k", KIKO, T0 + MIN)] }];
  const off = world({ follow: false, rounds });
  assert.deepEqual((await run(off, 3))[2], [["$k", null]]);
  assert.ok(!off.seen.some(l => l.endsWith("/state")), "off: no state read");
  const restarted = world({ rounds: [{ [ROOM]: [msg("$k", KIKO, T0 + MIN)] }] });
  assert.deepEqual((await run(restarted, 1))[0], [["$k", null]]);
  assert.equal(followFromEnv({ CHAT_FOLLOW: "1" }), true);
  for (const v of [undefined, "0", "true", "yes", ""]) assert.equal(followFromEnv({ CHAT_FOLLOW: v }), false);
});

test("follow: the basis is a name the delivery document accepts", () => {
  assert.ok(docs.bases.includes("follow"));
  const d = { seq: 1, roomAlias: ALIAS, roomId: ROOM, eventId: "$e", sender: KIKO, senderDisplay: "kiko", text: "x", atUnixMs: 1, context: [], image: null, mentions: [], isDirect: false, basis: "follow", form: "message" };
  assert.equal(docs.parseDeliveryV3(docs.deliveryV3Tuple(d)).basis, "follow");
});

const tick = async () => { for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r)); };

test("follow: a send receipt is not delayed by a room-state read that hangs or fails; the anchor happens in the background", async () => {
  for (const opts of [{ hangState: true }, { stateStatus: { [ROOM]: 500 } }]) {
    const w = world({ ...opts, rounds: [] });
    await w.mouth.syncOnce();
    const sent = await Promise.race([w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" }), new Promise(r => setTimeout(() => r("late"), 500))]);
    assert.equal(sent.refusal, null);
  }
  const ok = world({ rounds: [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [msg("$k1", KIKO, T0 + MIN)] }] });
  await ok.mouth.syncOnce(); await ok.basisOfRound();
  await ok.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  await tick();
  assert.deepEqual(await ok.basisOfRound(), [["$k1", "follow"]]);
});

test("follow: the flag off makes a send cost no extra call and no extra turn", async () => {
  const w = world({ follow: false, rounds: [] });
  await w.mouth.syncOnce();
  const before = w.seen.length;
  await w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  await tick();
  assert.ok(!w.seen.slice(before).some(l => l.endsWith("/state")));
});

test("follow: a homeserver clock up to 60 s behind still follows; 61 s early does not; the very end of the window is out, 1 ms before it is in", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - MIN), msg("$l0", LENA, T0 - MIN), msg("$e0", `@e:${HS}`, T0 - MIN), msg("$f0", `@f:${HS}`, T0 - MIN)] },
    { [ROOM]: [mine("$mine", T0)] },
    { [ROOM]: [msg("$x1", KIKO, T0 - 60 * SEC), msg("$x2", LENA, T0 - 61 * SEC), msg("$x3", `@e:${HS}`, T0 + W - 1), msg("$x4", `@f:${HS}`, T0 + W)] },
  ] });
  await w.mouth.syncOnce(); await go(w, T0 - MIN); await go(w, T0);
  assert.deepEqual((await go(w, T0 + 5 * SEC)).sort(), [["$x1", "follow"], ["$x2", null], ["$x3", "follow"], ["$x4", null]]);
});

test("follow: a window that ended while the pass was being read is out, whatever the message says", async () => {
  let jump = null;
  const w = world({ onSync: set => { if (jump !== null) set(jump); }, rounds: [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [mine("$mine", T0)] }, { [ROOM]: [msg("$x", KIKO, T0 + MIN)] }] });
  await w.mouth.syncOnce(); await go(w, T0 - MIN);
  const keep = w.mouth.followState;
  w.at(T0); await w.basisOfRound(); // her post; the next sync moves the clock after the purge
  assert.equal(keep().engagements.length, 1);
  jump = T0 + 30 * MIN; w.at(T0); // the purge at the start of the pass sees T0; the clock moves while the pass reads
  assert.deepEqual(await w.basisOfRound(), [["$x", null]]);
});

test("follow: the cap counts only the hour before the message", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - SEC)] },
    ...[1, 2, 3].flatMap(n => [{ [ROOM]: [mine(`$m${n}`, T0 + n * MIN)] }, { [ROOM]: [msg(`$x${n}`, KIKO, T0 + n * MIN + SEC)] }]),
    { [ROOM]: [msg("$k90", KIKO, T0 + 89 * MIN), mine("$m90", T0 + 90 * MIN)] }, { [ROOM]: [msg("$x90", KIKO, T0 + 90 * MIN + SEC)] },
  ] });
  await w.mouth.syncOnce(); await go(w, T0);
  for (const n of [1, 1, 2, 2, 3, 3]) await go(w, T0 + n * MIN + SEC);
  assert.deepEqual(await go(w, T0 + 30 * MIN), [["$k90", null]]); // stamps run ahead of this process's clock, so only the cap's own hour bound can tell
  assert.deepEqual(await go(w, T0 + 30 * MIN), [["$x90", "follow"]]);
});

test("follow: whoever her reply quotes is counted though they spoke long ago; a thread fallback pointer names no one", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$old", LENA, T0 - 30 * MIN), msg("$ghost", `@ghost:${HS}`, T0 - 30 * MIN)] },
    { [ROOM]: [mine("$mine", T0, { "m.relates_to": { "m.in_reply_to": { event_id: "$old" } } })] },
    { [ROOM]: [msg("$x1", LENA, T0 + MIN)] },
    { [ROOM]: [msg("$old2", KIKO, T0 + 2 * MIN)] },
    { [ROOM]: [mine("$mine2", T0 + 3 * MIN, { "m.relates_to": { rel_type: "m.thread", event_id: "$old", is_falling_back: true, "m.in_reply_to": { event_id: "$ghost" } } })] },
    { [ROOM]: [msg("$x2", STRANGER, T0 + 4 * MIN)] },
  ] });
  await w.mouth.syncOnce();
  await go(w, T0 - 30 * MIN); await go(w, T0);
  assert.deepEqual(await go(w, T0 + MIN), [["$x1", "follow"]]);
  await go(w, T0 + 2 * MIN);
  await go(w, T0 + 3 * MIN);
  const [engagement] = w.mouth.followState().engagements;
  assert.ok(engagement.counted.includes(KIKO), "kiko spoke 1 minute before the second post");
  assert.ok(!engagement.counted.includes(`@ghost:${HS}`), "a fallback pointer names no one");
});

test("follow: someone stamped after her send is not counted as having spoken before it", async () => {
  const w = world({ rounds: [{ [ROOM]: [msg("$ahead", KIKO, T0 + MIN)] }, { [ROOM]: [msg("$x", KIKO, T0 + 2 * MIN)] }] });
  await w.mouth.syncOnce(); await w.basisOfRound();
  await w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  await tick();
  assert.deepEqual(w.mouth.followState().engagements[0].counted, []);
  assert.deepEqual(await go(w, T0 + 2 * MIN), [["$x", null]]);
});

test("follow: the ring keeps the newest 200 senders of a room; a flood pushes out the oldest, not the newest", async () => {
  const flood = Array.from({ length: 250 }, (_, i) => msg(`$f${i}`, `@u${i}:${HS}`, T0 - 5 * MIN + i));
  const w = world({ rounds: [{ [ROOM]: flood }, { [ROOM]: [mine("$mine", T0)] }, { [ROOM]: [msg("$x0", `@u0:${HS}`, T0 + MIN), msg("$x249", `@u249:${HS}`, T0 + MIN)] }] });
  await w.mouth.syncOnce(); await w.basisOfRound();
  assert.equal(w.mouth.followState().spoke[0].senders.length, 200);
  await go(w, T0);
  assert.deepEqual(await go(w, T0 + MIN + SEC), [["$x0", null], ["$x249", "follow"]]);
  assert.equal(w.mouth.followState().engagements[0].counted.length, 200);
});

test("follow: only senders, and only real ones, are remembered and counted: not her, not a bridged id, not an edit, not a notice, not a malformed id", async () => {
  const puppet = `@_${PLATFORM}_9:${HS}`;
  const w = world({ rounds: [
    { [ROOM]: [msg("$a", KIKO, T0 - 2 * MIN), mine("$self", T0 - 2 * MIN), msg("$b", puppet, T0 - 2 * MIN),
      msg("$c", LENA, T0 - 2 * MIN, { "m.relates_to": { rel_type: "m.replace", event_id: "$a" } }), msg("$d", STRANGER, T0 - 2 * MIN, { msgtype: "m.notice" })] },
    { [ROOM]: [mine("$mine", T0, { "m.mentions": { user_ids: [ME, puppet, "not-an-id", `@${"x".repeat(300)}:${HS}`, LENA] } })] },
  ] });
  await w.mouth.syncOnce(); await go(w, T0 - MIN);
  assert.deepEqual(w.mouth.followState().spoke, [{ room: ROOM, senders: [KIKO] }]);
  await go(w, T0);
  assert.deepEqual(w.mouth.followState().engagements[0].counted.sort(), [KIKO, LENA].sort());
});

test("follow: her own post in the baseline sync, which is history, anchors nothing", async () => {
  const w = world({ baselineEvents: { [ROOM]: [msg("$k0", KIKO, T0 - MIN), mine("$mine", T0)] }, rounds: [] });
  await w.mouth.syncOnce();
  assert.deepEqual(w.mouth.followState().engagements, []);
});

test("follow: every pass forgets what has aged out: anchors, speakers and cap times", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [mine("$mine", T0)] }, { [ROOM]: [msg("$x", KIKO, T0 + MIN)] }, {},
  ] });
  await w.mouth.syncOnce(); await go(w, T0 - MIN); await go(w, T0); await go(w, T0 + MIN + SEC);
  const s1 = w.mouth.followState();
  assert.equal(s1.engagements.length, 1); assert.equal(s1.followsGiven, 1); assert.equal(s1.spoke.length, 1);
  await go(w, T0 + 3 * 60 * MIN);
  assert.deepEqual(w.mouth.followState(), { engagements: [], spoke: [], followsGiven: 0 });
});

test("follow: a failed room-state read at send time leaves the post retryable: its echo anchors it", async () => {
  const w = world({ stateFailures: 1, rounds: [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [msg("$sent1", ME, T0, { body: "hello" })] }, { [ROOM]: [msg("$x", KIKO, T0 + MIN)] }] });
  await w.mouth.syncOnce(); await w.basisOfRound();
  await w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  await tick();
  assert.deepEqual(w.mouth.followState().engagements, [], "the read failed: nothing anchored yet");
  await go(w, T0);
  assert.equal(w.mouth.followState().engagements.length, 1, "the echo anchored it");
  assert.deepEqual(await go(w, T0 + MIN), [["$x", "follow"]]);
});

test("follow: a send and its echo in flight together anchor once, and a failure of that one read leaves the echo to retry", async () => {
  const w = world({ stateFailures: 1, rounds: [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [msg("$sent1", ME, T0, { body: "hello" })] }] });
  await w.mouth.syncOnce(); await w.basisOfRound();
  const sending = w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  await sending;
  await w.basisOfRound(); // the echo, possibly while the send's read is still in flight
  await tick();
  assert.equal(w.mouth.followState().engagements.length, 1);
  assert.equal(w.seen.filter(l => l.endsWith("/state")).length, 2, "one failed read, one that worked; no third");
});

test("follow: a not-public verdict is recorded: the room is not read again for each post or pass", async () => {
  const w = world({ state: { [ROOM]: [{ type: "m.bridge", content: {} }] }, rounds: [
    { [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [msg("$sent1", ME, T0)] }, { [ROOM]: [mine("$a", T0 + SEC), msg("$x", KIKO, T0 + 2 * SEC)] }, { [ROOM]: [mine("$b", T0 + 3 * SEC)] },
  ] });
  await w.mouth.syncOnce(); await w.basisOfRound();
  await w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" });
  await tick();
  for (const t of [0, 0, 0]) await go(w, T0 + t);
  assert.equal(w.seen.filter(l => l.endsWith("/state")).length, 1);
  assert.deepEqual(w.mouth.followState().engagements, []);
});

test("follow: an echo that arrives while the send's anchor is still reading waits for it: one read, one snapshot", async () => {
  const w = world({ stateDelay: 80, rounds: [{ [ROOM]: [msg("$k0", KIKO, T0 - MIN)] }, { [ROOM]: [msg("$sent1", ME, T0 + 5 * SEC, { body: "hello" })] }] });
  await w.mouth.syncOnce(); await w.basisOfRound();
  await w.mouth.send({ requestId: "r1", persona: "eureka", roomAlias: ALIAS, text: "hello" }); // its anchor is now waiting on the state read
  await w.basisOfRound(); // the echo, meanwhile
  await new Promise(r => setTimeout(r, 200));
  assert.equal(w.seen.filter(l => l.endsWith("/state")).length, 1);
  assert.equal(w.mouth.followState().engagements[0].from, T0, "anchored at the send's time, not the echo's");
});

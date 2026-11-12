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
function world({ rounds, state = {}, stateStatus = {}, follow = true, rooms = [ALIAS, OTHER_ALIAS], direct } = {}) {
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
    if (st) return stateStatus[st[1]] ? reply(stateStatus[st[1]], { errcode: "M_UNKNOWN" }) : reply(200, state[st[1]] ?? []);
    if (init.method === "PUT" && p.includes("/send/m.room.message/")) return reply(200, { event_id: "$sent1" });
    const ev = p.match(/^\/rooms\/([^/]+)\/event\/(.+)$/);
    if (ev) return known.has(ev[2]) ? reply(200, known.get(ev[2])) : reply(404, { errcode: "M_NOT_FOUND" });
    if (p === "/sync") {
      if (u.searchParams.get("since") === null) return reply(200, { next_batch: "b0" });
      const events = rounds[round++] ?? {};
      for (const list of Object.values(events)) for (const e of list) known.set(e.event_id, e);
      return reply(200, { next_batch: `b${round}`, rooms: { join: Object.fromEntries(Object.entries(events).map(([r, list]) => [r, { timeline: { events: list } }])) } });
    }
    if (p.includes("/messages")) return reply(200, { chunk: [] });
    return reply(404, { errcode: "M_NOT_FOUND" });
  };
  let clock = T0;
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

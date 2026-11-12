// Follow (docs/bridge.md, "Follow"): an untagged message from someone she tagged or replied to, within W of her own post, is basis "follow",
// at most K an hour per (peer, room), public rooms only, and only with the mouth's follow flag. The homeserver is an in-process fetch port:
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
const PLATFORM = ["disc", "ord"].join("");
const SEC = 1000, W = 600 * SEC, T0 = 1_000_000_000_000;
const msg = (id, sender, ts, content = {}) => ({ type: "m.room.message", event_id: id, sender, origin_server_ts: ts, content: { msgtype: "m.text", body: `line ${id}`, ...content } });
const tagKiko = { "m.mentions": { user_ids: [KIKO] } };

// rounds: sync replies after the baseline, each { [roomId]: events }. state: room id -> state events.
function world({ rounds, state = {}, follow = true, rooms = [ALIAS, OTHER_ALIAS], direct } = {}) {
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
    if (st) return reply(200, state[st[1]] ?? []);
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
  return { mouth, seen, at: t => { clock = t; }, basisOfRound: async () => (await mouth.syncOnce()).map(d => [d.eventId, d.basis]) };
}
const run = async (w, n) => { await w.mouth.syncOnce(); const out = []; for (let i = 0; i < n; i++) out.push(await w.basisOfRound()); return out; };

test("follow: a peer she tagged, untagged and within W of her post, is follow; a stranger and a peer elsewhere are not", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$mine", ME, T0, tagKiko)] },
    { [ROOM]: [msg("$kiko", KIKO, T0 + 60 * SEC), msg("$stranger", STRANGER, T0 + 61 * SEC)], [OTHER_ROOM]: [msg("$kikoside", KIKO, T0 + 62 * SEC)] },
  ] });
  const [, second] = await run(w, 2);
  assert.deepEqual(second.sort(), [["$kiko", "follow"], ["$kikoside", null], ["$stranger", null]]);
});

test("follow: a reply of hers makes the author of the quoted event a peer", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$theirs", KIKO, T0 - 5 * SEC)] },
    { [ROOM]: [msg("$mine", ME, T0, { "m.relates_to": { "m.in_reply_to": { event_id: "$theirs" } } })] },
    { [ROOM]: [msg("$next", KIKO, T0 + 30 * SEC)] },
  ] });
  const out = await run(w, 3);
  assert.deepEqual(out[2], [["$next", "follow"]]);
});

test("follow: the chain dies when she stops speaking; a follow does not extend W; speaking again re-anchors", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$mine", ME, T0, tagKiko)] },
    { [ROOM]: [msg("$a", KIKO, T0 + 9 * 60 * SEC)] },        // inside W: follow
    { [ROOM]: [msg("$b", KIKO, T0 + W + SEC)] },             // after W from her post, though a follow came at 9 min: not follow
    { [ROOM]: [msg("$again", ME, T0 + 11 * 60 * SEC, tagKiko)] },
    { [ROOM]: [msg("$c", KIKO, T0 + 20 * 60 * SEC)] },       // inside W of the second post: follow
  ] });
  const out = await run(w, 5);
  assert.deepEqual(out.flat(), [["$a", "follow"], ["$b", null], ["$c", "follow"]]);
});

test("follow: a tag or reply to her keeps its own basis", async () => {
  const w = world({ rounds: [
    { [ROOM]: [msg("$mine", ME, T0, tagKiko)] },
    { [ROOM]: [msg("$t", KIKO, T0 + SEC, { "m.mentions": { user_ids: [ME] } })] },
  ] });
  assert.deepEqual((await run(w, 2))[1], [["$t", "tag"]]);
});

test("follow: a DM, an encrypted room and a bridged room open nothing and receive nothing", async () => {
  for (const state of [[{ type: "m.room.encryption", content: {} }], [{ type: "m.bridge", content: {} }], [{ type: "uk.half-shot.bridge", content: {} }]]) {
    const w = world({ state: { [ROOM]: state }, rounds: [
      { [ROOM]: [msg("$mine", ME, T0, tagKiko)] },
      { [ROOM]: [msg("$kiko", KIKO, T0 + SEC)] },
    ] });
    assert.deepEqual((await run(w, 2))[1], [["$kiko", null]]);
  }
  const dm = world({ direct: { [KIKO]: [ROOM] }, rounds: [{ [ROOM]: [msg("$mine", ME, T0, tagKiko)] }, { [ROOM]: [msg("$kiko", KIKO, T0 + SEC)] }] });
  assert.deepEqual((await run(dm, 2))[1], [["$kiko", "direct"]]);
});

test("follow: a bridged room, a bridged sender as peer, and a bridged event deliver no follow", async () => {
  const w2 = world({ state: { [ROOM]: [{ type: "m.bridge", content: {} }] }, rounds: [{ [ROOM]: [msg("$mine", ME, T0, tagKiko)] }, { [ROOM]: [msg("$kiko", KIKO, T0 + SEC)] }] });
  assert.deepEqual((await run(w2, 2))[1], [["$kiko", null]]);
  const puppet = `@_${PLATFORM}_123:${HS}`;
  const w3 = world({ rounds: [
    { [ROOM]: [msg("$mine", ME, T0, { "m.mentions": { user_ids: [puppet] } })] },
    { [ROOM]: [msg("$p", puppet, T0 + SEC)] },
  ] });
  assert.deepEqual((await run(w3, 2))[1], [["$p", null]]);
  const w4 = world({ rounds: [
    { [ROOM]: [msg("$mine", ME, T0, tagKiko)] },
    { [ROOM]: [msg("$kiko", KIKO, T0 + SEC, { [`${PLATFORM}.relay`]: true })] },
  ] });
  assert.deepEqual((await run(w4, 2))[1], [["$kiko", null]]);
});

test("follow: at most three an hour per peer and room; the fourth keeps its basis; an hour later it is follow again", async () => {
  const posts = n => ({ [ROOM]: [msg(`$me${n}`, ME, T0 + n * 5 * 60 * SEC, tagKiko), msg(`$k${n}`, KIKO, T0 + n * 5 * 60 * SEC + SEC)] });
  const w = world({ rounds: [posts(0), posts(1), posts(2), posts(3), { [ROOM]: [msg("$me9", ME, T0 + 70 * 60 * SEC, tagKiko), msg("$k9", KIKO, T0 + 70 * 60 * SEC + SEC)] }] });
  const out = (await run(w, 5)).flat();
  assert.deepEqual(out, [["$k0", "follow"], ["$k1", "follow"], ["$k2", "follow"], ["$k3", null], ["$k9", "follow"]]);
});

test("follow: flag off emits nothing new and reads nothing extra; a fresh mouth has no engagement", async () => {
  const rounds = [{ [ROOM]: [msg("$mine", ME, T0, tagKiko)] }, { [ROOM]: [msg("$kiko", KIKO, T0 + SEC)] }];
  const off = world({ follow: false, rounds });
  assert.deepEqual((await run(off, 2))[1], [["$kiko", null]]);
  assert.ok(!off.seen.some(l => l.endsWith("/state")), "off: no state read");
  const restarted = world({ rounds: [{ [ROOM]: [msg("$kiko", KIKO, T0 + SEC)] }] });
  assert.deepEqual((await run(restarted, 1))[0], [["$kiko", null]]);
  assert.equal(followFromEnv({ CHAT_FOLLOW: "1" }), true);
  for (const v of [undefined, "0", "true", "yes", ""]) assert.equal(followFromEnv({ CHAT_FOLLOW: v }), false);
});

test("follow: the basis is a name the delivery document accepts", () => {
  assert.ok(docs.bases.includes("follow"));
  const d = { seq: 1, roomAlias: ALIAS, roomId: ROOM, eventId: "$e", sender: KIKO, senderDisplay: "kiko", text: "x", atUnixMs: 1, context: [], image: null, mentions: [], isDirect: false, basis: "follow", form: "message" };
  assert.equal(docs.parseDeliveryV3(docs.deliveryV3Tuple(d)).basis, "follow");
});

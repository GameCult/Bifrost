// A Persona asks her mouth to keep one named Matrix file (gamecult.persona.chat_fetch_request.v1): the mouth reads the event itself,
// checks it, stores the bytes and posts the contract's inert receipt as her. The homeserver here is an in-process fetch port that keeps
// room state, events, relations and idempotent sends the way Synapse does, records every call, and can stall a call until the caller's
// own signal aborts it.
import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { createMouth, createRoomGate, fetchFromEnv } from "../tools/persona-matrix-mouth.mjs";
import { createImageStore, MAX_BYTES } from "../tools/persona-chat-images.mjs";
import * as docs from "../tools/persona-chat-documents.mjs";
import { png, jpeg, svg } from "./image-bytes.mjs";
import { personaStoreRoot } from "./image-bytes.mjs";

const HS = "gamecult.org";
const ROOM = "!room1:gamecult.org";
const ALIAS = `#swarm:${HS}`;
const ME = `@eureka:${HS}`;
const OTHER = `@kiko:${HS}`;
const FILE = "$file1";
const ROOT = "$album1";
const TOKEN = "tok-secret-eureka";
const sha = bytes => createHash("sha256").update(bytes).digest("hex");
const PLATFORM = ["disc", "ord"].join("");
const IMG = png(8, 8);
const IMG_SHA = sha(IMG);
const thread = { rel_type: "m.thread", event_id: ROOT };
const fileContent = (extra = {}) => ({ msgtype: "m.image", body: "cat.png", url: "mxc://gamecult.org/abc", info: { sha256: IMG_SHA, mimetype: "image/png", size: IMG.length }, "m.relates_to": thread, ...extra });

// holds: [{ match: /regex of "METHOD path"/, late: bool }]: the call waits until released or the caller's signal aborts. late: after its effect.
function synapse({ joined = [ROOM], state = [], events = {}, media = { abc: { bytes: IMG } }, holds = [], failing = {} } = {}) {
  const s = { calls: [], raw: [], joined, state, events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent() }, ...events }, media, holds, failing, sent: new Map(), relations: new Map(), mediaHits: 0 };
  const relationKey = (target, rel) => `${target}|${rel}`;
  const respond = (status, body) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
  const stall = (hold, signal) => new Promise((ok, fail) => {
    hold.open = ok;
    signal?.addEventListener("abort", () => fail(signal.reason), { once: true });
  });
  s.fetch = async (url, init = {}) => {
    const u = new URL(url);
    const method = init.method ?? "GET";
    const path = decodeURIComponent(u.pathname).replace(/^\/_matrix\/client\/v[13]/, "");
    const key = `${method} ${path}`;
    s.calls.push(`${key}${u.search}`);
    s.raw.push(`${u.pathname}${u.search}`);
    const hold = s.holds.find(h => h.match.test(key));
    if (hold && !hold.late) await stall(hold, init.signal);
    for (const [pattern, status] of Object.entries(s.failing)) if (new RegExp(pattern).test(key)) return respond(status, { errcode: "M_UNKNOWN" });
    const finish = async response => { if (hold?.late) await stall(hold, init.signal); return response; };
    if (path === "/account/whoami") return respond(200, { user_id: ME });
    if (path.includes("/account_data/m.direct")) return respond(404, { errcode: "M_NOT_FOUND" });
    if (path.startsWith("/directory/room/")) return respond(200, { room_id: ROOM });
    if (path === "/joined_rooms") return respond(200, { joined_rooms: s.joined });
    if (path === `/rooms/${ROOM}/state`) return respond(200, s.state);
    const event = path.match(/^\/rooms\/([^/]+)\/event\/(.+)$/);
    if (event) return s.events[event[2]] ? respond(200, s.events[event[2]]) : respond(404, { errcode: "M_NOT_FOUND" });
    const rel = path.match(/^\/rooms\/([^/]+)\/relations\/([^/]+)\/([^/]+)\/(.+)$/);
    if (rel) {
      const pages = s.relations.get(relationKey(rel[2], rel[3])) ?? [[]];
      const at = u.searchParams.get("from") === null ? 0 : Number(u.searchParams.get("from"));
      return respond(200, { chunk: pages[at] ?? [], ...(at + 1 < pages.length ? { next_batch: String(at + 1) } : {}) });
    }
    const put = path.match(/^\/rooms\/([^/]+)\/send\/([^/]+)\/(.+)$/);
    if (put && method === "PUT") {
      const body = JSON.parse(init.body);
      if (!s.sent.has(put[3])) {
        const event = { type: put[2], event_id: `$sent${s.sent.size + 1}`, sender: ME, content: body };
        s.sent.set(put[3], event);
        const relates = body["m.relates_to"];
        if (relates) {
          const k = relationKey(relates.event_id, relates.rel_type);
          s.relations.set(k, [[event, ...(s.relations.get(k)?.[0] ?? [])]]);
        }
      }
      return finish(respond(200, { event_id: s.sent.get(put[3]).event_id }));
    }
    const dl = path.match(/^\/media\/download\/[^/]+\/(.+)$/);
    if (dl) {
      s.mediaHits++;
      if (init.redirect === "error" && s.media[dl[1]]?.redirect) throw new TypeError("redirect");
      const m = s.media[dl[1]];
      if (!m) return respond(404, {});
      return new Response(m.bytes, { status: m.status ?? 200, headers: m.headers ?? {} });
    }
    return respond(404, { errcode: "M_NOT_FOUND" });
  };
  s.receipts = () => [...s.sent.values()].filter(e => e.type === docs.fileReceivedType);
  s.gets = pattern => s.calls.filter(c => pattern.test(c));
  return s;
}

function mouthOn(s, { brake = async () => true, deadline = 5000, store = true, clock, gate, put } = {}) {
  const root = personaStoreRoot();
  const real = createImageStore({ root });
  const puts = [];
  const images = store ? { put: (...a) => { puts.push(a); return put ? put(...a) : real.put(...a); } } : null;
  const logs = [];
  const mouth = createMouth({
    persona: "eureka", homeserver: "http://hs.test", serverName: HS, rooms: [ALIAS], token: TOKEN, fetch: s.fetch, state: { load: async () => null, save: async () => {} },
    brake, log: l => logs.push(l), images, fetchDeadlineMs: deadline, ...(clock ? { now: clock } : {}), ...(gate ? { gate } : {}),
  });
  const files = () => readdirSync(root, { recursive: true }).filter(f => /\.(png|jpg|gif|webp)$/.test(f));
  const ask = (over = {}) => mouth.fetchAttachment({ requestId: "r1", persona: "eureka", room: ALIAS, eventId: FILE, ...over });
  return { mouth, ask, puts, logs, files, root, fetchLines: () => logs.filter(l => l.startsWith("fetch persona=")) };
}
const noSynapse = s => assert.deepEqual(s.calls, [], "no Synapse call was made");

test("PersonaMismatch and Brake answer before any Synapse call; a null image store is StoreFailed before any call", async () => {
  const s = synapse();
  assert.equal((await mouthOn(s).ask({ persona: "nova" })).outcome, "PersonaMismatch");
  assert.equal((await mouthOn(s, { brake: async () => false }).ask()).outcome, "Brake");
  assert.equal((await mouthOn(s, { store: false }).ask()).outcome, "StoreFailed");
  noSynapse(s);
});

test("the request's ids are bounded: a room or event id that is not a path-safe Matrix id is refused with no Synapse request built from it", async () => {
  const s = synapse();
  const m = mouthOn(s);
  for (const eventId of ["file1", "$", "$a b", "$a/../../x", "$a?x=1", "$a#f", "$a%2F", "$..", "$a\\b", `$${"x".repeat(300)}`, "$a\u0085b"])
    assert.equal((await m.ask({ eventId })).outcome, "NoAttachment", JSON.stringify(eventId).slice(0, 30));
  for (const room of ["swarm", "!", "!r/../x:y", "!r?x:y", "!a b:y", "#a/b:y", "!r%2F:y", `!${"x".repeat(300)}`])
    assert.equal((await m.ask({ room })).outcome, "NotAMember", JSON.stringify(room).slice(0, 30));
  noSynapse(s);
});

test("a hostile album root or event id in the file event's own m.relates_to is refused: no request is built from it and nothing is posted", async () => {
  for (const root of ["$a/../../../etc", "$a?x=1", "$a#x", "$a%2F..", "not-an-event", "$a b", "$..", "", 7, null, "$a‮b"]) {
    const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ "m.relates_to": { rel_type: "m.thread", event_id: root } }) } } });
    const m = mouthOn(s);
    const receipt = await m.ask();
    assert.equal(receipt.outcome, "NoAttachment", `root ${JSON.stringify(root)}`);
    assert.equal(s.gets(/relations/).length, 0, "no relations request was built from the root");
    assert.equal(s.calls.filter(c => c.startsWith("PUT")).length, 0, "nothing posted");
    assert.equal(s.mediaHits, 0);
    assert.equal(m.puts.length, 0);
    assert.ok(!s.calls.some(c => /\.\.|etc|%2F/i.test(decodeURIComponent(c.split("?")[0]).replace(/\/_matrix\/client\/v\d/, "")) && /relations|send/.test(c)), "no request path carries the root");
    assert.ok(m.logs.every(l => !l.includes("/../") && !l.includes("x=1") && !l.includes("not-an-event") && !l.includes("\u202e")), "the hostile text is not logged");
  }
});

test("the log carries no hostile id: a request id that fails the shape is logged as -", async () => {
  const s = synapse();
  const m = mouthOn(s);
  await m.ask({ eventId: "$a\nfetch persona=eureka outcome=Stored" });
  assert.equal(m.fetchLines().length, 1);
  assert.match(m.fetchLines()[0], /event=- outcome=NoAttachment/);
});

test("fetch refuses a room the persona is not in, for an alias and for a bare id, before any state, event or media request", async () => {
  const s = synapse({ joined: ["!other:gamecult.org"] });
  const m = mouthOn(s);
  assert.equal((await m.ask()).outcome, "NotAMember");
  assert.equal((await m.ask({ room: "!dm:gamecult.org" })).outcome, "NotAMember");
  assert.equal(s.gets(/\/state|\/event\/|media/).length, 0);
});

test("membership is read live, not from the cached sets", async () => {
  const s = synapse();
  const m = mouthOn(s);
  assert.equal((await m.ask()).outcome, "Stored");
  s.joined = []; // she left; the mouth's cached alias set still holds the room
  s.calls.length = 0;
  assert.equal((await m.ask({ requestId: "r2" })).outcome, "NotAMember");
  assert.equal(s.gets(/\/state|\/event\/|media/).length, 0);
});

test("an unreadable /joined_rooms fails closed: FetchFailed and no state, event or media request", async () => {
  const s = synapse({ failing: { "/joined_rooms": 500 } });
  assert.equal((await mouthOn(s).ask()).outcome, "FetchFailed");
  assert.equal(s.gets(/\/state|\/event\/|media/).length, 0);
});

test("a cold mouth starts before it resolves an alias and answers with the resolved room id; a start() that throws is FetchFailed", async () => {
  const s = synapse();
  const m = mouthOn(s);
  const receipt = await m.ask();
  assert.equal(receipt.roomId, ROOM);
  assert.ok(receipt.image.path.startsWith(`${sha(ROOM)}/`), "the image path starts with sha256 of the resolved id");
  const down = synapse({ failing: { "/account/whoami": 500 } });
  assert.equal((await mouthOn(down).ask()).outcome, "FetchFailed");
});

test("a name speakableRoomId does not know is used as the room id, and the live /joined_rooms decides", async () => {
  const s = synapse();
  const m = mouthOn(s);
  const receipt = await m.ask({ room: ROOM });
  assert.equal(receipt.outcome, "Stored");
  assert.equal(receipt.roomId, ROOM);
  assert.ok(s.calls.every(c => !c.includes("null")), "no null id reaches a URL");
  assert.equal((await m.ask({ room: "!unknown:gamecult.org" })).outcome, "NotAMember");
});

test("the room type is read from the full state list: a bridge event under any state key is BridgedRoom with no event request; lookalikes are not", async () => {
  for (const type of ["m.bridge", "uk.half-shot.bridge"]) for (const content of [{ protocol: { id: "x" } }, {}]) {
    const s = synapse({ state: [{ type, state_key: "net/chan-42", content }] });
    assert.equal((await mouthOn(s).ask()).outcome, "BridgedRoom", `${type} ${JSON.stringify(content)}`);
    assert.equal(s.gets(/\/event\//).length, 0);
  }
  for (const type of ["m.bridges", "org.example.bridge"]) assert.equal((await mouthOn(synapse({ state: [{ type, state_key: "", content: {} }] })).ask()).outcome, "Stored");
  const s = synapse();
  await mouthOn(s).ask();
  assert.ok(s.calls.every(c => !/\/state\/(m|uk)\./.test(c)), "no by-type state read decides the room's type");
});

test("fetch refuses an encrypted room by state; both a bridge and encryption event give BridgedRoom", async () => {
  const enc = { type: "m.room.encryption", state_key: "", content: {} };
  const s = synapse({ state: [enc] });
  assert.equal((await mouthOn(s).ask()).outcome, "TypeRefused");
  assert.equal(s.gets(/\/event\//).length, 0);
  assert.equal((await mouthOn(synapse({ state: [enc, { type: "m.bridge", state_key: "k", content: {} }] })).ask()).outcome, "BridgedRoom");
});

test("an unreadable room state fails closed, and the call order is joined_rooms, state, event, media", async () => {
  for (const failing of [{ "/state": 500 }, { "/state": 404 }]) {
    const s = synapse({ failing });
    assert.equal((await mouthOn(s).ask()).outcome, "FetchFailed");
    assert.equal(s.gets(/\/event\//).length, 0);
  }
  const s = synapse();
  s.state = { not: "an array" };
  assert.equal((await mouthOn(s).ask()).outcome, "FetchFailed");
  const ok = synapse();
  await mouthOn(ok).ask();
  const order = ["/joined_rooms", "/state", "/event/", "media/download"].map(p => ok.calls.findIndex(c => c.includes(p)));
  assert.deepEqual(order, [...order].sort((a, b) => a - b));
  assert.ok(order.every(i => i >= 0));
});

test("an event of another room or none, or a 403 or 404 on it, is NotAMember", async () => {
  for (const room_id of ["!elsewhere:gamecult.org", undefined]) {
    const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id, sender: OTHER, content: fileContent() } } });
    assert.equal((await mouthOn(s).ask()).outcome, "NotAMember");
    assert.equal(s.mediaHits, 0);
  }
  assert.equal((await mouthOn(synapse({ failing: { "/event/": 403 } })).ask()).outcome, "NotAMember");
  assert.equal((await mouthOn(synapse()).ask({ eventId: "$nothere" })).outcome, "NotAMember");
});

test("the event guards answer in their order and none makes a media request", async () => {
  const cases = [
    [{ type: "m.room.encrypted", content: { algorithm: "m.megolm.v1.aes-sha2" } }, "TypeRefused"],
    [{ type: "m.room.message", content: { msgtype: "m.text", body: "hi" } }, "NoAttachment"],
    [{ type: "m.room.message", content: {} }, "NoAttachment"],
    [{ type: "m.room.message", content: fileContent({ "m.relates_to": { rel_type: "m.replace", event_id: "$x" } }) }, "NoAttachment"],
    [{ type: "m.reaction", content: fileContent() }, "NoAttachment"],
    [{ type: "m.room.message", content: fileContent({ file: { url: "mxc://gamecult.org/e", key: {} }, url: undefined }) }, "TypeRefused"],
    [{ type: "m.room.message", content: fileContent({ msgtype: "m.audio" }) }, "TypeRefused"],
    [{ type: "m.room.message", content: fileContent({ msgtype: "m.video" }) }, "TypeRefused"],
    [{ type: "m.room.message", content: fileContent({ msgtype: "m.sticker" }) }, "NoAttachment"],
    [{ type: "m.room.message", content: fileContent({ url: undefined }) }, "NoAttachment"],
    [{ type: "m.room.message", content: fileContent({ url: 7 }) }, "NoAttachment"],
    [{ type: "m.room.message", content: fileContent({ url: "https://evil.test/x" }) }, "NoAttachment"],
  ];
  for (const [event, outcome] of cases) {
    const s = synapse({ events: { [FILE]: { event_id: FILE, room_id: ROOM, sender: OTHER, ...event } } });
    assert.equal((await mouthOn(s).ask()).outcome, outcome, JSON.stringify(event).slice(0, 80));
    assert.equal(s.mediaHits, 0);
  }
});

test("a platform puppet sender or platform-named content key is BridgedRoom with no media request; lookalikes are not refused", async () => {
  const run = (sender, content) => { const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender, content } } }); return mouthOn(s).ask().then(r => [r.outcome, s.mediaHits]); };
  for (const sender of [`@${PLATFORM}_123:${HS}`, `@_${PLATFORM}_1:${HS}`, `@${PLATFORM}bot:${HS}`, `@${PLATFORM}:${HS}`, `@x_${PLATFORM}:${HS}`.toUpperCase().replace(":", ":")])
    assert.deepEqual(await run(sender, fileContent()), ["BridgedRoom", 0], sender);
  assert.deepEqual(await run(OTHER, fileContent({ [`org.example.${PLATFORM}`]: {} })), ["BridgedRoom", 0]);
  for (const sender of [`@${PLATFORM.replace("ord", "ourse")}:${HS}`, `@mediscord:${HS}`.replace("mediscord", "dis"), `@kiko:${HS}`])
    assert.equal((await run(sender, fileContent()))[0], "Stored", sender);
});

test("the mxc comes from the event only; the media request names the event's own url", async () => {
  const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ url: "mxc://gamecult.org/abc" }) } } });
  await mouthOn(s).ask();
  assert.deepEqual(s.raw.filter(p => p.includes("media")), ["/_matrix/client/v1/media/download/gamecult.org/abc"]);
  assert.throws(() => docs.parseFetchRequest(["r", "eureka", ALIAS, FILE, "mxc://x/y"]), /4-field/);
  assert.throws(() => docs.parseFetchRequest(["r", "eureka", ALIAS]), /4-field/);
});

test("a png under msgtype m.file with a text/html mimetype is stored as png; html bytes are TypeRefused", async () => {
  const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ msgtype: "m.file", info: { sha256: IMG_SHA, mimetype: "text/html" } }) } } });
  const r = await mouthOn(s).ask();
  assert.equal(r.outcome, "Stored");
  assert.equal(r.image.mime, "image/png");
  for (const msgtype of ["m.image", "m.file"]) {
    const html = Buffer.from("<html><script>alert(1)</script></html>");
    const t = synapse({ media: { abc: { bytes: html } }, events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ msgtype, info: {} }) } } });
    const m = mouthOn(t);
    assert.equal((await m.ask()).outcome, "TypeRefused");
    assert.equal(m.puts.length, 0);
  }
});

test("every failure word of the image path maps to the enum", async () => {
  const cases = [
    [{ abc: { bytes: IMG, status: 500 } }, "FetchFailed"],
    [{ abc: { bytes: Buffer.alloc(MAX_BYTES + 1, 1) } }, "TooLarge"],
    [{ abc: { bytes: IMG, headers: { "content-length": String(MAX_BYTES + 1) } } }, "TooLarge"],
    [{ abc: { bytes: png(10000, 10000) } }, "TooLarge"],
    [{ abc: { bytes: png(0, 0) } }, "TypeRefused"],
    [{ abc: { bytes: svg } }, "TypeRefused"],
    [{ abc: { redirect: true, bytes: IMG } }, "FetchFailed"],
  ];
  for (const [media, outcome] of cases) {
    const m = mouthOn(synapse({ media }));
    const r = await m.ask();
    assert.equal(r.outcome, outcome);
    assert.ok(docs.fetchOutcomes.includes(r.outcome));
    assert.equal(m.puts.length, 0);
  }
  const unwritable = mouthOn(synapse());
  unwritable.puts.length = 0;
  const broken = createMouth({ persona: "eureka", homeserver: "http://hs.test", serverName: HS, rooms: [ALIAS], token: TOKEN, fetch: synapse().fetch, state: { load: async () => null, save: async () => {} }, brake: async () => true, images: { put: () => { throw Object.assign(new Error("x"), { code: "ENOSPC" }); } } });
  assert.equal((await broken.fetchAttachment({ requestId: "r", persona: "eureka", room: ALIAS, eventId: FILE })).outcome, "StoreFailed");
});

test("an absent info.sha256 is StoredUnverified under the computed hash with no receipt, no relations read and no send", async () => {
  for (const info of [undefined, null, "str", 7, [IMG_SHA], {}, { mimetype: "image/png" }]) {
    const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ info }) } } });
    const m = mouthOn(s);
    const r = await m.ask();
    assert.equal(r.outcome, "StoredUnverified", JSON.stringify(info));
    assert.equal(r.receipt, null);
    assert.equal(r.image.sha256, IMG_SHA);
    assert.equal(s.gets(/relations/).length + s.calls.filter(c => c.startsWith("PUT")).length, 0);
    assert.match(m.fetchLines()[0], new RegExp(`outcome=StoredUnverified size=${IMG.length} type=image/png sha256=${IMG_SHA} receipt=skipped`));
  }
});

test("a present info.sha256 that is not the computed hash is a mismatch: nothing stored, nothing posted, the claim never logged", async () => {
  const claims = [null, 7, {}, "", IMG_SHA.slice(1), `${IMG_SHA}0`, "z".repeat(64), IMG_SHA.toUpperCase(), sha("other"), ` ${IMG_SHA}`];
  for (const claim of claims) {
    const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ info: { sha256: claim } }) } } });
    const m = mouthOn(s);
    const r = await m.ask();
    assert.equal(r.outcome, "HashMismatch", JSON.stringify(claim));
    assert.equal(m.puts.length, 0);
    assert.equal(m.files().length, 0);
    assert.equal(s.calls.filter(c => c.startsWith("PUT")).length, 0);
    assert.match(m.fetchLines()[0], new RegExp(`sha256=${IMG_SHA} `), "the computed hash, never the claim");
  }
});

test("a matching info.sha256 is Stored; in an album thread the receipt is Posted and is the contract's, nothing more", async () => {
  const s = synapse();
  const m = mouthOn(s);
  const r = await m.ask();
  assert.equal(r.outcome, "Stored");
  assert.equal(r.receipt, "Posted");
  assert.equal(m.files().length, 1);
  const [event] = s.receipts();
  assert.equal(event.sender, ME);
  assert.deepEqual(event.content, { "m.relates_to": { rel_type: "m.thread", event_id: ROOT }, [docs.fileReceivedType]: { event_id: FILE, sha256: IMG_SHA, size: IMG.length } });
  const text = JSON.stringify(event.content);
  assert.ok(!/mxc|\/|https?:|tok-secret|cat\.png|"mind"/.test(text.replace(/"m\.relates_to"|"rel_type"|"m\.thread"|"org\.gamecult\.file_received"/g, "")), "no path, scheme, mxc, token, filename or mind key");
  assert.ok(m.logs.every(l => !l.includes(TOKEN) && !l.includes("mxc://")));
  assert.match(m.fetchLines()[0], /receipt=posted/);
  const txn = [...s.sent.keys()][0];
  assert.equal(txn, `fr-${sha("eureka|" + FILE)}`);
});

const plainFile = relates => ({ [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: relates === undefined ? (({ "m.relates_to": _, ...rest }) => rest)(fileContent()) : fileContent({ "m.relates_to": relates }) } });

test("a file in no thread gets one plain receipt referencing the file; its reference and content.event_id are one value", async () => {
  for (const relates of [undefined, { "m.in_reply_to": { event_id: "$q" } }, { rel_type: "m.reference", event_id: "$z" }]) {
    const s = synapse({ events: plainFile(relates) });
    const m = mouthOn(s);
    const r = await m.ask({ room: ROOM });
    assert.equal(r.receipt, "Posted");
    assert.equal(s.receipts().length, 1);
    const [event] = s.receipts();
    assert.deepEqual(Object.keys(event.content).sort(), ["m.relates_to", docs.fileReceivedType]);
    assert.deepEqual(event.content["m.relates_to"], { rel_type: "m.reference", event_id: FILE });
    assert.deepEqual(Object.keys(event.content[docs.fileReceivedType]).sort(), ["event_id", "sha256", "size"]);
    assert.equal(event.content["m.relates_to"].event_id, event.content[docs.fileReceivedType].event_id);
    const again = await mouthOn(s).ask(); // a restarted mouth reads Matrix, keeps nothing
    assert.equal(again.receipt, "Existing");
    assert.equal(s.receipts().length, 1);
  }
  const root = synapse({ events: plainFile({ rel_type: "m.thread", event_id: FILE }) }); // the file is itself a thread root's reply to itself: still a valid thread shape
  assert.equal((await mouthOn(root).ask()).receipt, "Posted");
});

test("idempotence reads the v1 relations path of the thread or of the file, follows next_batch for three pages and never a fourth", async () => {
  const s = synapse();
  s.relations.set(`${ROOT}|m.thread`, [[], [], [], [{ type: docs.fileReceivedType, sender: ME, content: { [docs.fileReceivedType]: { event_id: FILE } } }]]);
  const m = mouthOn(s);
  const r = await m.ask();
  assert.equal(s.gets(/relations/).length, 3, "a fourth page is never asked");
  assert.ok(s.raw.includes(`/_matrix/client/v1/rooms/${encodeURIComponent(ROOM)}/relations/${encodeURIComponent(ROOT)}/m.thread/${docs.fileReceivedType}?dir=b&limit=100`));
  assert.ok(s.raw.includes(`/_matrix/client/v1/rooms/${encodeURIComponent(ROOM)}/relations/${encodeURIComponent(ROOT)}/m.thread/${docs.fileReceivedType}?dir=b&limit=100&from=1`), "page two via next_batch");
  assert.ok(s.calls.every(c => !c.includes("event_type=")));
  assert.equal(r.outcome, "Stored");
  assert.equal(r.receipt, null, "not exhausted proves nothing: no post");
  assert.equal(s.receipts().length, 0);
  assert.match(m.fetchLines()[0], /receipt=failed/);
  const t = synapse();
  t.relations.set(`${ROOT}|m.thread`, [[], [{ type: docs.fileReceivedType, sender: ME, content: { [docs.fileReceivedType]: { event_id: FILE } } }]]);
  assert.equal((await mouthOn(t).ask()).receipt, "Existing");
  assert.equal(t.receipts().length, 0);
  const f = synapse({ events: plainFile(undefined) });
  await mouthOn(f).ask({ room: ROOM });
  assert.ok(f.raw.some(p => p.startsWith(`/_matrix/client/v1/rooms/${encodeURIComponent(ROOM)}/relations/${encodeURIComponent(FILE)}/m.reference/${docs.fileReceivedType}?dir=b&limit=100`)));
});

test("a receipt by another sender, for another file, or redacted does not suppress hers, in both listings", async () => {
  const mine = (over = {}) => ({ type: docs.fileReceivedType, sender: ME, content: { [docs.fileReceivedType]: { event_id: FILE } }, ...over });
  const decoys = [mine({ sender: OTHER }), mine({ content: { [docs.fileReceivedType]: { event_id: "$other" } } }), mine({ unsigned: { redacted_because: {} }, content: {} })];
  for (const [events, key, room] of [[undefined, `${ROOT}|m.thread`, ALIAS], [plainFile(undefined), `${FILE}|m.reference`, ROOM]]) {
    const s = synapse(events ? { events } : {});
    s.relations.set(key, [decoys]);
    const r = await mouthOn(s).ask({ room });
    assert.equal(r.receipt, "Posted");
    assert.equal(s.receipts().length, 1);
  }
});

test("a failed relations read or a failed post does not fail the fetch: Stored, receipt nil, file kept, log receipt=failed", async () => {
  for (const [events, failing, room] of [[undefined, { "/relations/": 500 }, ALIAS], [undefined, { "PUT .*/send/": 500 }, ALIAS], [plainFile(undefined), { "/relations/": 500 }, ROOM], [plainFile(undefined), { "PUT .*/send/": 500 }, ROOM]]) {
    const s = synapse({ ...(events ? { events } : {}), failing });
    const m = mouthOn(s);
    const r = await m.ask({ room });
    assert.equal(r.outcome, "Stored");
    assert.equal(r.receipt, null);
    assert.equal(m.files().length, 1);
    assert.match(m.fetchLines()[0], /receipt=failed/);
    assert.equal(s.receipts().length, 0);
  }
});

test("a brake placed after the file is stored posts no receipt: Stored, receipt nil, log receipt=skipped", async () => {
  const s = synapse();
  let reads = 0;
  const m = mouthOn(s, { brake: async () => { reads++; return !s.mediaHits || reads <= 2 ? true : s.calls.filter(c => c.includes("relations")).length === 0 ? true : false; } });
  const r = await m.ask();
  assert.equal(r.outcome, "Stored");
  assert.equal(r.receipt, null);
  assert.equal(m.files().length, 1);
  assert.equal(s.calls.filter(c => c.startsWith("PUT")).length, 0);
  assert.match(m.fetchLines()[0], /receipt=skipped/);
});

test("the fetch is rate limited per Persona: the 31st in an hour is RateLimited with no request; every request counts", async () => {
  const s = synapse({ joined: [] });
  let t = 0;
  const m = mouthOn(s, { clock: () => t });
  for (let i = 0; i < 30; i++) assert.equal((await m.ask({ requestId: `r${i}` })).outcome, "NotAMember");
  s.calls.length = 0;
  assert.equal((await m.ask({ requestId: "r31" })).outcome, "RateLimited");
  noSynapse(s);
  t += 3_600_001;
  assert.equal((await m.ask({ requestId: "r32" })).outcome, "NotAMember");
});

test("the fetch runs inside the room gate with a send: a fetch queued behind a slow holder whose deadline fires does no Synapse GET and stores nothing", async () => {
  const s = synapse();
  const gate = createRoomGate();
  let release;
  const holder = gate.run(ROOM, () => new Promise(ok => { release = ok; }));
  const m = mouthOn(s, { gate, deadline: 80 });
  const asked = await m.ask({ room: ROOM }); // answers at the deadline, while the holder still holds the gate
  assert.equal(asked.outcome, "FetchFailed");
  s.calls.length = 0;
  release();
  await holder;
  await new Promise(r => setTimeout(r, 30));
  assert.deepEqual(s.calls, [], "the queued fetch ran nothing past its deadline");
  assert.equal(m.puts.length, 0);
  assert.equal(m.files().length, 0);
  assert.equal(m.fetchLines().length, 1, "one line per fetch");
  assert.match(m.fetchLines()[0], /outcome=FetchFailed .*receipt=skipped/);
});

test("a brake placed while a fetch waits in the gate stops it before any request", async () => {
  const s = synapse();
  const gate = createRoomGate();
  let release;
  const holder = gate.run(ROOM, () => new Promise(ok => { release = ok; }));
  let free = true;
  const m = mouthOn(s, { gate, brake: async () => free });
  await m.mouth.start();
  s.calls.length = 0;
  const asked = m.ask({ room: ROOM });
  await new Promise(r => setTimeout(r, 20));
  free = false;
  release();
  await holder;
  assert.equal((await asked).outcome, "Brake");
  assert.deepEqual(s.calls, []);
  assert.equal(m.puts.length, 0);
});

test("one deadline covers the whole fetch: a stall in each read before the put is FetchFailed receipt=skipped, nothing is put or sent once it is released", async () => {
  for (const match of [/GET \/joined_rooms/, /GET \/rooms\/[^/]+\/state$/, /GET \/rooms\/[^/]+\/event\//, /media\/download/]) {
    const hold = { match };
    const s = synapse();
    const m = mouthOn(s, { deadline: 120 });
    await m.mouth.start();
    s.holds.push(hold);
    const r = await m.ask({ room: ROOM });
    assert.equal(r.outcome, "FetchFailed", String(match));
    hold.open?.();
    await new Promise(r => setTimeout(r, 20));
    assert.equal(m.puts.length, 0);
    assert.equal(s.calls.filter(c => c.startsWith("PUT")).length, 0);
    assert.equal(m.fetchLines().length, 1);
    assert.match(m.fetchLines()[0], /outcome=FetchFailed .*receipt=skipped/);
  }
});

test("a deadline after the put leaves a stored file and no receipt, logged receipt=failed; the retry posts exactly one receipt", async () => {
  for (const match of [/GET \/rooms\/[^/]+\/relations\//, /PUT \/rooms\/[^/]+\/send\//]) {
    const hold = { match, late: /PUT/.test(match.source) };
    const s = synapse({ holds: [hold] });
    const m = mouthOn(s, { deadline: 150 });
    const r = await m.ask({ room: ROOM });
    assert.equal(r.outcome, "FetchFailed");
    assert.equal(r.image, null);
    assert.equal(m.puts.length, 1);
    assert.equal(m.files().length, 1);
    assert.match(m.fetchLines()[0], /outcome=FetchFailed .*receipt=failed/);
    s.holds.length = 0;
    const retry = await m.ask({ room: ROOM, requestId: "r2" });
    assert.equal(retry.outcome, "Stored");
    assert.ok(retry.receipt === "Posted" || retry.receipt === "Existing");
    assert.equal(s.receipts().length, 1, "the same txn id: still one receipt");
  }
});

test("the mouth log line carries the computed hash, the planted token and mxc are in no line, and a Stored fetch has exactly one line", async () => {
  const s = synapse();
  const m = mouthOn(s);
  await m.ask();
  assert.equal(m.fetchLines().length, 1);
  assert.equal(m.fetchLines()[0], `fetch persona=eureka room=${ROOM} event=${FILE} outcome=Stored size=${IMG.length} type=image/png sha256=${IMG_SHA} receipt=posted`);
  assert.ok(m.logs.every(l => !l.includes(TOKEN) && !l.includes("mxc")));
  const j = mouthOn(synapse({ media: { abc: { bytes: jpeg(8, 8) } }, events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ info: { sha256: IMG_SHA } }) } } }));
  assert.equal((await j.ask()).outcome, "HashMismatch");
  assert.match(j.fetchLines()[0], /type=image\/jpeg/);
});

test("the receipt document follows its one table", () => {
  const image = { path: `${sha(ROOM)}/${IMG_SHA}.png`, mime: "image/png", size: 100, sha256: IMG_SHA };
  for (const receipt of [null, "Posted", "Existing"]) assert.doesNotThrow(() => docs.fetchReceiptTuple({ requestId: "r", outcome: "Stored", roomId: ROOM, image, receipt }));
  assert.doesNotThrow(() => docs.fetchReceiptTuple({ requestId: "r", outcome: "StoredUnverified", roomId: ROOM, image }));
  for (const receipt of ["Posted", "Existing", "$ev", "posted"]) assert.throws(() => docs.fetchReceiptTuple({ requestId: "r", outcome: "StoredUnverified", roomId: ROOM, image, receipt }));
  assert.throws(() => docs.fetchReceiptTuple({ requestId: "r", outcome: "Stored", roomId: ROOM, image, receipt: "$ev" }));
  assert.throws(() => docs.fetchReceiptTuple({ requestId: "r", outcome: "Stored" }), /exactly for Stored/);
  for (const outcome of docs.fetchOutcomes.filter(w => !w.startsWith("Stored"))) {
    assert.doesNotThrow(() => docs.fetchReceiptTuple({ requestId: "r", outcome }));
    assert.throws(() => docs.fetchReceiptTuple({ requestId: "r", outcome, roomId: ROOM }));
    assert.throws(() => docs.fetchReceiptTuple({ requestId: "r", outcome, image }));
    assert.throws(() => docs.fetchReceiptTuple({ requestId: "r", outcome, receipt: "Posted" }));
  }
  assert.equal(docs.fetchOutcomes.length, 14);
  assert.throws(() => docs.fetchReceiptTuple({ requestId: "r", outcome: "Nope" }), /unknown fetch outcome/);
  const tuple = docs.fetchReceiptTuple({ requestId: "r", outcome: "Stored", roomId: ROOM, image, receipt: "Posted" });
  assert.equal(tuple.length, 5);
  assert.deepEqual(docs.parseFetchReceipt(tuple), { requestId: "r", outcome: "Stored", roomId: ROOM, image, receipt: "Posted" });
  assert.throws(() => docs.parseFetchReceipt([...tuple, "extra"]), /5-field/);
  assert.throws(() => docs.parseFetchReceipt(tuple.slice(0, 4)), /5-field/);
  assert.throws(() => docs.parseFetchReceipt(["r", "StoredUnverified", ROOM, tuple[3], "Posted"]));
  assert.throws(() => docs.parseFetchRequest(["r", "eureka", "x".repeat(300), FILE]), /over 255/);
});

test("the flag is exactly one", () => {
  assert.equal(fetchFromEnv({}), false);
  for (const v of ["0", "true", "yes", "", " 1", "1 ", "01"]) assert.equal(fetchFromEnv({ CHAT_FETCH: v }), false, JSON.stringify(v));
  assert.equal(fetchFromEnv({ CHAT_FETCH: "1" }), true);
});

test("images.put has exactly two call sites in the mouth and the fetch's comes after the hash decision; no second media fetcher", () => {
  const source = readFileSync(resolve(import.meta.dirname, "..", "tools", "persona-matrix-mouth.mjs"), "utf8");
  assert.equal(source.match(/images\.put\(/g).length, 2);
  const fetchBody = source.slice(source.indexOf("async function fetchAttachment"), source.indexOf("async function postFileReceipt"));
  assert.ok(fetchBody.indexOf("HashMismatch") < fetchBody.indexOf("images.put("), "the hash decision precedes the put");
  assert.ok(fetchBody.indexOf("throwIfAborted", fetchBody.indexOf("HashMismatch")) < fetchBody.indexOf("images.put("), "the deadline is checked right before the put");
  assert.ok(!/media\/download/.test(source), "no media GET outside fetchImage");
  assert.equal([...source.matchAll(/\/state\/(m\.|uk\.)|event_type=/g)].length, 0);
});

test("the receipt names the event the GET resolved: reference and content.event_id both carry the returned id, in a thread file and a plain one", async () => {
  const answered = { type: "m.room.message", event_id: "$resolved", room_id: ROOM, sender: OTHER };
  const plain = synapse({ events: { [FILE]: { ...answered, content: plainFile(undefined)[FILE].content } } });
  assert.equal((await mouthOn(plain).ask({ room: ROOM })).receipt, "Posted");
  const [event] = plain.receipts();
  assert.equal(event.content["m.relates_to"].event_id, "$resolved");
  assert.equal(event.content[docs.fileReceivedType].event_id, "$resolved");
  const threaded = synapse({ events: { [FILE]: { ...answered, content: fileContent() } } });
  await mouthOn(threaded).ask({ room: ROOM });
  assert.equal(threaded.receipts()[0].content[docs.fileReceivedType].event_id, "$resolved");
  const bad = synapse({ events: { [FILE]: { ...answered, event_id: "$a/../b", content: fileContent() } } });
  assert.equal((await mouthOn(bad).ask({ room: ROOM })).outcome, "NoAttachment");
  assert.equal(bad.receipts().length, 0);
});

test("a failure leaves the log as fixed words: a transport error holding a URL, an mxc and a sender-chosen 200-byte album root reaches no line", async () => {
  const root = `$${"r".repeat(199)}`;
  const taint = `http://hs.test/_matrix/client/v3/joined_rooms mxc://gamecult.org/abc ${root} Bearer ${TOKEN}`;
  for (const [pattern, expectFetch] of [[/GET \/rooms\/[^/]+\/relations\//, "Stored"], [/GET \/rooms\/[^/]+\/state$/, "FetchFailed"], [/PUT \/rooms\/[^/]+\/send\//, "Stored"]]) {
    const s = synapse({ events: { [FILE]: { type: "m.room.message", event_id: FILE, room_id: ROOM, sender: OTHER, content: fileContent({ "m.relates_to": { rel_type: "m.thread", event_id: root } }) } } });
    const real = s.fetch;
    s.fetch = async (url, init = {}) => {
      if (pattern.test(`${init.method ?? "GET"} ${decodeURIComponent(new URL(url).pathname).replace(/^\/_matrix\/client\/v[13]/, "")}`)) throw new Error(`fetch failed ${taint}`);
      return real(url, init);
    };
    const m = mouthOn(s);
    assert.equal((await m.ask({ room: ROOM })).outcome, expectFetch, String(pattern));
    assert.equal(m.fetchLines().length, 1, "still one fetch line");
    for (const line of m.logs) {
      for (const bit of ["http", "mxc", root, "r".repeat(40), TOKEN, "unreachable"]) assert.ok(!line.includes(bit), `${bit} in: ${line.slice(0, 120)}`);
    }
    assert.match(m.fetchLines()[0], / cause=Error\/-$/);
  }
  const status = synapse();
  status.failing = { "/relations/": 500 };
  const m = mouthOn(status);
  await m.ask();
  assert.match(m.fetchLines()[0], /receipt=failed cause=Error\/500$/);
});

test("the deadline aborts the in-flight media GET: the room gate is free again at once, not after the media timeout", async () => {
  const s = synapse({ holds: [{ match: /media\/download/ }] });
  const gate = createRoomGate();
  const m = mouthOn(s, { gate, deadline: 100 });
  assert.equal((await m.ask({ room: ROOM })).outcome, "FetchFailed");
  const freed = await Promise.race([gate.run(ROOM, async () => "free"), new Promise(r => setTimeout(() => r("held"), 300))]);
  assert.equal(freed, "free");
  assert.equal(m.puts.length, 0);
});

test("a redacted receipt does not count as hers even when its content is intact", async () => {
  const s = synapse();
  s.relations.set(`${ROOT}|m.thread`, [[{ type: docs.fileReceivedType, sender: ME, unsigned: { redacted_because: {} }, content: { [docs.fileReceivedType]: { event_id: FILE } } }]]);
  assert.equal((await mouthOn(s).ask({ room: ROOM })).receipt, "Posted");
  assert.equal(s.receipts().length, 1);
});

test("a deadline that passes while the brake is re-read before the receipt PUT sends nothing", async () => {
  const s = synapse();
  let reads = 0, free;
  const m = mouthOn(s, { deadline: 150, brake: async () => (++reads === 3 ? new Promise(ok => { free = () => ok(true); }) : true) });
  assert.equal((await m.ask({ room: ROOM })).outcome, "FetchFailed");
  free();
  await new Promise(r => setTimeout(r, 30));
  assert.equal(s.calls.filter(c => c.startsWith("PUT")).length, 0);
  assert.equal(m.puts.length, 1);
});

test("a store that throws is one fetch line with a fixed cause: its code, message and name text reach no line", async () => {
  const taint = `/srv/images/secret-path http://hs.test/x mxc://gamecult.org/abc ${TOKEN}`;
  const s = synapse();
  const m = mouthOn(s, { put: () => { throw Object.assign(new Error(taint), { code: taint, name: "not a class name" }); } });
  assert.equal((await m.ask()).outcome, "StoreFailed");
  assert.equal(m.fetchLines().length, 1);
  assert.match(m.fetchLines()[0], /outcome=StoreFailed .* cause=Error\/-$/);
  assert.equal(m.logs.length, m.fetchLines().length, "no other line");
  for (const line of m.logs) for (const bit of ["secret-path", "http", "mxc", TOKEN, "EACCES"]) assert.ok(!line.includes(bit), `${bit} in: ${line}`);
  assert.equal(s.calls.filter(c => c.startsWith("PUT")).length, 0, "no receipt for an unstored file");
});

test("an event id with characters encodeURIComponent changes goes out as one encoded path segment in every request built from it", async () => {
  const hostile = ["$a+b=c", "$a@b:c", "$a&b", "$a b".replace(" ", "!"), "$a'(b)*", "$a;b,c", "$a$b"];
  for (const [id, threaded] of hostile.flatMap(h => [[h, true], [h, false]])) {
    assert.ok(docs.isFetchId("$", id), id);
    assert.notEqual(encodeURIComponent(id), id, `${id} is changed by encoding`);
    const s = synapse({ events: { [id]: { type: "m.room.message", event_id: id, room_id: ROOM, sender: OTHER, content: fileContent({ "m.relates_to": threaded ? { rel_type: "m.thread", event_id: id } : undefined }) } } });
    const m = mouthOn(s);
    assert.equal((await m.ask({ eventId: id, room: ROOM })).outcome, "Stored", id);
    const enc = encodeURIComponent(id);
    const eventGet = s.raw.filter(p => p.includes("/event/"));
    const relGet = s.raw.filter(p => p.includes("/relations/"));
    assert.ok(eventGet.length === 1 && eventGet[0] === `/_matrix/client/v3/rooms/${encodeURIComponent(ROOM)}/event/${enc}`, `event GET: ${eventGet}`);
    assert.ok(relGet.length >= 1 && relGet.every(p => p.startsWith(`/_matrix/client/v1/rooms/${encodeURIComponent(ROOM)}/relations/${enc}/${threaded ? "m.thread" : "m.reference"}/`)), `relations GET: ${relGet}`);
    assert.ok(s.raw.every(p => !p.split("?")[0].includes(id) || id === enc), "no raw id in a request path");
  }
});

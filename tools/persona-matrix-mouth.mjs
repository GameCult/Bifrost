// The Matrix ear and mouth of every Persona. One process: one account (token) and
// one /sync loop per Persona, one CultNet endpoint for all of them.
//
// Ear: /sync the configured rooms (and the account's direct rooms); every message
// from another sender becomes a delivery kept in memory, carrying its sender id,
// its m.mentions user ids and whether the room is a DM. The /sync position
// (next_batch) is persisted only when an ack covers it, so a restart before the
// ack re-delivers instead of losing. A v3 delivery also carries what Matrix proves about why it is for her (basis: direct, tag,
// reply, room, and with CHAT_FOLLOW=1 follow; form: message, notice, edit); names and roles are the heartbeat's to match, not the mouth's (docs/bridge.md,
// "Addressing facts"). Reading wakes nothing: the heartbeat polls
// with a non-consuming chat_pending_request and decides who runs. An ack posts nothing to Matrix. Her public
// m.read receipt follows a chat_read document from Huginn, sent when a delivery has gone into the model's
// input: the mouth posts m.read at that event id only, for an event it delivered to her, never behind one
// already posted in that room (fire and forget on the endpoint: a failure is logged once, not retried).
// Her presence: online while a turn is going on (a read report, a send or an ack within presenceGraceMs),
// unavailable once the grace has passed, offline while her brake is held. Every /sync carries the state
// and a change is also PUT at once, so a transition does not wait out the long poll. A brake placed mid
// long-poll shows at the next round, within syncTimeoutMs plus one PUT; a stopped mouth reads offline after
// the homeserver's sync timeout.
// Mouth: a send request becomes one PUT with txnId = request_id (a retry is idempotent), and a room
// is joined, only while that Persona's brake is released. A v2 send carries the basis of her turn (the
// newest room event she saw): inside the room gate, one step per room across every Persona, the mouth
// reads the room's head from Matrix and answers RoomMoved, naming the messages that moved it, instead
// of the PUT. A v1 send, and a v2 send with no basis, are not checked; both still pass through the gate.
//
// Follow (CHAT_FOLLOW=1, default off; docs/bridge.md "Follow"): her own send in a public room (not a DM, not encrypted, not bridged) anchors the room
// for 10 minutes; those who spoke there in the 10 minutes before it, and whoever it tagged or replied to, get basis "follow" on their later messages
// inside the window, at most 3 an hour per peer and room. Memory only. Off, no basis "follow" is ever emitted: Huginn's parser refuses it until its
// Basis::Follow lands.
//
// Media: an m.image/m.file/m.audio/m.video event is delivered as one typed line (see bodyText), not its bare filename.
// An m.image event in a delivered message is also fetched, as this Persona and only when an image store is configured
// (persona-chat-images.mjs): the delivery then carries a reference to the stored file, or the line says why not.
//
// Context: Matrix is the record. A delivery's `context` is read from the homeserver when the delivery
// is built (GET /messages, or the thread's relations for a threaded message), as this Persona's
// account, and holds m.room.message events from everyone, her own included, oldest first, ending
// just before the delivered event. Nothing about a room's past is kept in this process, so a restart
// loses none of it. A failed fetch gives empty context and a log line; the delivery still goes out.
//
// Each Matrix access token is read here, from its own systemd credential, and
// nowhere else. It is never put in a document, an environment passed to a child,
// or a log, and it is only ever presented in its own Persona's Authorization header.
//
// Everything outside this process is a port (fetch, state, brake, clock), so the
// core runs against a fake homeserver with no CultLib and no daemon.
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { createSocket } from "node:dgram";
import { resolve, join } from "node:path";
import { pathToFileURL } from "node:url";
import * as docs from "./persona-chat-documents.mjs";
import { createImageStore, fetchImage } from "./persona-chat-images.mjs";

const API = "/_matrix/client/v3";
const BRAKE_TYPE = "eureka.control.brake.v1";
const CONTEXT = 20; // /sync timeline limit per round
const CONTEXT_LINES = 20; // the most context lines the delivery tuple carries (persona-chat-documents maxContext)
const CONTEXT_BYTES = 16 * 1024; // text and sender bytes over all lines of one delivery: under CONTEXT_LINES x limits.contextBytes
const FETCH_LIMIT = 100; // events asked of the homeserver per context fetch
const V1 = "/_matrix/client/v1"; // /relations lives only under v1
const MAX_PENDING = 500;
const SLUG = /^[A-Za-z0-9_-]{1,64}$/;
const SEND_TIMEOUT_MS = 10000; // the head read and the PUT of one gated send
const PRESENCE_GRACE_MS = 5 * 60 * 1000; // a turn's last sign of life keeps her online this long
const DELIVERED_INDEX = 4000; // delivered event ids remembered so a read report can be checked against them
const DM_SCAN = 200; // joined rooms read when looking for an existing DM with a person
const NOT_E2EE = "This room is not end-to-end encrypted: the homeserver can read what is said here.";
const dayOf = ms => new Date(ms).toISOString().slice(0, 10);
const TAIL_TTL_MS = 15 * 1000;
const FETCH_DEADLINE_MS = 15000; // one fetch, whole: it ends before Huginn's 20 s wait
const FETCH_PER_HOUR = 30; // fetches one Persona may ask in an hour
const RELATION_PAGES = 3; // pages of a receipt target's relations read before the answer is called unproven
const PLATFORM = ["disc", "ord"].join(""); // the bridged platform's name, as Huginn's room_scope spells it: never written out whole
const FOLLOW_WINDOW_MS = 10 * 60 * 1000; // W: an engagement lasts this long from the Mind's own post; a follow never extends it
const FOLLOW_PER_HOUR = 3; // K: follow bases given per (Mind, peer, room) in an hour; past it the message keeps the basis it had
const FOLLOW_ROOM_TTL_MS = 5 * 60 * 1000; // how long a room's public verdict (not encrypted, not bridged) is kept
const FOLLOW_SLACK_MS = 60 * 1000; // clock disagreement tolerated between this process and the homeserver
const FOLLOW_HOUR_MS = 60 * 60 * 1000;
const FOLLOW_RING = 200; // recent senders kept per room
const HEAD_PAGE = 50; // the room head a gated send reads; also the most ids a RoomMoved receipt names

// The kind of an event for a delivery: an edit (m.replace) and an automated notice (m.notice) are not messages anyone is addressed by.
const formOf = content => content?.["m.relates_to"]?.rel_type === "m.replace" ? "edit" : content?.msgtype === "m.notice" ? "notice" : "message";

// A message line: a text event with a sender, not an edit.
const isLine = e => e?.type === "m.room.message" && typeof e.content?.body === "string" && typeof e.sender === "string" && e.sender !== "" && e.content?.["m.relates_to"]?.rel_type !== "m.replace";
// Whether `event` moves the room under a Persona whose account is `selfId`: someone else's line that is
// not an automated notice. Threaded replies count; the ear delivers them to her.
export const movesRoom = (event, selfId) => isLine(event) && event.sender !== selfId && event.content?.msgtype !== "m.notice";

// A media event (m.image, m.file, m.audio, m.video) reaches the Persona as one typed line, then its caption if it has one:
//   [image filename="cat.png" mime=image/png size=2048 url=mxc://gamecult.test/abc]
// A Persona turn is a tool-less text call (it has no way to open a file or see an image), so the mouth names the
// attachment and fetches nothing. Every field is checked before it is printed; a field that fails is left out.
// An encrypted attachment names its `file.url` and carries `encrypted`; its key is never read. `refused` is the
// fixed-word reason an image was not stored (persona-chat-images `refusals`); it is never sender text.
const MEDIA_KIND = { "m.image": "image", "m.file": "file", "m.audio": "audio", "m.video": "video" };
const MXC = /^mxc:\/\/[A-Za-z0-9.:_-]+\/[A-Za-z0-9_-]+$/;
const MIME = /^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}\/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}$/;
const FILENAME_BYTES = 255;
export function bodyText(content, maxBytes, refused = null) {
  const kind = MEDIA_KIND[content?.msgtype];
  if (!kind) return docs.clip(content.body, maxBytes);
  const encrypted = typeof content.file?.url === "string" && typeof content.url !== "string";
  const url = typeof content.url === "string" ? content.url : content.file?.url;
  const named = typeof content.filename === "string" && content.filename !== "";
  const filename = named ? content.filename : content.body;
  const parts = [kind, `filename=${JSON.stringify(docs.clip(filename, FILENAME_BYTES))}`];
  if (typeof content.info?.mimetype === "string" && MIME.test(content.info.mimetype)) parts.push(`mime=${content.info.mimetype}`);
  if (Number.isSafeInteger(content.info?.size) && content.info.size >= 0) parts.push(`size=${content.info.size}`);
  if (typeof url === "string" && MXC.test(url)) parts.push(`url=${url}`);
  if (encrypted) parts.push("encrypted");
  if (refused !== null) parts.push(`refused=${refused}`);
  const line = docs.clip(`[${parts.join(" ")}]`, maxBytes);
  const caption = named && content.body !== filename ? content.body : "";
  return caption === "" ? line : docs.clip(`${line}\n${caption}`, maxBytes);
}

// The one gate every mouth shares: run(roomId, fn) runs fn after every earlier fn on that room, and a
// failure of one does not stop the next. It holds ordering only, nothing across a restart.
export function createRoomGate() {
  const tails = new Map();
  return {
    run(roomId, fn) {
      const result = (tails.get(roomId) ?? Promise.resolve()).then(fn);
      const tail = result.catch(() => {});
      tails.set(roomId, tail);
      tail.then(() => { if (tails.get(roomId) === tail) tails.delete(roomId); });
      return result;
    },
  };
}

export function createMouth({
  persona, homeserver, serverName, rooms, token, fetch: httpFetch = globalThis.fetch, state, brake,
  now = Date.now, log = () => {}, syncTimeoutMs = 30000, maxPending = MAX_PENDING, gate = createRoomGate(), sendTimeoutMs = SEND_TIMEOUT_MS,
  presenceGraceMs = PRESENCE_GRACE_MS, fetchDeadlineMs = FETCH_DEADLINE_MS,
  follow = false, // the follow basis (CHAT_FOLLOW): an untagged message from someone she tagged or replied to, within FOLLOW_WINDOW_MS of her own post, is for her
  replyLink = false, // resolve each reply's quoted event into delivery.replyTo (the v4 answer; one more event read per reply to a non-Mind)
  images = null, // this Persona's image store (createImageStore), or null: images stay the typed text line
  isServed = async () => false, // (async) whether a Matrix user id is an account this process speaks for: a Mind is not a person to open a DM with
}) {
  if (!persona || !homeserver || !token || !rooms?.length) throw new Error("persona, homeserver, rooms and token are required");
  const say = message => log(redact(String(message), token));
  const roomIdByAlias = new Map();
  const aliasByRoomId = new Map();
  const joinedAliases = new Set();
  const freshRooms = new Set(); // joined by this process since the last sync: their history is context, not mail
  const directRooms = new Set(); // room ids the account marks as DMs (m.direct)
  const displayNames = new Map();
  let selfId = null;
  let cursor = null; // the /sync position we are reading from
  let seqCounter = 0;
  let pending = []; // deliveries not yet acked
  let batches = []; // { token, maxSeq }: positions awaiting an ack
  let committed = null; // the position that is persisted
  let started = false;
  let lastPresence = null; // 'online' | 'unavailable' | 'offline' | null: command-only, suppresses repeat presence PUTs
  let activeUntil = 0; // her last sign of life in a turn, plus the grace
  const tailReads = new Map(); // room id -> { at, lines }: a room's tail read, kept TAIL_TTL_MS or until a sync brings the room news
  const delivered = new Map(); // event id -> { roomId, roomAlias, seq }: what the mouth handed her, newest last, bounded
  const readThrough = new Map(); // room id -> { seq, eventId } of the last receipt that landed
  const readTails = new Map(); // room id -> the read report in flight: reports for one room run one at a time
  const directContent = {}; // the account's m.direct content: user id -> room ids
  const invites = new Map(); // room id -> { inviter, direct }: invitations not yet answered
  const processed = new Set(); // event ids read since the position last advanced
  const noticed = new Set(); // rooms this mouth created where the not-E2EE first line has gone out
  const personTails = new Map(); // person's user id -> the open in flight: opens for one person run one at a time
  const fetchTimes = []; // when this Persona's recent fetches began: display-only rate count, reset by a restart
  const openedOn = new Map(); // person's user id -> the UTC day this process last created a room for them: one creation per person per day

  async function matrix(method, path, body, prefix = API, signal) {
    let response;
    try {
      response = await httpFetch(`${homeserver}${prefix}${path}`, {
        method,
        headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal,
      });
    } catch (error) {
      throw new Error(`homeserver unreachable: ${redact(error?.message ?? String(error), token)}`);
    }
    if (!response.ok) throw Object.assign(new Error(`homeserver answered ${response.status} for ${method} ${path.split("?")[0]}`), { status: response.status });
    return response.json();
  }

  const released = async () => { try { return (await brake()) === true; } catch { return false; } };
  const noteDirect = content => {
    for (const [user, ids] of Object.entries(content ?? {})) {
      if (!Array.isArray(ids)) continue;
      directContent[user] = ids.filter(id => typeof id === "string");
      directContent[user].forEach(id => directRooms.add(id));
    }
  };

  function noteInvites(invited) {
    for (const [roomId, room] of Object.entries(invited ?? {})) {
      const event = (room.invite_state?.events ?? []).find(e => e.type === "m.room.member" && e.state_key === selfId && typeof e.sender === "string");
      if (event) invites.set(roomId, { inviter: event.sender, direct: event.content?.is_direct === true });
    }
  }

  // A DM invitation from an account on our own homeserver is accepted, and only while the brake is
  // released; one received while braked waits for the release. An invitation from any other server is
  // declined. A same-server invitation to a room that is not a DM is not ours to accept and is dropped.
  async function answerInvites() {
    for (const [roomId, invite] of [...invites]) {
      try {
        const room = encodeURIComponent(roomId);
        if (!serverName || invite.inviter.slice(invite.inviter.indexOf(":") + 1) !== serverName) {
          await matrix("POST", `/rooms/${room}/leave`, {});
          invites.delete(roomId);
        } else if (!invite.direct) {
          invites.delete(roomId);
        } else {
          if (!(await released())) continue; // read fresh before every join: a brake placed mid-pass stops the next one
          await matrix("POST", `/join/${room}`, {});
          invites.delete(roomId);
          directRooms.add(roomId);
          directContent[invite.inviter] = [...new Set([...(directContent[invite.inviter] ?? []), roomId])];
          await matrix("PUT", `/user/${encodeURIComponent(selfId)}/account_data/m.direct`, directContent).catch(error => say(`m.direct not saved: ${error.message}`));
        }
      } catch (error) {
        say(`invite to ${roomId} not answered: ${error.message}`);
      }
    }
  }
  const unjoined = () => joinedAliases.size < rooms.length;
  // The rooms this Persona may speak in: a configured room she has joined, or one of her DMs. One rule for send and for the tail.
  const speakableRoomId = alias => joinedAliases.has(alias) ? roomIdByAlias.get(alias) : directRooms.has(alias) ? alias : null;

  // Join the configured rooms the account is not in, only while the brake is released.
  // One room failing is logged and retried on the next round; it never stops the others.
  async function ensureRooms() {
    const member = new Set((await matrix("GET", "/joined_rooms")).joined_rooms ?? []);
    let free = null;
    for (const alias of rooms.filter(a => !joinedAliases.has(a))) {
      try {
        let roomId = roomIdByAlias.get(alias);
        if (!roomId) ({ room_id: roomId } = await matrix("GET", `/directory/room/${encodeURIComponent(alias)}`));
        roomIdByAlias.set(alias, roomId);
        if (!member.has(roomId)) {
          free ??= await released();
          if (!free) continue;
          await matrix("POST", `/join/${encodeURIComponent(roomId)}`, {});
          freshRooms.add(roomId);
        }
        aliasByRoomId.set(roomId, alias);
        joinedAliases.add(alias);
      } catch (error) {
        say(`room ${alias} not joined: ${error.message}`);
      }
    }
  }

  async function start() {
    if (!started) {
      const saved = await state.load();
      cursor = committed = saved?.nextBatch ?? null;
      seqCounter = saved?.seq ?? 0;
      selfId = (await matrix("GET", "/account/whoami")).user_id;
      try { noteDirect(await matrix("GET", `/user/${encodeURIComponent(selfId)}/account_data/m.direct`)); } catch { /* no m.direct: no DMs yet */ }
      started = true;
    }
    if (unjoined()) await ensureRooms();
  }

  const threadRootOf = e => { const r = e?.content?.["m.relates_to"]; return r?.rel_type === "m.thread" && typeof r.event_id === "string" && r.event_id !== "" ? r.event_id : null; };
  const lineOf = e => ({ sender: e.sender, text: bodyText(e.content, docs.limits.contextBytes), atUnixMs: Number.isSafeInteger(e.origin_server_ts) && e.origin_server_ts >= 0 ? e.origin_server_ts : 0 });

  // The room's recent messages outside threads, oldest first.
  // `truncated`: the homeserver filled the whole window, so older history may exist that this read never saw.
  async function roomRead(roomId) {
    const filter = encodeURIComponent(JSON.stringify({ types: ["m.room.message"] }));
    const reply = await matrix("GET", `/rooms/${encodeURIComponent(roomId)}/messages?dir=b&limit=${FETCH_LIMIT}&filter=${filter}`);
    const chunk = reply.chunk ?? [];
    return { events: chunk.filter(e => isLine(e) && threadRootOf(e) === null).reverse(), truncated: chunk.length >= FETCH_LIMIT };
  }
  const roomEvents = async roomId => (await roomRead(roomId)).events;
  // One thread's messages, root first.
  async function threadEvents(roomId, rootId) {
    const room = encodeURIComponent(roomId);
    const root = await matrix("GET", `/rooms/${room}/event/${encodeURIComponent(rootId)}`);
    const replies = await matrix("GET", `/rooms/${room}/relations/${encodeURIComponent(rootId)}/m.thread?dir=b&limit=${FETCH_LIMIT}`, undefined, V1);
    return [root, ...(replies.chunk ?? []).reverse()].filter(isLine);
  }

  // The context of one delivered event: the messages before it, the newest kept to the byte budget, each line clipped.
  // `round` caches the fetches of one /sync round, so a room or thread is read once however many deliveries it made.
  async function contextFor(roomId, event, round) {
    const root = threadRootOf(event);
    const key = root ? `${roomId}|${root}` : roomId;
    if (!round.has(key)) {
      round.set(key, (root ? threadEvents(roomId, root) : roomEvents(roomId)).catch(error => {
        say(`context for ${roomId}${root ? ` thread ${root}` : ""} not fetched: ${error.message}`);
        return [];
      }));
    }
    const events = await round.get(key);
    let cut = events.findIndex(e => e.event_id === event.event_id);
    if (cut < 0) cut = events.filter(e => e.origin_server_ts < event.origin_server_ts).length;
    const lines = [];
    let bytes = 0;
    for (const e of events.slice(0, cut).reverse()) {
      if (!docs.isMatrixId("@", e.sender)) continue; // a line the tuple would refuse is left out, not the whole delivery
      const l = lineOf(e);
      const size = Buffer.byteLength(l.text) + Buffer.byteLength(l.sender);
      if (lines.length >= CONTEXT_LINES || bytes + size > CONTEXT_BYTES) break;
      bytes += size;
      lines.unshift(l);
    }
    return lines;
  }

  // Follow (docs/bridge.md, "Follow"). Memory only, bounded, purged on every sync pass. Her own send in a public room (not a DM, not encrypted, not
  // bridged) anchors that room for W from the send: `engagements` room id -> { from, ends, counted }, counted being the senders who spoke there in the
  // W before the send (`spoke`, a per-room ring of recent senders with event times) plus whoever the post tagged or replied to, if the event says so.
  // Someone who first speaks after the send is not counted. Only her speaking again re-anchors (and re-snapshots); a follow never extends the window.
  // `followsGiven` room|peer -> times follows were given. A restart empties all of it, which only shortens windows.
  const engagements = new Map();
  const spoke = new Map();
  const followsGiven = new Map();
  const anchored = new Set(); // event ids of her own posts already anchored: the send path and its /sync echo anchor once
  const anchoring = new Map(); // event ids whose anchor is in flight: the send path and its echo never snapshot twice
  const publicRooms = new Map(); // room id -> { ok, at }: the verdicts, a "no" included, so a not-public room is read once per FOLLOW_ROOM_TTL_MS, not every pass
  const platformId = id => platformEvent({ sender: id });
  // true or false is a verdict (kept); null is a failed read: unknown, retryable, kept nowhere.
  async function publicRoom(roomId) {
    if (directRooms.has(roomId)) return false;
    const known = publicRooms.get(roomId);
    if (known && now() - known.at < FOLLOW_ROOM_TTL_MS) return known.ok;
    try {
      const state = await matrix("GET", `/rooms/${encodeURIComponent(roomId)}/state`, undefined, API, AbortSignal.timeout(sendTimeoutMs));
      if (!Array.isArray(state)) return null;
      const ok = !state.some(e => e?.type === "m.room.encryption" || e?.type === "m.bridge" || e?.type === "uk.half-shot.bridge");
      publicRooms.set(roomId, { ok, at: now() });
      return ok;
    } catch (error) {
      say(`follow: room ${roomId} state not read: ${error.message}`);
      return null; // unknown is not public, and not a verdict either
    }
  }
  const eventAt = event => Number.isSafeInteger(event.origin_server_ts) && event.origin_server_ts >= 0 ? event.origin_server_ts : now();
  function purgeFollow() {
    const t = now();
    for (const [room, e] of engagements) if (e.ends + FOLLOW_SLACK_MS <= t) engagements.delete(room);
    for (const [room, ring] of spoke) {
      const kept = ring.filter(r => r.at > t - FOLLOW_WINDOW_MS - FOLLOW_SLACK_MS);
      if (kept.length === 0) spoke.delete(room); else spoke.set(room, kept);
    }
    for (const [key, times] of followsGiven) {
      const kept = times.filter(x => t - x < FOLLOW_HOUR_MS + FOLLOW_SLACK_MS);
      if (kept.length === 0) followsGiven.delete(key); else followsGiven.set(key, kept);
    }
  }
  // Someone spoke in the room (a message from another account, not an edit or a notice): the ring behind the snapshot.
  function noteSpeaker(roomId, event) {
    if (!follow || directRooms.has(roomId) || formOf(event.content) !== "message" || platformEvent(event) || event.sender === selfId) return;
    const ring = spoke.get(roomId) ?? [];
    ring.push({ sender: event.sender, at: eventAt(event) });
    if (ring.length > FOLLOW_RING) ring.shift();
    spoke.set(roomId, ring);
  }
  // Her post at time `at` (the send's own time, or the echo's origin time): snapshot the room. `tagged`: ids from the event's m.mentions, `replyTo` the quoted event id.
  async function anchor(roomId, eventId, at, tagged = [], replyTo = null) {
    if (!follow) return;
    while (anchoring.has(eventId)) await anchoring.get(eventId).catch(() => {}); // the send and its echo meet here: the second waits, then sees whether the first succeeded
    if (anchored.has(eventId)) return;
    const run = (async () => {
      const verdict = await publicRoom(roomId);
      if (verdict === null) return; // the read failed: this post stays retryable, its echo tries again
      anchored.add(eventId);
      if (anchored.size > 200) anchored.delete(anchored.values().next().value);
      if (!verdict) return; // a "no" stays recorded
      const counted = new Set((spoke.get(roomId) ?? []).filter(r => r.at >= at - FOLLOW_WINDOW_MS && r.at <= at).map(r => r.sender));
      for (const id of tagged) counted.add(id);
      if (typeof replyTo === "string" && replyTo !== "") { const author = await authorOf(roomId, replyTo); if (author !== null) counted.add(author); }
      for (const id of [...counted]) if (!docs.isMatrixId("@", id) || id === selfId || platformId(id)) counted.delete(id);
      engagements.set(roomId, { from: at, ends: at + FOLLOW_WINDOW_MS, counted });
    })();
    anchoring.set(eventId, run);
    try { await run; } finally { anchoring.delete(eventId); }
  }
  // Her own event seen in /sync (a send from another client, or the echo of a mouth send, which is then not anchored twice).
  async function noteOwnPost(roomId, event) {
    if (!follow || formOf(event.content) !== "message" || platformEvent(event)) return;
    const relates = event.content["m.relates_to"];
    const tagged = Array.isArray(event.content["m.mentions"]?.user_ids) ? event.content["m.mentions"].user_ids : [];
    await anchor(roomId, event.event_id, eventAt(event), tagged.slice(0, docs.limits.mentions), relates?.is_falling_back === true ? null : relates?.["m.in_reply_to"]?.event_id);
  }
  // Whether this message is a follow, and if so spends one of the peer's K for the hour. Called only for a message that has no better basis.
  async function followed(roomId, event) {
    if (!follow || directRooms.has(roomId) || formOf(event.content) !== "message" || platformEvent(event)) return false;
    const e = engagements.get(roomId), at = eventAt(event), t = now();
    // Both clocks must agree: the message's own time inside the window, and this process's time not past it (a forged or old timestamp reuses nothing).
    if (!e || !e.counted.has(event.sender) || at < e.from - FOLLOW_SLACK_MS || at >= e.ends || t >= e.ends + FOLLOW_SLACK_MS) return false;
    if (!(await publicRoom(roomId))) return false;
    const key = `${roomId}|${event.sender}`;
    const recent = (followsGiven.get(key) ?? []).filter(x => at - x < FOLLOW_HOUR_MS);
    if (recent.length >= FOLLOW_PER_HOUR) { followsGiven.set(key, recent); return false; }
    followsGiven.set(key, [...recent, at]);
    return true;
  }

  // Why a delivered event is for this Persona, from what Matrix says and nothing else: "direct" (a DM), "tag" (m.mentions lists her
  // id, or, from a client that sends no m.mentions at all, the body links her matrix.to pill), "reply" (it replies to, or sits in a
  // thread under, an event her account wrote, so her Self run's words count as hers), "room" (an @room mention). In that order; null
  // when none holds. Her names, her projects' names and the sender's role are the heartbeat's to match (Huginn address.rs), never the
  // mouth's. A reply target that cannot be read is null and logged: unknown, not "not hers".
  async function basisOf(roomId, event, lookup = true) {
    if (directRooms.has(roomId)) return "direct";
    const mentions = event.content["m.mentions"];
    if (Array.isArray(mentions?.user_ids) ? mentions.user_ids.includes(selfId) : mentions === undefined && pillFor(event.content.formatted_body)) return "tag";
    if (lookup && formOf(event.content) === "message" && await repliesToMe(roomId, event.content["m.relates_to"])) return "reply"; // an edit or a notice wakes no one: no fetch for it
    return mentions?.room === true ? "room" : null;
  }
  // A pill is an anchor whose href is exactly her matrix.to link; a longer id, an attribute that merely contains one and a quoted reply do not count.
  const pillFor = html => typeof html === "string" && [...html.replace(/<mx-reply>[\s\S]*?<\/mx-reply>/gi, "").matchAll(/<a\s[^>]*?href\s*=\s*(["'])(.*?)\1/gi)].some(([, , href]) => [selfId, encodeURIComponent(selfId)].some(id => href === `https://matrix.to/#/${id}`));
  const authors = new Map(); // `${room id} ${event id}` -> { sender, excerpt, at }: read results, kept TARGET_TTL_MS, bounded; the key holds the room so another room's reply cannot borrow a hit
  const TARGET_TTL_MS = 60 * 1000; // how long a read of a target is kept: a failure always; a hit only with replyLink (without it a hit is kept until bounded out, as before v4)
  const REPLY_READS_PER_ROUND = 8; // target GETs one sync round may make, whoever asks (basis or reply_to): an event whose uncached targets would pass it waits for the next round
  const KEEP_HIT_MS = 10 * 60 * 1000; // how long transient failures may keep a hit's sender alive after its last good read
  let replyReads = 0;
  const held = new Map(); // key -> entry: targets the round's cap check counted as cached, kept for that event so the 60 s keep cannot lapse between the check and the read and spend a GET the check did not count
  const cachedTarget = key => {
    const known = authors.get(key);
    if (!known) return null;
    if (known.good !== undefined && now() - known.good > KEEP_HIT_MS) return null; // a renewed hit is never served past KEEP_HIT_MS after its last good read, whatever its age
    return now() - known.at < TARGET_TTL_MS || (!replyLink && known.sender !== null) ? known : null;
  };
  // The quoted text, as a single line (invisible and format characters removed; still untrusted text): nil when the event is redacted (the homeserver strips its content), is not text, or is edited
  // (an edit target, or one with an m.replace bundled: the original body is the pre-edit text, which its author replaced), or is nothing but
  // its own reply fallback. Else: the legacy rich-reply fallback (leading "> " lines and the blank line after) is dropped, CR/LF/tab runs
  // become one space, control characters (C0, DEL, C1), format characters (Cf, incl. bidi marks, U+061C, U+200B-200F, U+2060-2064), private use (Co), the tag block U+E0000-E007F and unpaired surrogates are removed, the result is trimmed, then cut to the
  // first 240 code points (not bytes, so no character is split; a combining mark is its own code point) and trimmed again. Computed
  // only when replyLink is on.
  const FALLBACK_LINE = /^>( |$)/;
  const excerptOf = target => {
    const c = target?.content;
    if (!replyLink || target?.unsigned?.redacted_because !== undefined || target?.type !== "m.room.message" || typeof c?.body !== "string") return null;
    if (c["m.relates_to"]?.rel_type === "m.replace" || target.unsigned?.["m.relations"]?.["m.replace"] !== undefined) return null;
    let body = c.body;
    if (c["m.relates_to"]?.["m.in_reply_to"] !== undefined) {
      const lines = body.split("\n");
      while (lines.length > 0 && FALLBACK_LINE.test(lines[0])) lines.shift();
      if (lines.length > 0 && lines[0] === "") lines.shift();
      body = lines.join("\n");
    }
    const line = bodyText({ ...c, body }, docs.limits.contextBytes)
      .replace(/[\r\n\t\u2028\u2029]+/g, " ")
      .replace(/[\u0000-\u001f\u007f-\u009f\u061c\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u2069\u034f\u115f\u1160\u17b4\u17b5\u180b-\u180f\u2065\u2800\u3164\ufe00-\ufe0f\uffa0\ufff0-\ufff8]|[\p{Cf}\p{Co}\u{E0000}-\u{E007F}\u{E0080}-\u{E00FF}\u{E0100}-\u{E0FFF}]|[\uD800-\uDFFF]/gu, "")
      .trim();
    const cut = [...line].slice(0, docs.limits.replyExcerpt).join("").trimEnd();
    return cut === "" ? null : cut;
  };
  async function targetOf(roomId, eventId) {
    const key = `${roomId} ${eventId}`;
    const cached = cachedTarget(key) ?? held.get(key);
    if (cached) return cached; // with replyLink a hit is short too: a later redaction or edit of the target must show at a re-read
    const known = authors.get(key);
    let sender = null, excerpt = null, transient = false;
    replyReads++;
    try {
      const target = await matrix("GET", `/rooms/${encodeURIComponent(roomId)}/event/${encodeURIComponent(eventId)}`, undefined, API, AbortSignal.timeout(sendTimeoutMs));
      if (target?.room_id === undefined || target.room_id === roomId) { // an id from another room is not an answer about this one: no sender, no text
        sender = docs.isMatrixId("@", target?.sender) ? target.sender : null;
        if (sender !== null) excerpt = excerptOf(target);
      }
    } catch (error) {
      transient = error.status === undefined || error.status >= 500 || error.status === 429; // network, timeout, 5xx, 429: the answer is unknown; 403, 404 and any other refusal are an answer
      say(`reply target ${eventId} not read: ${error.message}`);
    }
    const t = now();
    const entry = sender !== null ? { sender, excerpt, at: t, good: t }
      : transient && known?.sender != null && t - known.good <= KEEP_HIT_MS ? { ...known, excerpt: null, at: t } // a transient failure (network, timeout, 5xx, 429) keeps the earlier hit's sender and renews only its age, for at most KEEP_HIT_MS after the last good read; never its excerpt (a re-read happens only after TARGET_TTL_MS, so the excerpt is already as old as a good read may be, and a renewed entry must not serve it again)
      : { sender: null, excerpt: null, at: t }; // any other failure (403, 404, wrong room, no sender), or a hit kept too long, is the failure entry
    authors.set(key, entry);
    if (authors.size > DELIVERED_INDEX) authors.delete(authors.keys().next().value);
    return entry;
  }
  const authorOf = async (roomId, eventId) => (await targetOf(roomId, eventId)).sender;
  // The event a message replies to: its own m.in_reply_to, unless that is a thread's fallback pointer (is_falling_back), which
  // names no one. nil when the target cannot be read or sits in another room.
  // The GETs an event could cost in targetOf: its uncached reply target and thread root. An over-count is harmless (the event waits a round); an under-count would pass the cap.
  const targetsToRead = (roomId, event) => {
    const relates = event.content["m.relates_to"];
    if (!replyLink || formOf(event.content) !== "message") return 0;
    const ids = new Set([relates?.["m.in_reply_to"]?.event_id, relates?.rel_type === "m.thread" ? relates.event_id : undefined].filter(id => typeof id === "string" && id !== ""));
    held.clear(); // events run one at a time: a pin serves only the event whose check made it
    let reads = 0;
    for (const id of ids) {
      const key = `${roomId} ${id}`, hit = cachedTarget(key);
      if (hit) held.set(key, hit); else reads++;
    }
    return reads;
  };
  async function replyToOf(roomId, event) {
    if (!replyLink || formOf(event.content) !== "message") return null;
    const r = event.content["m.relates_to"]?.["m.in_reply_to"];
    const id = r?.event_id;
    if (typeof id !== "string" || id === "" || event.content["m.relates_to"]["is_falling_back"] === true) return null;
    const { sender, excerpt } = await targetOf(roomId, id);
    return sender === null || !docs.isMatrixId("$", id) ? null : { eventId: id, sender, excerpt };
  }
  async function repliesToMe(roomId, relates) {
    const targets = new Set([relates?.["m.in_reply_to"]?.event_id, relates?.rel_type === "m.thread" ? relates.event_id : undefined].filter(id => typeof id === "string" && id !== ""));
    for (const id of targets) if (await authorOf(roomId, id) === selfId) return true;
    return false;
  }

  // The image of one delivered m.image event: { image } (a stored reference) or { refused } (a reason word). With no
  // store nothing is attempted and nothing is said. The url is the event's own content.url; encrypted events and
  // anything the checks refuse leave only the text line.
  async function imageOf(roomId, content) {
    if (!images || content?.msgtype !== "m.image") return {};
    if (content.file !== undefined) return { refused: "encrypted" };
    const got = await fetchImage({ fetch: httpFetch, homeserver, token, mxc: content.url });
    if (got.refusal) return { refused: got.refusal };
    try { return { image: images.put(roomId, got.bytes, got.mime) }; } catch (error) {
      say(`image not stored: ${error?.code ?? "write failed"}`);
      return { refused: "store-failed" };
    }
  }

  // Her presence: the brake decides offline; otherwise a turn in the last presenceGraceMs decides online.
  const presenceFor = free => !free ? "offline" : now() < activeUntil ? "online" : "unavailable";
  const touch = () => { activeUntil = now() + presenceGraceMs; };
  async function setPresence(free) {
    let wanted = presenceFor(free);
    if (wanted === lastPresence) return;
    if (wanted !== "offline" && !(await released())) wanted = "offline"; // never PUT a present state on a stale brake read
    if (wanted === lastPresence) return;
    try {
      await matrix("PUT", `/presence/${encodeURIComponent(selfId)}/status`, { presence: wanted }, API, AbortSignal.timeout(sendTimeoutMs));
      lastPresence = wanted;
    } catch (error) { say(`presence not set: ${error.message}`); }
  }

  // One /sync round. Returns the deliveries it made.
  async function syncOnce() {
    if (!started || unjoined()) await start();
    purgeFollow();
    replyReads = 0;
    held.clear();
    const free = await released(); // the round's brake read for presence, taken before any early return
    await setPresence(free);
    if (pending.length >= maxPending) return []; // unanswered mail is not outrun: the position stays where it is
    const filter = encodeURIComponent(JSON.stringify({ room: { timeline: { limit: CONTEXT } } }));
    const since = cursor === null ? "" : `&since=${encodeURIComponent(cursor)}`;
    const stateSent = presenceFor(free); // the state this poll carries, fixed before the poll: the grace may pass while it is open
    const reply = await matrix("GET", `/sync?timeout=${cursor === null ? 0 : syncTimeoutMs}&set_presence=${stateSent}&filter=${filter}${since}`);
    if (free) lastPresence = stateSent; // a braked round's offline is only believed once its own PUT lands
    const baseline = cursor === null; // first ever sync: history is context, not mail
    for (const event of reply.account_data?.events ?? []) if (event.type === "m.direct") noteDirect(event.content);
    noteInvites(reply.rooms?.invite);
    await answerInvites(); // each join re-reads the brake, as send does
    const made = [];
    const round = new Map();
    let truncated = false; // the pending cap was reached inside this round
    rounds: for (const [roomId, room] of Object.entries(reply.rooms?.join ?? {})) {
      const alias = aliasByRoomId.get(roomId) ?? (directRooms.has(roomId) ? roomId : undefined);
      if (!alias) continue;
      for (const event of [...(room.state?.events ?? []), ...(room.timeline?.events ?? [])]) {
        const shown = event.content?.displayname;
        if (event.type === "m.room.member" && typeof event.state_key === "string" && typeof shown === "string" && shown !== "") displayNames.set(event.state_key, docs.clip(shown, docs.limits.displayBytes));
      }
      if ((room.timeline?.events ?? []).length > 0) tailReads.delete(roomId);
      for (const event of room.timeline?.events ?? []) {
        if (event.type !== "m.room.message" || typeof event.content?.body !== "string") continue;
        if (!docs.isMatrixId("@", event.sender) || !docs.isMatrixId("$", event.event_id)) continue; // an id the tuple would refuse is no event: it could not be delivered, and one such event must not wedge the round
        if (processed.has(event.event_id)) continue; // read already in a round the cap kept from advancing
        const ts = event.origin_server_ts;
        const atUnixMs = Number.isSafeInteger(ts) && ts >= 0 ? ts : now();
        const deliverable = !baseline && !freshRooms.has(roomId) && event.sender !== selfId;
        if (deliverable && pending.length + made.length >= maxPending) { truncated = true; break rounds; }
        if (deliverable && replyLink && replyReads + targetsToRead(roomId, event) > REPLY_READS_PER_ROUND) { say(`event ${event.event_id} not read: round cap on target reads, the rest of the round waits for the next`); truncated = true; break rounds; } // not processed: the next round meets it again, with its read count reset
        if (!baseline && !freshRooms.has(roomId) && event.sender === selfId) await noteOwnPost(roomId, event);
        noteSpeaker(roomId, event);
        let basis = deliverable ? await basisOf(roomId, event) : null;
        if (deliverable && (basis === null || basis === "room") && await followed(roomId, event)) basis = "follow";
        const replyTo = deliverable ? await replyToOf(roomId, event) : null;
        processed.add(event.event_id);
        if (deliverable) {
          const context = await contextFor(roomId, event, round);
          const { image = null, refused = null } = await imageOf(roomId, event.content);
          if (refused !== null) say(`image refused: ${refused}`);
          const mentioned = event.content["m.mentions"]?.user_ids;
          made.push({
            seq: ++seqCounter, roomAlias: alias, roomId, eventId: event.event_id, sender: event.sender,
            senderDisplay: displayNames.get(event.sender) ?? (localpart(event.sender) || event.sender), text: bodyText(event.content, docs.limits.textBytes, refused), atUnixMs,
            context, image,
            mentions: Array.isArray(mentioned) ? mentioned.filter(id => docs.isMatrixId("@", id)).slice(0, docs.limits.mentions) : [],
            isDirect: directRooms.has(roomId), basis, form: formOf(event.content), replyTo,
          });
          if (image) images.hold(image.path, seqCounter);
        }
      }
    }
    pending.push(...made);
    for (const d of made) {
      delivered.set(d.eventId, { roomId: d.roomId, roomAlias: d.roomAlias, seq: d.seq });
      if (delivered.size > DELIVERED_INDEX) delivered.delete(delivered.keys().next().value);
    }
    if (!truncated) { // a truncated round leaves the position where it was: the rest stays on the server
      cursor = reply.next_batch;
      batches.push({ token: reply.next_batch, maxSeq: seqCounter });
      processed.clear();
      freshRooms.clear();
    }
    await commit();
    if (made.length > 0) await state.save({ nextBatch: committed, seq: seqCounter }); // sequence numbers never repeat across a restart
    return made;
  }

  // The persisted position is the newest batch whose deliveries are all acked.
  async function commit(throughSeq = pending.length ? pending[0].seq - 1 : seqCounter) {
    let covered = null;
    for (const b of batches) { if (b.maxSeq <= throughSeq) covered = b; else break; }
    if (covered && covered.token !== committed) {
      committed = covered.token;
      await state.save({ nextBatch: committed, seq: seqCounter });
    }
    batches = batches.filter(b => b.maxSeq > throughSeq);
  }

  // Her m.read receipt, display-only: public, unthreaded, at the event Huginn reports she has taken in. Only for her own
  // persona, an event this mouth delivered to her, in the room it was delivered for (the receipt goes to the delivered
  // room id, never to the reported alias), and only forward of the last receipt that landed in that room. Reports for
  // one room run one at a time, so the order they arrive in is the order they are judged in. Posted only while her
  // brake is released, read fresh before the presence write and again before the POST. Returns the reason nothing
  // was posted, or null.
  function read({ persona: asked, roomAlias, eventId }) {
    const quiet = message => { try { say(message); } catch { /* a throwing logger must not reject a caller that does not await */ } };
    if (asked !== persona) return Promise.resolve("PersonaMismatch");
    const known = delivered.get(eventId);
    if (!known) { quiet("read report names an event this mouth never delivered"); return Promise.resolve("UnknownEvent"); }
    if (roomAlias !== known.roomAlias && roomAlias !== known.roomId) { quiet("read report names another room than the delivery"); return Promise.resolve("RoomMismatch"); }
    const run = async () => {
      if ((readThrough.get(known.roomId)?.seq ?? 0) >= known.seq) return "Behind";
      if (!(await released())) { quiet("receipt not posted: brake held"); return "Brake"; } // none is queued
      touch();
      await setPresence(true);
      if (!(await released())) { quiet("receipt not posted: brake held"); return "Brake"; }
      try {
        await matrix("POST", `/rooms/${encodeURIComponent(known.roomId)}/receipt/m.read/${encodeURIComponent(eventId)}`, {}, API, AbortSignal.timeout(sendTimeoutMs));
      } catch (error) {
        quiet(`receipt in ${known.roomId} not posted: ${error.message}`);
        return "Failed"; // the last receipt that landed stays what it was: a later report for an older event is still Behind
      }
      readThrough.set(known.roomId, { seq: known.seq, eventId });
      return null;
    };
    const tail = (readTails.get(known.roomId) ?? Promise.resolve()).then(run, run); // a report that threw must not wedge the room's later ones
    readTails.set(known.roomId, tail);
    tail.then(() => { if (readTails.get(known.roomId) === tail) readTails.delete(known.roomId); }, () => { if (readTails.get(known.roomId) === tail) readTails.delete(known.roomId); });
    return tail;
  }

  async function ack(throughSeq) {
    pending = pending.filter(d => d.seq > throughSeq);
    images?.acked(throughSeq);
    await commit(pending.length ? pending[0].seq - 1 : seqCounter);
    touch();
  }

  // Her rooms' recent talk, for a reader that was not woken for it: in each room she may speak in, the newest tailPerRoom lines
  // whether or not they were delivered or acked (a delivery the wake rule left unaddressed is acked and gone from `pending`; the tail
  // is where a beat still finds it, and the reader removes what a delivery already shows), the newest tailTotal over all rooms,
  // oldest first. From the same /messages read as a delivery's context, as her account, bodies clipped as context lines are. The
  // basis is what Matrix says on the event itself (no reply lookup: nil is unproven). A room that cannot be read in sendTimeoutMs
  // is logged and absent, so one stuck room never holds the reply.
  // Returns { lines, gaps, rooms }: `rooms` are the rooms read, in read order (configured rooms in join order, then DMs); `gaps` are
  // those of them whose lines are not all in `lines` (read failed or timed out, the read window full so older lines may be unseen, a line past a cap, a line dropped as malformed), in
  // the same order. A room with nothing to show because it is quiet is not a gap. A mouth that has not started knows no room but the
  // configured ones, and all of those are gaps.
  async function tail() {
    if (!started) return { lines: [], gaps: [...rooms], rooms: [...rooms] };
    const targets = new Set();
    for (const alias of [...joinedAliases, ...directRooms]) { const id = speakableRoomId(alias); if (id) targets.add(id); }
    const gapped = new Set();
    const perRoom = await Promise.all([...targets].map(async roomId => {
      const alias = aliasByRoomId.get(roomId) ?? roomId;
      let read = tailReads.get(roomId);
      if (!read || now() - read.at >= TAIL_TTL_MS) {
        let timer;
        const lines = Promise.race([
          roomRead(roomId),
          new Promise((_, fail) => { timer = setTimeout(() => fail(new Error(`no answer in ${sendTimeoutMs} ms`)), sendTimeoutMs); }),
        ]).catch(error => { say(`tail for ${roomId} not fetched: ${error.message}`); return null; }).finally(() => clearTimeout(timer));
        read = { at: now(), lines };
        tailReads.set(roomId, read);
      }
      const got = await read.lines;
      if (got === null) { if (tailReads.get(roomId) === read) tailReads.delete(roomId); gapped.add(alias); return []; }
      const { events, truncated } = got;
      if (truncated || events.length > docs.limits.tailPerRoom) gapped.add(alias);
      const lines = [];
      for (const e of events.slice(-docs.limits.tailPerRoom)) {
        if (!docs.isMatrixId("$", e.event_id) || !docs.isMatrixId("@", e.sender)) { gapped.add(alias); continue; }
        const at = Number.isSafeInteger(e.origin_server_ts) && e.origin_server_ts >= 0 ? e.origin_server_ts : 0;
        lines.push({ roomAlias: alias, eventId: e.event_id, atUnixMs: at, sender: e.sender, form: formOf(e.content), basis: await basisOf(roomId, e, false), text: bodyText(e.content, docs.limits.contextBytes) });
      }
      return lines;
    }));
    const newest = perRoom.flat().sort((a, b) => a.atUnixMs - b.atUnixMs);
    for (const dropped of newest.slice(0, Math.max(0, newest.length - docs.limits.tailTotal))) gapped.add(dropped.roomAlias);
    const order = [...targets].map(id => aliasByRoomId.get(id) ?? id);
    return { lines: newest.slice(-docs.limits.tailTotal), gaps: order.filter(a => gapped.has(a)), rooms: order };
  }
  // The rooms `tail` would read, in its order: what a reply names as unread when the read itself fails outright.
  function speakableRooms() {
    const ids = new Set();
    for (const alias of [...joinedAliases, ...directRooms]) { const id = speakableRoomId(alias); if (id) ids.add(id); }
    return started ? [...ids].map(id => aliasByRoomId.get(id) ?? id) : [...rooms];
  }

  function pendingAfter(afterSeq) {
    return pending.filter(d => d.seq > afterSeq).map(d => ({ ...d, context: d.context.map(c => ({ ...c })), mentions: [...d.mentions], image: d.image && { ...d.image } }));
  }

  // The ids of the messages that moved the room since `basis`, oldest first. Walks the head newest to oldest
  // and stops at the event she saw; with none, at her turn's start time; if she saw an event the page does
  // not reach, the whole page counts.
  async function newerThan(roomId, basis) {
    const filter = encodeURIComponent(JSON.stringify({ types: ["m.room.message"] }));
    const reply = await matrix("GET", `/rooms/${encodeURIComponent(roomId)}/messages?dir=b&limit=${HEAD_PAGE}&filter=${filter}`, undefined, API, AbortSignal.timeout(sendTimeoutMs));
    const moved = [];
    for (const e of reply.chunk ?? []) {
      if (basis.seenEventId !== null ? e.event_id === basis.seenEventId : e.origin_server_ts <= basis.sinceUnixMs) break;
      if (movesRoom(e, selfId)) moved.push(e.event_id);
    }
    return moved.reverse();
  }

  async function send({ requestId, persona: asked, roomAlias, text, basis }) {
    const refuse = (refusal, newer = []) => ({ requestId, eventId: null, refusal, newer });
    if (asked !== persona) return refuse("PersonaMismatch");
    if (!(await released())) return refuse("Brake");
    if (typeof text !== "string" || text.trim() === "") return refuse("EmptyText");
    try {
      if (!started || unjoined()) await start();
      const roomId = speakableRoomId(roomAlias);
      if (!roomId) return refuse("UnknownRoom");
      const result = await gate.run(roomId, async () => {
        if (basis) {
          const newer = await newerThan(roomId, basis);
          if (newer.length > 0) return refuse("RoomMoved", newer);
        }
        if (!(await released())) return refuse("Brake"); // a brake placed during the gate wait or the head read stops this send
        const sent = await matrix("PUT", `/rooms/${encodeURIComponent(roomId)}/send/m.room.message/${encodeURIComponent(requestId)}`, { msgtype: "m.text", body: text }, API, AbortSignal.timeout(sendTimeoutMs));
        return { requestId, eventId: sent.event_id, refusal: null, newer: [] };
      });
      if (result.refusal === null) {
        touch(); setPresence(true).catch(() => {}); // outside the room gate and not awaited: a slow presence write holds neither another send nor this receipt
        if (follow) { const at = now(); anchor(roomId, result.eventId, at).catch(error => say(`follow: send not anchored: ${error.message}`)); } // in the background too: a room-state read that hangs or fails never delays the receipt
      }
      return result;
    } catch (error) {
      say(`send failed: ${error.message}`);
      return refuse("SendFailed");
    }
  }

  // Every room this account shares with `userId` alone, newest first: { roomId, createdAt, marker, encrypted, theirs }, theirs the
  // person's membership ("join" | "invite" | "leave" | "ban" | null when they were never invited). Project rooms are not DMs. A
  // room qualifies when its members are exactly the two of us, or m.direct lists it for the person (a created room whose invite failed).
  async function directsWith(userId) {
    const joined = (await matrix("GET", "/joined_rooms")).joined_rooms ?? [];
    const known = new Set(directContent[userId] ?? []);
    const candidates = [...known, ...joined.filter(id => !known.has(id) && !aliasByRoomId.has(id))].slice(0, DM_SCAN);
    const found = [];
    for (const roomId of candidates) {
      let events;
      try { events = await matrix("GET", `/rooms/${encodeURIComponent(roomId)}/state`); } catch (error) {
        if (/ 40[34] /.test(error.message)) continue; // a room m.direct still lists that this account has left or forgotten
        throw error;
      }
      const members = events.filter(e => e.type === "m.room.member");
      const mine = members.find(e => e.state_key === selfId);
      if (mine?.content?.membership !== "join") continue;
      const others = members.filter(e => e.state_key !== selfId);
      const theirs = others.find(e => e.state_key === userId);
      if (!(known.has(roomId) || (others.length === 1 && theirs))) continue;
      const create = events.find(e => e.type === "m.room.create");
      found.push({
        roomId, createdAt: create?.origin_server_ts ?? 0, marker: create?.content?.["org.gamecult.open_request"] ?? null,
        encrypted: events.some(e => e.type === "m.room.encryption"), theirs: theirs?.content?.membership ?? null,
      });
    }
    return found.sort((a, b) => b.createdAt - a.createdAt);
  }

  // The first line of an open: the invite is the ask and the room is not end-to-end encrypted, and the line says so.
  // Every line into a room this path created carries the notice until one has gone out (a restart repeats it once at worst).
  async function putLine(roomId, requestId, text, made) {
    return gate.run(roomId, async () => {
      if (!(await released())) return null;
      const body = made && !noticed.has(roomId) ? `${NOT_E2EE}\n\n${text}` : text;
      const sent = (await matrix("PUT", `/rooms/${encodeURIComponent(roomId)}/send/m.room.message/${encodeURIComponent(requestId)}`, { msgtype: "m.text", body }, API, AbortSignal.timeout(sendTimeoutMs))).event_id;
      if (made) noticed.add(roomId);
      return sent;
    });
  }

  // A Persona's request for a direct room with one person. One outcome word and, for Opened and Existing, the room and the line's event.
  function openDirect(request) {
    const key = typeof request.userId === "string" ? request.userId : "";
    const run = () => openDirectNow(request);
    const tail = (personTails.get(key) ?? Promise.resolve()).then(run, run);
    personTails.set(key, tail);
    const done = () => { if (personTails.get(key) === tail) personTails.delete(key); };
    tail.then(done, done);
    return tail;
  }
  async function openDirectNow({ requestId, persona: asked, userId, text }) {
    const answer = (outcome, roomId = null, eventId = null) => { say(`open direct ${requestId}: ${outcome}${roomId ? ` ${roomId}` : ""}`); return { requestId, outcome, roomId, eventId }; };
    if (asked !== persona) return answer("PersonaMismatch");
    if (!(await released())) return answer("Brake");
    if (typeof text !== "string" || text.trim() === "") return answer("EmptyText");
    try {
      if (!started || unjoined()) await start();
      if (!serverName || typeof userId !== "string" || userId.slice(userId.indexOf(":") + 1) !== serverName) return answer("ForeignUser");
      if (userId === selfId || await isServed(userId)) return answer("NotAPerson");
      try { await matrix("GET", `/profile/${encodeURIComponent(userId)}`); } catch (error) {
        if (/ 404 /.test(error.message)) return answer("NoSuchUser");
        throw error;
      }
      const rooms = await directsWith(userId);
      const usable = rooms.filter(r => !r.encrypted); // a line is never sent into an encrypted room
      const resumed = usable.find(r => r.marker === requestId); // a repeat of this very request after a crash
      const live = resumed ?? usable.find(r => r.theirs === "join");
      if (live) {
        const eventId = await putLine(live.roomId, requestId, text, live.marker !== null);
        return eventId === null ? answer("Brake") : answer(resumed ? "Opened" : "Existing", live.roomId, eventId);
      }
      const latest = rooms.find(r => ["invite", "leave", "ban"].includes(r.theirs)); // an invite or a decline stands in an encrypted room too
      if (latest?.theirs === "invite") return answer("InvitePending");
      if (latest) return answer("Declined"); // left or banned, and nothing newer: only the person reopens it, by inviting her
      let roomId = usable.find(r => r.theirs === null && r.marker !== null)?.roomId ?? null; // made here earlier, the invite never went out
      if (roomId === null) {
        if (openedOn.get(userId) === dayOf(now())) return answer("DailyLimit");
        if (!(await released())) return answer("Brake");
        ({ room_id: roomId } = await matrix("POST", "/createRoom", { preset: "private_chat", is_direct: true, creation_content: { "m.federate": false, "org.gamecult.open_request": requestId } }, API, AbortSignal.timeout(sendTimeoutMs)));
        openedOn.set(userId, dayOf(now()));
        directRooms.add(roomId);
        directContent[userId] = [...new Set([...(directContent[userId] ?? []), roomId])];
        await matrix("PUT", `/user/${encodeURIComponent(selfId)}/account_data/m.direct`, directContent).catch(error => say(`m.direct not saved: ${error.message}`));
      }
      if (!(await released())) return answer("Brake");
      await matrix("POST", `/rooms/${encodeURIComponent(roomId)}/invite`, { user_id: userId, reason: docs.clip(text, 300) }, API, AbortSignal.timeout(sendTimeoutMs));
      const eventId = await putLine(roomId, requestId, text, true);
      if (eventId === null) return answer("Brake");
      touch(); setPresence(true).catch(() => {});
      return answer("Opened", roomId, eventId);
    } catch (error) {
      say(`open direct failed: ${error.message}`);
      return answer("SendFailed");
    }
  }

  // A top-level refusal of the event itself, ported from Huginn's room_scope refuse_event: a sender whose localpart (after leading
  // underscores) starts with the platform's name, or has it after an underscore, or followed by `bot` or an underscore; or a content
  // key naming it. Identifier rules, not language reading.
  const platformEvent = event => {
    const local = String(event?.sender ?? "").replace(/^@+/, "").split(":")[0].toLowerCase();
    return local.replace(/^_+/, "").startsWith(PLATFORM) || local.includes(`_${PLATFORM}`) || local.includes(`${PLATFORM}bot`) || local.includes(`${PLATFORM}_`)
      || Object.keys(event?.content ?? {}).some(key => key.toLowerCase().includes(PLATFORM));
  };
  const IMAGE_WORD = { "no-url": "NoAttachment", encrypted: "TypeRefused", "wrong-type": "TypeRefused", "bad-header": "TypeRefused", "too-many-pixels": "TooLarge", "too-big": "TooLarge", "fetch-failed": "FetchFailed", "store-failed": "StoreFailed" };

  // A Persona asks her mouth to keep one named Matrix file. The request is a room and an event id; the mouth reads the event, takes
  // its content.url, fetches and checks the bytes, stores them, and posts the contract's inert receipt as her. One deadline owns the
  // whole of it. The answer is one fixed outcome word and, for a stored file, the resolved room id and the store reference.
  async function fetchAttachment({ requestId, persona: asked, room, eventId }) {
    const deadline = AbortSignal.timeout(fetchDeadlineMs);
    const seen = { room: "-", event: "-", size: "-", type: "-", sha256: "-" };
    const idOr = (sigil, v) => docs.isFetchId(sigil, v) ? v : "-"; // a hostile id never reaches the log
    let answered = false; // one log line per fetch: a gate step that finishes after the deadline answered is silent
    const done = (outcome, { image = null, roomId = null, receipt = null, logged = "skipped", cause = null } = {}) => {
      if (!answered) say(`fetch persona=${persona} room=${seen.room} event=${seen.event} outcome=${outcome} size=${seen.size} type=${seen.type} sha256=${seen.sha256} receipt=${logged}${cause === null ? "" : ` cause=${cause}`}`);
      answered = true;
      return { requestId, outcome, roomId, image, receipt };
    };
    if (asked !== persona) return done("PersonaMismatch");
    if (!(await released())) return done("Brake");
    if (!images) return done("StoreFailed");
    const at = now();
    while (fetchTimes.length > 0 && at - fetchTimes[0] >= 3_600_000) fetchTimes.shift();
    if (fetchTimes.length >= FETCH_PER_HOUR) return done("RateLimited");
    fetchTimes.push(at);
    seen.event = idOr("$", eventId);
    if (!docs.isFetchId("$", eventId)) return done("NoAttachment");
    if (!docs.isFetchId("!", room) && !docs.isFetchId("#", room)) return done("NotAMember");
    let stored = false; // images.put has run: a deadline after it leaves a file and no receipt
    const aborted = new Promise((_, reject) => deadline.addEventListener("abort", () => reject(deadline.reason), { once: true }));
    aborted.catch(() => {});
    const within = promise => Promise.race([promise, aborted]);
    try {
      if (!started || unjoined()) await within(start());
      const roomId = speakableRoomId(room) ?? room;
      if (!docs.isFetchId("!", roomId) && !docs.isFetchId("#", roomId)) return done("NotAMember"); // an alias the directory answered with a strange id
      seen.room = idOr("!", roomId);
      return await within(gate.run(roomId, async () => {
        deadline.throwIfAborted(); // re-checked after the gate wait: a queued fetch does not run past its deadline
        if (!(await released())) return done("Brake"); // nor past a brake placed while it waited
        const read = (method, path, body, prefix = API) => matrix(method, path, body, prefix, deadline);
        const enc = encodeURIComponent;
        const member = (await read("GET", "/joined_rooms")).joined_rooms;
        if (!Array.isArray(member) || !member.includes(roomId)) return done("NotAMember");
        const state = await read("GET", `/rooms/${enc(roomId)}/state`);
        if (!Array.isArray(state)) return done("FetchFailed");
        if (state.some(e => e?.type === "m.bridge" || e?.type === "uk.half-shot.bridge")) return done("BridgedRoom");
        if (state.some(e => e?.type === "m.room.encryption")) return done("TypeRefused");
        let event;
        try { event = await read("GET", `/rooms/${enc(roomId)}/event/${enc(eventId)}`); } catch (error) {
          if (error.status === 403 || error.status === 404) return done("NotAMember");
          throw error;
        }
        if (event?.room_id !== roomId) return done("NotAMember");
        if (!docs.isFetchId("$", event.event_id)) return done("NoAttachment"); // the receipt names the event the homeserver returned, not the one asked for
        if (platformEvent(event)) return done("BridgedRoom");
        const content = event.content;
        if (event.type === "m.room.encrypted") return done("TypeRefused");
        const relates = content?.["m.relates_to"];
        if (event.type !== "m.room.message" || content === null || typeof content !== "object" || Object.keys(content).length === 0 || relates?.rel_type === "m.replace") return done("NoAttachment");
        if (content.file !== undefined) return done("TypeRefused");
        if (content.msgtype === "m.audio" || content.msgtype === "m.video") return done("TypeRefused");
        if (content.msgtype !== "m.image" && content.msgtype !== "m.file") return done("NoAttachment");
        if (typeof content.url !== "string" || !content.url.startsWith("mxc://")) return done("NoAttachment");
        // The album root is the sender's word: it names a URL path below, so it must be an event id or nothing is built from it.
        const albumRoot = relates?.rel_type === "m.thread" ? relates.event_id : null;
        if (relates?.rel_type === "m.thread" && !docs.isFetchId("$", albumRoot)) return done("NoAttachment");
        const got = await fetchImage({ fetch: httpFetch, homeserver, token, mxc: content.url, signal: deadline });
        deadline.throwIfAborted();
        if (got.refusal) return done(IMAGE_WORD[got.refusal] ?? "FetchFailed");
        const computed = createHash("sha256").update(got.bytes).digest("hex");
        Object.assign(seen, { size: got.bytes.length, type: got.mime, sha256: computed });
        const info = content.info;
        const claimed = info !== null && typeof info === "object" && !Array.isArray(info) && Object.hasOwn(info, "sha256");
        if (claimed && info.sha256 !== computed) return done("HashMismatch");
        deadline.throwIfAborted();
        let image;
        try { image = images.put(roomId, got.bytes, got.mime); } catch (error) { return done("StoreFailed", { cause: causeOf(error) }); }
        stored = true;
        if (!claimed) return done("StoredUnverified", { image, roomId });
        const { receipt, word, cause } = await postFileReceipt(roomId, albumRoot, event.event_id, computed, got.bytes.length, deadline);
        return done("Stored", { image, roomId, receipt, logged: word, cause });
      }));
    } catch (error) {
      return done("FetchFailed", { logged: stored ? "failed" : "skipped", cause: causeOf(error) });
    }
  }

  // The contract's receipt for a file the mouth stored, posted as her own user: a thread reply when the file is in an album thread,
  // else a plain event referencing the file. Idempotence is Matrix's: her own earlier receipt in the target's relations. A relations
  // read or a PUT that fails, a held brake, proves nothing and posts nothing: the file stays stored and the receipt is nil. A deadline
  // is not such a failure and leaves the caller (it throws).
  async function postFileReceipt(roomId, albumRoot, fileEventId, sha256, size, deadline) {
    const none = (word, cause = null) => ({ receipt: null, word, cause });
    try {
      const room = encodeURIComponent(roomId);
      const [target, relation] = albumRoot === null ? [fileEventId, "m.reference"] : [albumRoot, "m.thread"];
      let from = null, exhausted = false;
      for (let page = 0; page < RELATION_PAGES && !exhausted; page++) {
        const reply = await matrix("GET", `/rooms/${room}/relations/${encodeURIComponent(target)}/${relation}/${docs.fileReceivedType}?dir=b&limit=100${from === null ? "" : `&from=${encodeURIComponent(from)}`}`, undefined, V1, deadline);
        const mine = (reply?.chunk ?? []).some(e => e?.type === docs.fileReceivedType && e.sender === selfId && !e.unsigned?.redacted_because && e.content?.[docs.fileReceivedType]?.event_id === fileEventId);
        if (mine) return { receipt: "Existing", word: "existing" };
        if (typeof reply?.next_batch === "string" && reply.next_batch !== "") from = reply.next_batch; else exhausted = true;
      }
      if (!exhausted) return none("failed"); // three pages and more remain: not proven
      if (!(await released())) return none("skipped"); // read again immediately before the PUT, as send does inside its gate
      deadline.throwIfAborted();
      const relates = albumRoot === null ? { rel_type: "m.reference", event_id: fileEventId } : { rel_type: "m.thread", event_id: albumRoot };
      const txn = `fr-${createHash("sha256").update(`${persona}|${fileEventId}`).digest("hex")}`;
      await matrix("PUT", `/rooms/${room}/send/${docs.fileReceivedType}/${txn}`, { "m.relates_to": relates, [docs.fileReceivedType]: { event_id: fileEventId, sha256, size } }, API, deadline);
      return { receipt: "Posted", word: "posted" };
    } catch (error) {
      if (deadline.aborted) throw error;
      return none("failed", causeOf(error));
    }
  }

  async function run(signal) {
    const pause = ms => new Promise(r => { const t = setTimeout(r, ms); signal?.addEventListener("abort", () => { clearTimeout(t); r(); }, { once: true }); });
    while (!signal?.aborted) {
      try {
        await syncOnce();
        if (pending.length >= maxPending) await pause(1000);
      } catch (error) {
        say(`sync failed: ${error.message}`);
        await pause(5000);
      }
    }
  }

  // A plain copy of the follow memory, for tests: anchors with their counted senders, the sender ring, and how many cap entries are held.
  const followState = () => ({ engagements: [...engagements].map(([room, e]) => ({ room, from: e.from, ends: e.ends, counted: [...e.counted] })), spoke: [...spoke].map(([room, ring]) => ({ room, senders: ring.map(r => r.sender) })), followsGiven: followsGiven.size });
  return { followState, start, syncOnce, ack, read, pendingAfter, tail, speakableRooms, send, openDirect, fetchAttachment, run, get userId() { return selfId; }, get pendingCount() { return pending.length; } };
}

// The log's word for a failure: error class and HTTP status only, never the message (it can hold a URL, an mxc or a sender-chosen id).
function causeOf(error) {
  return `${/^[A-Za-z]{1,32}$/.test(error?.name) ? error.name : "Error"}/${Number.isInteger(error?.status) ? error.status : "-"}`;
}

// Replace every secret in a message. `secrets` is one string or a list.
export function redact(message, secrets) {
  let out = String(message);
  for (const secret of [secrets].flat()) if (secret) out = out.split(secret).join("[redacted]");
  return out;
}

// Run `fn`; whatever it throws leaves with every secret redacted.
export async function guarded(secrets, fn) {
  try { return await fn(); } catch (error) { throw new Error(redact(error?.message ?? String(error), secrets)); }
}

function localpart(userId) {
  return userId.replace(/^@/, "").split(":")[0];
}

// Brake: released only when the instance's control store decodes to a released
// brake. Absent, undecodable or held reads are all "not released".
export function createBrakeReader({ controlPath, readStore, decode }) {
  return async () => {
    try {
      const read = readStore(controlPath);
      if (read.absent) return false;
      const envelope = read.envelopes.find(e => e.type === BRAKE_TYPE && e.key === "brake");
      if (!envelope) return false;
      const value = decode(new Uint8Array(envelope.payload));
      const released = Array.isArray(value) ? value[0] : value instanceof Map ? value.get(0) ?? value.get("released") : value?.[0] ?? value?.["0"] ?? value?.released;
      return released === true;
    } catch {
      return false;
    }
  };
}

// The RUDP connection id is derived from the credential, so a peer without it cannot open the session.
export function connectionIdFor(connectionKey) {
  return createHash("sha256").update(connectionKey).digest().readUInt32BE(0);
}

// The reply to a chat_pending_request: deliveries oldest first, each encoded on its own so one that
// will not encode is skipped and logged without silencing the rest, up to the reply byte budget.
// The caller acks what it has read and asks again.
// A v4 reply (v4 true, with v3) is the v3 reply with each delivery's reply_to counted in its size, like any field: one oversize delivery is still alone on its page.
// A v3 reply also carries `tail` (room lines, oldest first), trimmed oldest first to what the byte budget has left after the deliveries,
// and `tail_gaps`: the rooms of `tail.rooms`, in that order, with lines missing from the reply: those `tail.gaps` names plus every room
// whose line the byte budget or an unencodable line dropped here.
export function pageOf(persona, deliveries, msgpack, log = () => {}, v3 = false, tail = { lines: [], gaps: [], rooms: [] }, v4 = false) {
  const page = [];
  let bytes = 0;
  for (const delivery of deliveries) {
    let tuple, size;
    try { tuple = (v4 ? docs.deliveryV4Tuple : v3 ? docs.deliveryV3Tuple : docs.deliveryTuple)(delivery); size = msgpack.encode(tuple).length; } catch (error) {
      log(`delivery ${delivery?.seq} skipped: ${error?.message ?? error}`);
      continue;
    }
    if (page.length > 0 && bytes + size > docs.limits.replyBytes) break;
    page.push(tuple);
    bytes += size;
  }
  if (!v3) return [persona, page];
  const lines = [];
  const gapped = new Set(tail.gaps);
  let full = false;
  for (const line of tail.lines.slice().reverse()) {
    let tuple, size;
    try { tuple = docs.tailLineTuple(line); size = msgpack.encode(tuple).length; } catch (error) {
      log(`tail line ${line?.eventId} skipped: ${error?.message ?? error}`);
      gapped.add(line?.roomAlias);
      continue;
    }
    if (full || bytes + size > docs.limits.replyBytes) { full = true; gapped.add(line.roomAlias); continue; }
    lines.unshift(tuple);
    bytes += size;
  }
  return [persona, page, lines, tail.rooms.filter(room => gapped.has(room))];
}

// CultNet RUDP endpoint on loopback, one for every Persona in `mouths` (persona -> mouth).
// Every client (each Persona process and heartbeat dials with its own socket) gets its own
// session, and a request is answered on the session it arrived on. Requests arrive as
// document_put_raw messages. A request naming a Persona this
// process does not serve is answered with a typed UnknownPersona refusal.
export async function startChatEndpoint({ mouths, runtime, port, connectionKey, runtimeId, socketFactory = createSocket, host = "127.0.0.1", log = () => {}, replyLink = false, fetchEnabled = false }) {
  if (!/^127\.\d+\.\d+\.\d+$/.test(host)) throw new Error("the chat endpoint listens on loopback only");
  const { msgpack, cultnet } = runtime;
  const socket = socketFactory("udp4");
  await new Promise((ok, fail) => { socket.once("error", fail); socket.once("listening", ok); socket.bind(port, host); });
  const connectionId = connectionIdFor(connectionKey);
  const server = new cultnet.CultNetRudpSocketTransportServer({ runtimeId, socket, connectionId, initialSequence: 100, resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200,
    // A client keeps its link between requests and does not redial, so only a client gone for an hour (a crash that sent no goodbye) is reaped.
    peerTimeoutMs: 3_600_000 });
  const respond = (peer, persona, kind, recordKey, tuple) => server.send(peer, "schema", msgpack.encode(cultnet.encodeCultNetMessageForWire(
    docs.envelope({ kind, recordKey, payload: msgpack.encode(tuple), sourceRuntimeId: runtimeId, sourceAgentId: persona, sourceRole: "bifrost-persona-matrix-mouth" }), "cultnet.schema.v0")));
  const refuseUnknown = (peer, persona) => respond(peer, persona, docs.kinds.refusal, persona, docs.refusalTuple({ persona, refusal: "UnknownPersona" }));
  const served = [docs.kinds.pendingRequest, docs.kinds.pendingRequestV3, ...(replyLink ? [docs.kinds.pendingRequestV4] : []), docs.kinds.ack, docs.kinds.read, docs.kinds.sendRequest, docs.kinds.sendRequestV2, docs.kinds.openDirectRequest, ...(fetchEnabled ? [docs.kinds.fetchRequest] : [])];
  const handle = async ({ peer, channelId, payload }) => {
    try {
      if (channelId !== "schema") throw new Error("chat request arrived on a foreign channel");
      const message = cultnet.parseCultNetMessage(msgpack.decode(payload), "cultnet.schema.v0");
      const document = docs.checkEnvelope(message, null);
      if (!served.includes(document.schemaId)) { // a kind this mouth does not serve is answered, loudly, and logged: never dropped
        log(`chat endpoint refused a request of an unknown kind: ${String(document.schemaId).slice(0, 120)}`);
        const who = typeof document.sourceAgentId === "string" && SLUG.test(document.sourceAgentId) ? document.sourceAgentId : "unknown";
        return respond(peer, who, docs.kinds.refusal, who, docs.refusalTuple({ persona: who, refusal: "UnknownKind" }));
      }
      const tuple = msgpack.decode(document.payload);
      if (document.schemaId === docs.kinds.openDirectRequest) {
        const request = docs.parseOpenDirectRequest(tuple);
        const mouth = mouths.get(request.persona);
        const receipt = mouth ? await mouth.openDirect(request) : { requestId: request.requestId, outcome: "UnknownPersona", roomId: null, eventId: null };
        respond(peer, request.persona, docs.kinds.openDirectReceipt, receipt.requestId, docs.openDirectReceiptTuple(receipt));
      } else if (document.schemaId === docs.kinds.fetchRequest) { // only reachable with fetchEnabled: `served` drops it otherwise
        const request = docs.parseFetchRequest(tuple);
        const mouth = mouths.get(request.persona);
        const receipt = mouth ? await mouth.fetchAttachment(request) : { requestId: request.requestId, outcome: "UnknownPersona", roomId: null, image: null, receipt: null };
        respond(peer, request.persona, docs.kinds.fetchReceipt, receipt.requestId, docs.fetchReceiptTuple(receipt));
      } else if (document.schemaId === docs.kinds.pendingRequest || document.schemaId === docs.kinds.pendingRequestV3 || document.schemaId === docs.kinds.pendingRequestV4) {
        const v4 = document.schemaId === docs.kinds.pendingRequestV4; // only reachable with replyLink: `served` drops it otherwise
        const v3 = v4 || document.schemaId === docs.kinds.pendingRequestV3; // the kind asked by names the answer: v1 gets the 12-field v2 answer, as before
        const request = docs.parsePendingRequest(tuple);
        const mouth = mouths.get(request.persona);
        if (!mouth) return refuseUnknown(peer, request.persona);
        const tail = v3 ? await mouth.tail().catch(error => { log(`tail not read: ${error?.message ?? error}`); const rooms = mouth.speakableRooms(); return { lines: [], gaps: rooms, rooms }; }) : undefined;
        respond(peer, request.persona, v4 ? docs.kinds.pendingV4 : v3 ? docs.kinds.pendingV3 : docs.kinds.pending, request.persona, pageOf(request.persona, mouth.pendingAfter(request.afterSeq), msgpack, log, v3, tail, v4));
      } else if (document.schemaId === docs.kinds.ack) {
        const request = docs.parseAck(tuple);
        const mouth = mouths.get(request.persona);
        if (!mouth) return refuseUnknown(peer, request.persona);
        await mouth.ack(request.throughSeq);
      } else if (document.schemaId === docs.kinds.read) {
        const request = docs.parseRead(tuple);
        const mouth = mouths.get(request.persona);
        if (!mouth) return refuseUnknown(peer, request.persona);
        mouth.read(request).catch(error => log(`read report failed: ${error?.message ?? error}`)); // the peer's next request does not wait on Matrix
      } else {
        const v2 = document.schemaId === docs.kinds.sendRequestV2;
        const request = v2 ? docs.parseSendRequestV2(tuple) : docs.parseSendRequest(tuple);
        const mouth = mouths.get(request.persona);
        const receipt = mouth ? await mouth.send(request) : { requestId: request.requestId, eventId: null, refusal: "UnknownPersona", newer: [] };
        if (v2) respond(peer, request.persona, docs.kinds.sendReceiptV2, receipt.requestId, docs.sendReceiptV2Tuple(receipt));
        else respond(peer, request.persona, docs.kinds.sendReceipt, receipt.requestId, docs.sendReceiptTuple(receipt));
      }
    } catch (error) {
      log(`chat endpoint rejected a frame: ${error?.message ?? error}`);
    }
  };
  // A peer's requests run in order; peers do not wait on one another.
  const busy = new WeakMap();
  const frame = value => { busy.set(value.peer, (busy.get(value.peer) ?? Promise.resolve()).then(() => handle(value))); };
  server.on("frame", frame);
  // A socket error is logged; the endpoint keeps serving.
  server.on("error", error => log(`chat endpoint socket error: ${error?.message ?? error}`));
  return { address: socket.address(), close() { server.off("frame", frame); server.close(); } };
}

export function cultcacheState({ runtime, path }) {
  const def = runtime.defineDocumentType({
    type: "gamecult.persona.chat_cursor.v1", schemaName: "gamecult.persona.chat_cursor.v1", schemaId: "gamecult.persona.chat_cursor.v1",
    schemaVersion: "gamecult.persona.chat_cursor.v1", contentHash: "gamecult.persona.chat_cursor.v1", global: false, name: "key",
    schema: { parse: v => ({ key: String(v.key), nextBatch: v.nextBatch ?? null, seq: Number(v.seq) }) },
  });
  const cache = runtime.CultCache.builder().withDocumentType(def).withGenericStore(new runtime.SingleFileMessagePackBackingStore(path)).build();
  const unwrap = v => (Array.isArray(v) ? v[0] : v?.value ?? v);
  return {
    async load() { await cache.pullAllBackingStores(); return unwrap(cache.get(def, "cursor")) ?? null; },
    async save({ nextBatch, seq }) { await cache.put(def, "cursor", { key: "cursor", nextBatch, seq }); },
  };
}

export function loadRuntime(cultRoot) {
  const at = name => createRequire(resolve(cultRoot, "packages", name, "package.json"));
  const cc = at("cultcache-ts")("@gamecult/cultcache-ts"), cn = at("cultnet-ts")("@gamecult/cultnet-ts"), mp = at("cultnet-ts")("@msgpack/msgpack");
  return { CultCache: cc.CultCache, SingleFileMessagePackBackingStore: cc.SingleFileMessagePackBackingStore, defineDocumentType: cc.defineDocumentType, readSingleFileStore: cc.readSingleFileStore, cultnet: cn, msgpack: mp };
}

// The v4 flag: on only for exactly "1"; unset, "0", "true", "yes" and anything else are off.
export const replyLinkFromEnv = env => env.CHAT_PENDING_V4 === "1";
// The fetch flag, the same way: on only for exactly "1". Off, the fetch request kind is an unknown kind.
export const fetchFromEnv = env => env.CHAT_FETCH === "1";
// The follow flag, the same way. Off, the mouth never emits basis "follow" (Huginn's parser refuses an unknown basis until its Basis::Follow lands).
export const followFromEnv = env => env.CHAT_FOLLOW === "1";

function required(env, name) {
  if (!env[name]) throw new Error(`${name} is required`);
  return env[name];
}
function credential(env, name) {
  return readFileSync(join(required(env, "CREDENTIALS_DIRECTORY"), name), "utf8").trim();
}

const ROOM_ALIAS = /^#[^\s:,]+:[^\s,]+$/;

// The env variable holding one Persona's project rooms: MATRIX_ROOMS_<SLUG>, the slug
// upper-cased with "." and "-" turned into "_".
export function projectRoomsVariable(persona) {
  return `MATRIX_ROOMS_${persona.toUpperCase().replace(/[.-]/g, "_")}`;
}

// One Persona's rooms: the shared MATRIX_ROOMS plus her own project rooms, deduplicated.
// Another Persona's project variable is never read. A malformed project alias is refused.
export function roomsFor(env, persona) {
  const list = text => text.split(",").map(s => s.trim()).filter(Boolean);
  const name = projectRoomsVariable(persona);
  const project = list(env[name] ?? "");
  const bad = project.find(alias => !ROOM_ALIAS.test(alias));
  if (bad) throw new Error(`${name} has a malformed room alias: ${JSON.stringify(bad)} (expected #name:server)`);
  return [...new Set([...list(required(env, "MATRIX_ROOMS")), ...project])];
}

// PERSONAS is the comma-separated list of Persona slugs this process speaks for.
// Each slug names its credential (matrix-token-<slug>), its cursor directory under
// STATE_DIRECTORY, its image store at IMAGE_STORE_ROOT/<slug> (optional) and its brake at CONTROL_ROOT/<slug>/control.cc.
export async function main(env = process.env) {
  const personas = required(env, "PERSONAS").split(",").map(s => s.trim()).filter(Boolean);
  if (personas.length === 0 || new Set(personas).size !== personas.length || !personas.every(p => SLUG.test(p))) throw new Error("PERSONAS must be distinct slugs");
  if (new Set(personas.map(projectRoomsVariable)).size !== personas.length) throw new Error("PERSONAS must map to distinct MATRIX_ROOMS_<SLUG> variables");
  const tokens = new Map(personas.map(p => [p, credential(env, `matrix-token-${p}`)]));
  const chatKey = credential(env, "chat-key");
  const secrets = [...tokens.values(), chatKey];
  return guarded(secrets, async () => {
    const runtime = loadRuntime(resolve(env.CULTLIB_ROOT ?? resolve(import.meta.dirname, "..", "..", "CultLib")));
    const log = line => console.error(redact(line, secrets));
    const homeserver = required(env, "MATRIX_HOMESERVER").replace(/\/$/, "");
    const serverName = required(env, "MATRIX_SERVER_NAME");
    const roomsByPersona = new Map(personas.map(persona => [persona, roomsFor(env, persona)]));
    const stateRoot = required(env, "STATE_DIRECTORY");
    const controlRoot = env.CONTROL_ROOT ?? "/etc/gamecult/minds";
    const imageRoot = env.IMAGE_STORE_ROOT ? resolve(env.IMAGE_STORE_ROOT) : null; // unset: chat images stay the typed text line
    const gate = createRoomGate(); // one for every Persona: two Minds in one room take turns
    const fetchEnabled = fetchFromEnv(env);
    const replyLink = replyLinkFromEnv(env); // default off: Huginn pins the v3 tuple bytes until it reads v4
    const mouths = new Map(personas.map(persona => {
      mkdirSync(join(stateRoot, persona), { recursive: true });
      return [persona, createMouth({
        persona, homeserver, serverName, rooms: roomsByPersona.get(persona), token: tokens.get(persona), gate, replyLink, follow: followFromEnv(env), isServed: async id => { await Promise.all([...mouths.values()].map(m => m.start().catch(() => {}))); return [...mouths.values()].some(m => m.userId === id); }, log: line => log(`${persona}: ${line}`),
        images: imageRoot ? createImageStore({ root: join(imageRoot, persona), log: line => log(`${persona}: ${line}`) }) : null,
        state: cultcacheState({ runtime, path: join(stateRoot, persona, "cursor.cc") }),
        brake: createBrakeReader({ controlPath: join(controlRoot, persona, "control.cc"), readStore: runtime.readSingleFileStore, decode: runtime.msgpack.decode }),
      })];
    }));
    const listen = required(env, "CHAT_RUDP_LISTEN"); // host:port, loopback
    const colon = listen.lastIndexOf(":");
    await startChatEndpoint({ mouths, runtime, host: listen.slice(0, colon), port: Number(listen.slice(colon + 1)), connectionKey: chatKey, runtimeId: "bifrost-persona-mouth", log, replyLink, fetchEnabled });
    await Promise.all([...mouths.values()].map(mouth => mouth.run()));
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(error?.message ?? String(error)); process.exitCode = 1; });
}

// gamecult.persona.chat_*.v1, the v2 send pair and the v2 pending answer: the documents Huginn and the Matrix mouth exchange.
// Each kind is a positional tuple (the CultCache payload shape); this module owns
// the field order, the parsers and the envelope check. It does no I/O and loads no
// runtime, so the wire encoder (MessagePack) stays with the caller. The image path, mime and size bounds it checks
// come from persona-chat-images.mjs, which owns them.

import { IMAGE_PATH, MAX_BYTES, MIMES, extensionOf } from "./persona-chat-images.mjs";

export const kinds = Object.freeze({
  pendingRequest: "gamecult.persona.chat_pending_request.v1",
  pending: "gamecult.persona.chat_pending.v2",
  pendingRequestV3: "gamecult.persona.chat_pending_request.v2", // the same tuple as v1; asking by this kind is asking for the v3 answer
  pendingV3: "gamecult.persona.chat_pending.v3",
  pendingRequestV4: "gamecult.persona.chat_pending_request.v3", // the same tuple again; served only when the mouth's reply-link flag is on, else an unknown kind
  pendingV4: "gamecult.persona.chat_pending.v4",
  ack: "gamecult.persona.chat_ack.v1",
  read: "gamecult.persona.chat_read.v1",
  sendRequest: "gamecult.persona.chat_send_request.v1",
  sendReceipt: "gamecult.persona.chat_send_receipt.v1",
  sendRequestV2: "gamecult.persona.chat_send_request.v2",
  sendReceiptV2: "gamecult.persona.chat_send_receipt.v2",
  refusal: "gamecult.persona.chat_refusal.v1",
  openDirectRequest: "gamecult.persona.chat_open_direct_request.v1",
  openDirectReceipt: "gamecult.persona.chat_open_direct_receipt.v1",
});

export const refusals = Object.freeze(["Brake", "PersonaMismatch", "UnknownRoom", "EmptyText", "SendFailed", "UnknownPersona"]);

// RoomMoved exists only in the v2 receipt: the v1 tuple, a v1 parse and the typed refusal document do not know it.
export const refusalsV2 = Object.freeze([...refusals, "RoomMoved"]);

// The typed refusal document also answers a request of a kind this mouth does not serve (no Persona is known then).
const refusalDocument = Object.freeze([...refusals, "UnknownKind"]);

// One outcome word per open-direct receipt. room_id and event_id are present exactly for Opened and Existing.
export const directOutcomes = Object.freeze(["Opened", "Existing", "Brake", "PersonaMismatch", "UnknownPersona", "EmptyText", "ForeignUser", "NotAPerson", "NoSuchUser", "InvitePending", "Declined", "DailyLimit", "SendFailed"]);

const maxTextBytes = 4000;
export const maxNewer = 50;
const maxContext = 20;

// Per-delivery bounds, applied where the mouth reads Matrix: the body is bounded like a send,
// a context line and a display name are smaller, and a reply is paged to a byte budget.
export const limits = Object.freeze({ textBytes: maxTextBytes, contextBytes: 1000, displayBytes: 256, mentions: 64, replyBytes: 256 * 1024, tailPerRoom: 20, tailTotal: 60, replyExcerpt: 240 });

// `value` cut to at most `maxBytes` of UTF-8 without splitting a character.
export function clip(value, maxBytes) {
  if (Buffer.byteLength(value, "utf8") <= maxBytes) return value;
  return Buffer.from(value, "utf8").subarray(0, maxBytes).toString("utf8").replace(/�$/, "");
}

export function pendingRequestTuple({ persona, afterSeq }) {
  return [text(persona, "persona"), seq(afterSeq, "after_seq")];
}
export function parsePendingRequest(t) {
  const [persona, afterSeq] = tuple(t, 2, "chat_pending_request");
  return { persona: text(persona, "persona"), afterSeq: seq(afterSeq, "after_seq") };
}

// What the mouth can establish about a delivery from Matrix alone, for the Persona it is delivered to (see docs/bridge.md, "Addressing facts").
// `basis` is why the message is for her, or null when the mouth has no proof (not that it is not for her: her names, her projects' names and
// the sender's role are not the mouth's to know). `form` is the event's kind: only a "message" can address anyone.
export const bases = Object.freeze(["direct", "tag", "reply", "room"]);
export const forms = Object.freeze(["message", "notice", "edit"]);

export function ackTuple({ persona, throughSeq }) {
  return [text(persona, "persona"), seq(throughSeq, "through_seq")];
}
export function parseAck(t) {
  const [persona, throughSeq] = tuple(t, 2, "chat_ack");
  return { persona: text(persona, "persona"), throughSeq: seq(throughSeq, "through_seq") };
}

// Huginn's report that a delivery has gone into the model's input: she has read through this event of this room.
// at_unix_ms is when the model took it in; the mouth does not trust it for anything but the log.
export function readTuple({ persona, roomAlias, eventId, atUnixMs }) {
  return [text(persona, "persona"), text(roomAlias, "room_alias"), text(eventId, "event_id"), seq(atUnixMs, "at_unix_ms")];
}
export function parseRead(t) {
  const [persona, roomAlias, eventId, atUnixMs] = tuple(t, 4, "chat_read");
  return { persona: text(persona, "persona"), roomAlias: text(roomAlias, "room_alias"), eventId: text(eventId, "event_id"), atUnixMs: seq(atUnixMs, "at_unix_ms") };
}

export function sendRequestTuple({ requestId, persona, roomAlias, text: body }) {
  return [text(requestId, "request_id"), text(persona, "persona"), text(roomAlias, "room_alias"), String(body)];
}
export function parseSendRequest(t) {
  const [requestId, persona, roomAlias, body] = tuple(t, 4, "chat_send_request");
  if (typeof body !== "string") throw new Error("chat_send_request text must be a string");
  if (Buffer.byteLength(body, "utf8") > maxTextBytes) throw new Error("chat_send_request text is over the byte bound");
  return { requestId: text(requestId, "request_id"), persona: text(persona, "persona"), roomAlias: text(roomAlias, "room_alias"), text: body };
}

export function sendReceiptTuple({ requestId, eventId = null, refusal = null }) {
  if ((eventId === null) === (refusal === null)) throw new Error("a send receipt carries exactly one of event_id and refusal");
  if (refusal !== null && !refusals.includes(refusal)) throw new Error(`unknown refusal ${refusal}`);
  return [text(requestId, "request_id"), eventId, refusal];
}
export function parseSendReceipt(t) {
  const [requestId, eventId, refusal] = tuple(t, 3, "chat_send_receipt");
  if ((eventId === null) === (refusal === null)) throw new Error("a send receipt carries exactly one of event_id and refusal");
  if (refusal !== null && !refusals.includes(refusal)) throw new Error(`unknown refusal ${String(refusal)}`);
  return { requestId: text(requestId, "request_id"), eventId: eventId === null ? null : text(eventId, "event_id"), refusal };
}

// v2: the same send plus a basis. basis is null (her choice stands: no check) or { seenEventId|null, sinceUnixMs }.
export function sendRequestV2Tuple({ requestId, persona, roomAlias, text: body, basis = null }) {
  return [text(requestId, "request_id"), text(persona, "persona"), text(roomAlias, "room_alias"), String(body),
    basis === null ? null : [basis.seenEventId === null ? null : text(basis.seenEventId, "seen_event_id"), seq(basis.sinceUnixMs, "since_unix_ms")]];
}
export function parseSendRequestV2(t) {
  const [requestId, persona, roomAlias, body, basis] = tuple(t, 5, "chat_send_request_v2");
  if (typeof body !== "string") throw new Error("chat_send_request text must be a string");
  if (Buffer.byteLength(body, "utf8") > maxTextBytes) throw new Error("chat_send_request text is over the byte bound");
  let parsed = null;
  if (basis !== null) {
    const [seen, since] = tuple(basis, 2, "basis");
    parsed = { seenEventId: seen === null ? null : text(seen, "seen_event_id"), sinceUnixMs: seq(since, "since_unix_ms") };
  }
  return { requestId: text(requestId, "request_id"), persona: text(persona, "persona"), roomAlias: text(roomAlias, "room_alias"), text: body, basis: parsed };
}

// `newer` is the ids of the messages that moved the room, oldest first: non-empty exactly when the refusal is RoomMoved.
function checkReceiptV2(eventId, refusal, newer) {
  if ((eventId === null) === (refusal === null)) throw new Error("a send receipt carries exactly one of event_id and refusal");
  if (refusal !== null && !refusalsV2.includes(refusal)) throw new Error(`unknown refusal ${String(refusal)}`);
  if (!Array.isArray(newer) || newer.length > maxNewer || (newer.length > 0) !== (refusal === "RoomMoved")) throw new Error("newer is non-empty exactly when the refusal is RoomMoved, and at most 50 ids");
  return newer.map(id => text(id, "newer"));
}
export function sendReceiptV2Tuple({ requestId, eventId = null, refusal = null, newer = [] }) {
  return [text(requestId, "request_id"), eventId, refusal, checkReceiptV2(eventId, refusal, newer)];
}
export function parseSendReceiptV2(t) {
  const [requestId, eventId, refusal, newer] = tuple(t, 4, "chat_send_receipt_v2");
  return { requestId: text(requestId, "request_id"), eventId: eventId === null ? null : text(eventId, "event_id"), refusal, newer: checkReceiptV2(eventId, refusal, newer) };
}

// A typed refusal for a request that names a Persona the mouth does not serve.
export function refusalTuple({ persona, refusal }) {
  if (!refusalDocument.includes(refusal)) throw new Error(`unknown refusal ${refusal}`);
  return [text(persona, "persona"), refusal];
}
export function parseRefusal(t) {
  const [persona, refusal] = tuple(t, 2, "chat_refusal");
  if (!refusalDocument.includes(refusal)) throw new Error(`unknown refusal ${String(refusal)}`);
  return { persona: text(persona, "persona"), refusal };
}

// A Persona asks her mouth to open a direct room with one person and say a first line there. user_id is a Matrix user id.
export function openDirectRequestTuple({ requestId, persona, userId, text: body }) {
  return [text(requestId, "request_id"), text(persona, "persona"), text(userId, "user_id"), String(body)];
}
export function parseOpenDirectRequest(t) {
  const [requestId, persona, userId, body] = tuple(t, 4, "chat_open_direct_request");
  if (typeof body !== "string") throw new Error("chat_open_direct_request text must be a string");
  if (Buffer.byteLength(body, "utf8") > maxTextBytes) throw new Error("chat_open_direct_request text is over the byte bound");
  return { requestId: text(requestId, "request_id"), persona: text(persona, "persona"), userId: text(userId, "user_id"), text: body };
}

function checkDirectReceipt(outcome, roomId, eventId) {
  if (!directOutcomes.includes(outcome)) throw new Error(`unknown open-direct outcome ${String(outcome)}`);
  const placed = outcome === "Opened" || outcome === "Existing";
  if (placed !== (roomId !== null && eventId !== null) || (!placed && (roomId !== null || eventId !== null))) throw new Error("room_id and event_id are present exactly for Opened and Existing");
}
export function openDirectReceiptTuple({ requestId, outcome, roomId = null, eventId = null }) {
  checkDirectReceipt(outcome, roomId, eventId);
  return [text(requestId, "request_id"), outcome, roomId === null ? null : text(roomId, "room_id"), eventId === null ? null : text(eventId, "event_id")];
}
export function parseOpenDirectReceipt(t) {
  const [requestId, outcome, roomId, eventId] = tuple(t, 4, "chat_open_direct_receipt");
  checkDirectReceipt(outcome, roomId, eventId);
  return { requestId: text(requestId, "request_id"), outcome, roomId: roomId === null ? null : text(roomId, "room_id"), eventId: eventId === null ? null : text(eventId, "event_id") };
}

export function deliveryTuple(d) {
  return [
    seq(d.seq, "seq"), text(d.roomAlias, "room_alias"), text(d.roomId, "room_id"), matrixId("$", d.eventId, "event_id"),
    matrixId("@", d.sender, "sender"), text(d.senderDisplay, "sender_display"), String(d.text), seq(d.atUnixMs, "at_unix_ms"),
    d.context.slice(-maxContext).map(c => [matrixId("@", c.sender, "sender"), String(c.text), seq(c.atUnixMs, "at_unix_ms")]),
    d.mentions.map(id => matrixId("@", id, "mention")), flag(d.isDirect, "is_direct"), imageTuple(d.image ?? null),
  ];
}

// The image the mouth stored for a delivery, by reference: [path, mime, size, sha256] or null. The path is relative to the
// Persona's image store and is exactly `<hex sha256 of the room id>/<hex sha256 of the bytes>[.<ext of the mime>]`, so it can
// be neither absolute nor climb out of the store. Whether the file matches the rest is the reader's to re-check.
function imageTuple(image) {
  if (image === null) return null;
  const { path, mime, size, sha256 } = image;
  if (typeof path !== "string" || !IMAGE_PATH.test(path)) throw new Error("image path must be <room hash>/<bytes hash>[.ext]");
  if (!MIMES.includes(mime)) throw new Error("image mime must be image/png, image/jpeg, image/gif or image/webp");
  if (path.includes(".") && !path.endsWith(`.${extensionOf(mime)}`)) throw new Error("image path extension must match its mime");
  if (!Number.isSafeInteger(size) || size < 1 || size > MAX_BYTES) throw new Error("image size must be 1 to 5 MiB");
  if (typeof sha256 !== "string" || !/^[0-9a-f]{64}$/.test(sha256)) throw new Error("image sha256 must be lowercase hex");
  if (!path.split("/")[1].startsWith(sha256)) throw new Error("image path must name its sha256");
  return [path, mime, size, sha256];
}
function parseImage(t) {
  const [path, mime, size, sha256] = tuple(t, 4, "image");
  imageTuple({ path, mime, size, sha256 });
  return { path, mime, size, sha256 };
}
// The v3 delivery: the 12 fields above, then basis and form.
export function deliveryV3Tuple(d) {
  return [...deliveryTuple(d), basis(d.basis ?? null), form(d.form ?? "message")];
}
export function parseDeliveryV3(t) {
  const fields = tuple(t, 14, "delivery_v3");
  return { ...parseDelivery(fields.slice(0, 12)), basis: basis(fields[12]), form: form(fields[13]) };
}
function basis(v) {
  if (v !== null && !bases.includes(v)) throw new Error(`unknown delivery basis ${String(v)}`);
  return v;
}
function form(v) {
  if (!forms.includes(v)) throw new Error(`unknown delivery form ${String(v)}`);
  return v;
}

// The v4 delivery: the 14 fields of v3, then reply_to: nil, or [event_id, sender, excerpt] for the event this one replies to.
// sender is the quoted author's Matrix id. excerpt is nil for a quoted event whose text must not travel (redacted, not text);
// otherwise its first replyExcerpt (240) Unicode code points. A quote the mouth could not establish is reply_to nil, never a guess.
export function deliveryV4Tuple(d) {
  return [...deliveryV3Tuple(d), replyTo(d.replyTo ?? null)];
}
export function parseDeliveryV4(t) {
  const fields = tuple(t, 15, "delivery_v4");
  return { ...parseDeliveryV3(fields.slice(0, 14)), replyTo: parseReplyTo(fields[14]) };
}
// A Matrix id as the tuple carries it: its sigil, a non-empty rest, at most 255 bytes, no Unicode whitespace, control, format or private-use characters and no unpaired surrogates.
// The mouth applies the same test to what a homeserver returned, so a hostile id is no reply_to rather than a skipped delivery.
export const isMatrixId = (sigil, v) => typeof v === "string" && Buffer.byteLength(v) <= 255 && v.length > 1 && v[0] === sigil && !/[\p{White_Space}\p{Cc}\p{Cf}\p{Co}\p{Default_Ignorable_Code_Point}\uD800-\uDFFF]/u.test(v);
function replyTo(r) {
  if (r === null) return null;
  const { eventId, sender, excerpt = null } = r;
  if (!isMatrixId("$", eventId)) throw new Error("reply_to event_id must be an event id ($..., at most 255 bytes)");
  if (!isMatrixId("@", sender)) throw new Error("reply_to sender must be a user id (@..., at most 255 bytes)");
  if (excerpt !== null && (typeof excerpt !== "string" || [...excerpt].length > limits.replyExcerpt)) throw new Error(`reply_to excerpt must be a string of at most ${limits.replyExcerpt} code points, or nil`);
  return [text(eventId, "reply_to event_id"), text(sender, "reply_to sender"), excerpt];
}
function parseReplyTo(t) {
  if (t === null) return null;
  const [eventId, sender, excerpt] = tuple(t, 3, "reply_to");
  return { eventId: text(eventId, "reply_to event_id"), sender: text(sender, "reply_to sender"), excerpt: replyTo({ eventId, sender, excerpt })[2] };
}

export function parseDelivery(t) {
  const [s, roomAlias, roomId, eventId, sender, senderDisplay, body, atUnixMs, context, mentions, isDirect, image] = tuple(t, 12, "delivery");
  if (!Array.isArray(context)) throw new Error("delivery context must be a list");
  if (!Array.isArray(mentions)) throw new Error("delivery mentions must be a list");
  return {
    seq: seq(s, "seq"), roomAlias: text(roomAlias, "room_alias"), roomId: text(roomId, "room_id"), eventId: matrixId("$", eventId, "event_id"),
    sender: matrixId("@", sender, "sender"), senderDisplay: text(senderDisplay, "sender_display"), text: String(body), atUnixMs: seq(atUnixMs, "at_unix_ms"),
    context: context.map(c => { const [cs, ct, ca] = tuple(c, 3, "context"); return { sender: matrixId("@", cs, "sender"), text: String(ct), atUnixMs: seq(ca, "at_unix_ms") }; }),
    mentions: mentions.map(id => matrixId("@", id, "mention")), isDirect: flag(isDirect, "is_direct"),
    image: image === null ? null : parseImage(image),
  };
}

export function pendingTuple({ persona, deliveries }) {
  return [text(persona, "persona"), deliveries.map(deliveryTuple)];
}
export function parsePending(t) {
  const [persona, deliveries] = tuple(t, 2, "chat_pending");
  if (!Array.isArray(deliveries)) throw new Error("chat_pending deliveries must be a list");
  return { persona: text(persona, "persona"), deliveries: deliveries.map(parseDelivery) };
}

// One line of a room's recent talk, newest-unacked: room alias (or room id for a DM), event id, origin time, sender, form, basis, body.
// Data for the reader, never an instruction: no delivery, no wake, no ack position hangs on it.
export function tailLineTuple(l) {
  return [text(l.roomAlias, "room"), matrixId("$", l.eventId, "event_id"), seq(l.atUnixMs, "at_unix_ms"), matrixId("@", l.sender, "sender"), form(l.form), basis(l.basis ?? null), String(l.text)];
}
function parseTailLine(t) {
  const [roomAlias, eventId, atUnixMs, sender, f, b, body] = tuple(t, 7, "tail_line");
  return { roomAlias: text(roomAlias, "room"), eventId: matrixId("$", eventId, "event_id"), atUnixMs: seq(atUnixMs, "at_unix_ms"), sender: matrixId("@", sender, "sender"), form: form(f), basis: basis(b), text: String(body) };
}

// The v3 answer: the persona, the deliveries, the tail (at most tailPerRoom lines a room, tailTotal in all, oldest first), then
// tail_gaps: the rooms (alias, or room id for a DM) whose lines are not all in the tail, each once. Absent or empty: every room was read fully.
export function pendingV3Tuple({ persona, deliveries, tail = [], tailGaps = [] }) {
  if (tail.length > limits.tailTotal) throw new Error("chat_pending tail is over the line bound");
  return [text(persona, "persona"), deliveries.map(deliveryV3Tuple), tail.map(tailLineTuple), tailGaps.map(room => text(room, "tail_gap"))];
}
export function parsePendingV3(t) {
  const [persona, deliveries, tail, tailGaps] = tuple(t, 4, "chat_pending_v3");
  if (!Array.isArray(deliveries)) throw new Error("chat_pending deliveries must be a list");
  if (!Array.isArray(tail)) throw new Error("chat_pending tail must be a list");
  if (!Array.isArray(tailGaps)) throw new Error("chat_pending tail_gaps must be a list");
  const gaps = tailGaps.map(room => text(room, "tail_gap"));
  if (new Set(gaps).size !== gaps.length) throw new Error("chat_pending tail_gaps repeats a room");
  return { persona: text(persona, "persona"), deliveries: deliveries.map(parseDeliveryV3), tail: tail.map(parseTailLine), tailGaps: gaps };
}

// The v4 answer: the v3 answer with v4 deliveries.
export function pendingV4Tuple({ persona, deliveries, tail = [], tailGaps = [] }) {
  const [p, , t, g] = pendingV3Tuple({ persona, deliveries: [], tail, tailGaps });
  return [p, deliveries.map(deliveryV4Tuple), t, g];
}
export function parsePendingV4(t) {
  const [persona, deliveries, tail, tailGaps] = tuple(t, 4, "chat_pending_v4");
  if (!Array.isArray(deliveries)) throw new Error("chat_pending deliveries must be a list");
  const rest = parsePendingV3([persona, [], tail, tailGaps]);
  return { ...rest, deliveries: deliveries.map(parseDeliveryV4) };
}

// The CultNet document_put_raw envelope every chat_* document travels in.
export function envelope({ kind, recordKey, payload, sourceRuntimeId, sourceAgentId, sourceRole }) {
  return {
    schemaVersion: "cultnet.document_put_raw.v0",
    messageId: `${kind}:${recordKey}`,
    document: {
      schemaId: kind, recordKey, storedAt: new Date().toISOString(), payloadEncoding: "messagepack", payload,
      sourceRuntimeId, sourceAgentId, sourceRole, tags: ["cultnet.transport.rudp.v0"],
    },
  };
}

// allowedKinds null: any kind passes (the caller answers a kind it does not serve).
export function checkEnvelope(message, allowedKinds) {
  const d = message?.document;
  if (message?.schemaVersion !== "cultnet.document_put_raw.v0" || !d) throw new Error("not a document_put_raw message");
  if (allowedKinds !== null && !allowedKinds.includes(d.schemaId)) throw new Error(`document kind ${String(d.schemaId)} is not accepted here`);
  if (d.payloadEncoding !== "messagepack") throw new Error("chat documents are MessagePack");
  if (!Array.isArray(d.tags) || d.tags.length !== 1 || d.tags[0] !== "cultnet.transport.rudp.v0") throw new Error("chat document tags substituted authority");
  return d;
}

function tuple(t, n, what) {
  if (!Array.isArray(t) || t.length !== n) throw new Error(`${what} must be a ${n}-field tuple`);
  return t;
}
function matrixId(sigil, v, what) {
  if (!isMatrixId(sigil, v)) throw new Error(`${what} must be a Matrix id (${sigil}..., at most 255 bytes, no whitespace or control characters)`);
  return v;
}
function text(v, what) {
  if (typeof v !== "string" || v === "") throw new Error(`${what} must be a non-empty string`);
  return v;
}
function flag(v, what) {
  if (typeof v !== "boolean") throw new Error(`${what} must be a boolean`);
  return v;
}
function seq(v, what) {
  if (!Number.isSafeInteger(v) || v < 0) throw new Error(`${what} must be a non-negative integer`);
  return v;
}

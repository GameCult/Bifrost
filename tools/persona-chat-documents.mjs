// gamecult.persona.chat_*.v1: the documents Huginn and the Matrix mouth exchange.
// Each kind is a positional tuple (the CultCache payload shape); this module owns
// the field order, the parsers and the envelope check. It does no I/O and loads no
// runtime, so the wire encoder (MessagePack) stays with the caller.

export const kinds = Object.freeze({
  pendingRequest: "gamecult.persona.chat_pending_request.v1",
  pending: "gamecult.persona.chat_pending.v1",
  ack: "gamecult.persona.chat_ack.v1",
  sendRequest: "gamecult.persona.chat_send_request.v1",
  sendReceipt: "gamecult.persona.chat_send_receipt.v1",
});

export const refusals = Object.freeze(["Brake", "PersonaMismatch", "UnknownRoom", "EmptyText", "SendFailed"]);

const maxTextBytes = 4000;
const maxContext = 20;

export function pendingRequestTuple({ persona, afterSeq }) {
  return [text(persona, "persona"), seq(afterSeq, "after_seq")];
}
export function parsePendingRequest(t) {
  const [persona, afterSeq] = tuple(t, 2, "chat_pending_request");
  return { persona: text(persona, "persona"), afterSeq: seq(afterSeq, "after_seq") };
}

export function ackTuple({ persona, throughSeq }) {
  return [text(persona, "persona"), seq(throughSeq, "through_seq")];
}
export function parseAck(t) {
  const [persona, throughSeq] = tuple(t, 2, "chat_ack");
  return { persona: text(persona, "persona"), throughSeq: seq(throughSeq, "through_seq") };
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
  return { requestId: text(requestId, "request_id"), eventId: eventId === null ? null : text(eventId, "event_id"), refusal };
}

export function deliveryTuple(d) {
  return [
    seq(d.seq, "seq"), text(d.roomAlias, "room_alias"), text(d.roomId, "room_id"), text(d.eventId, "event_id"),
    text(d.sender, "sender"), text(d.senderDisplay, "sender_display"), String(d.text), seq(d.atUnixMs, "at_unix_ms"),
    d.context.slice(-maxContext).map(c => [text(c.sender, "sender"), String(c.text), seq(c.atUnixMs, "at_unix_ms")]),
  ];
}
export function parseDelivery(t) {
  const [s, roomAlias, roomId, eventId, sender, senderDisplay, body, atUnixMs, context] = tuple(t, 9, "delivery");
  if (!Array.isArray(context)) throw new Error("delivery context must be a list");
  return {
    seq: seq(s, "seq"), roomAlias: text(roomAlias, "room_alias"), roomId: text(roomId, "room_id"), eventId: text(eventId, "event_id"),
    sender: text(sender, "sender"), senderDisplay: text(senderDisplay, "sender_display"), text: String(body), atUnixMs: seq(atUnixMs, "at_unix_ms"),
    context: context.map(c => { const [cs, ct, ca] = tuple(c, 3, "context"); return { sender: text(cs, "sender"), text: String(ct), atUnixMs: seq(ca, "at_unix_ms") }; }),
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

export function checkEnvelope(message, allowedKinds) {
  const d = message?.document;
  if (message?.schemaVersion !== "cultnet.document_put_raw.v0" || !d) throw new Error("not a document_put_raw message");
  if (!allowedKinds.includes(d.schemaId)) throw new Error(`document kind ${String(d.schemaId)} is not accepted here`);
  if (d.payloadEncoding !== "messagepack") throw new Error("chat documents are MessagePack");
  if (!Array.isArray(d.tags) || d.tags.length !== 1 || d.tags[0] !== "cultnet.transport.rudp.v0") throw new Error("chat document tags substituted authority");
  return d;
}

function tuple(t, n, what) {
  if (!Array.isArray(t) || t.length !== n) throw new Error(`${what} must be a ${n}-field tuple`);
  return t;
}
function text(v, what) {
  if (typeof v !== "string" || v === "") throw new Error(`${what} must be a non-empty string`);
  return v;
}
function seq(v, what) {
  if (!Number.isSafeInteger(v) || v < 0) throw new Error(`${what} must be a non-negative integer`);
  return v;
}

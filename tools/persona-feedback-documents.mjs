export const feedbackEventType = "voidbot.discord.persona_feedback_event";
export const feedbackEventSchema = "voidbot.discord.persona_feedback_event.v0";
export const feedbackBindingType = "bifrost.persona_feedback.target_binding";
export const feedbackBindingSchema = "bifrost.persona_feedback.target_binding.v0";
export const actorLinkType = "bifrost.discord.actor_link";
export const actorLinkSchema = "bifrost.discord.actor_link.v0";
export const feedbackReceiptType = "bifrost.persona_feedback.admission_receipt";
export const feedbackReceiptSchema = "bifrost.persona_feedback.admission_receipt.v0";
export const feedbackDeliveryType = "bifrost.persona_feedback.delivery";
export const feedbackDeliverySchema = "bifrost.persona_feedback.delivery.v0";

// Kept as explicit constructors because CultCache document metadata is part of the wire contract.
export function buildFeedbackDefinitions(defineDocumentType) {
  const make = (type, schemaId, name, parse) => defineDocumentType({ type, schemaName: type, schemaId, schemaVersion: schemaId, contentHash: schemaId, global: false, name, schema: { parse } });
  return {
    event: make(feedbackEventType, feedbackEventSchema, "eventId", parseEvent),
    binding: make(feedbackBindingType, feedbackBindingSchema, "bindingId", parseBinding),
    actorLink: make(actorLinkType, actorLinkSchema, "linkId", parseActorLink),
    receipt: make(feedbackReceiptType, feedbackReceiptSchema, "receiptId", parseReceipt),
    delivery: make(feedbackDeliveryType, feedbackDeliverySchema, "admissionId", parseDelivery),
  };
}

export function parseEvent(value) {
  const v = record(value, "feedback event");
  exact(v.schemaVersion, feedbackEventSchema, "event schema");
  for (const key of ["eventId","guildId","channelId","messageId","authorId","addressingMode","targetPersonaId","targetRepoName","targetRuntimeId","content","payloadHash","producerId","producerRuntimeId"]) text(v[key], key);
  exact(v.producerId, "voidbot", "producer"); exact(v.authorityClass, "feedback_only", "authorityClass");
  exact(v.status, "pending", "status");
  if(!["role","text","reply","broadcast"].includes(v.addressingMode))throw new Error("addressingMode is invalid.");
  if(!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(v.observedAt)||Number.isNaN(Date.parse(v.observedAt)))throw new Error("observedAt must be RFC3339.");
  if (!/^[a-f0-9]{64}$/.test(v.payloadHash)) throw new Error("payloadHash must be sha256 hex.");
  return v;
}
export function parseBinding(value) { const v = record(value,"target binding"); exact(v.schemaVersion,feedbackBindingSchema,"binding schema"); for (const k of ["bindingId","guildId","channelId","targetPersonaId","targetRepoName","targetRuntimeId","producerId","producerRuntimeId","sourceVisibility","dataClassification","deliveryStorePath"]) text(v[k],k); const expected={public:"public_feedback",organization:"organization_feedback",private:"private_feedback"}[v.sourceVisibility];if(!expected||v.dataClassification!==expected)throw new Error("sourceVisibility and dataClassification must be the exact admitted pair."); return v; }
export function parseActorLink(value) { const v=record(value,"actor link"); exact(v.schemaVersion,actorLinkSchema,"actor link schema"); for(const k of ["linkId","guildId","discordAuthorId","bifrostActorId","heimdallActorRef"]) text(v[k],k); return v; }
export function parseReceipt(value) { const v=record(value,"receipt"); exact(v.schemaVersion,feedbackReceiptSchema,"receipt schema"); for(const k of ["receiptId","eventId","status","classification","payloadHash","bindingId","recordedAt"]) text(v[k],k); return v; }
export function parseDelivery(value) { const v=record(value,"delivery"); exact(v.schemaVersion,feedbackDeliverySchema,"delivery schema"); for(const k of ["admissionId","packetSha256","sourceObserverId","sourceObserverRuntimeId","provider","bifrostAdmissionReceiptId","authority","providerIdentityId"]) text(v[k],k); exact(v.sourceObserverId,"voidbot","sourceObserverId"); exact(v.provider,"bifrost","provider"); exact(v.authority,"feedback_only","authority"); if(!(v.providerSignature instanceof Uint8Array)||v.providerSignature.length!==64)throw new Error("providerSignature must be 64 bytes."); const p=record(v.packet,"packet"); for(const k of ["feedbackId","sourceEventId","sourceActorId","actorClassification","discordGuildId","discordChannelId","discordMessageId","targetRuntimeId","targetRepository","targetPersonaId","sourceRoomId","feedbackText","contentSha256","sourceVisibility","dataClassification"])text(p[k],k);if(!Array.isArray(p.sourceDiscussionRefs)||p.sourceDiscussionRefs.length===0)throw new Error("sourceDiscussionRefs are required.");if(p.privateStateIncluded!==false)throw new Error("privateStateIncluded describes embedded private machine state and must be false.");return v; }
function record(value,label){if(!value||typeof value!=="object"||Array.isArray(value))throw new Error(`${label} must be an object.`);return value;}
function text(value,label){if(typeof value!=="string"||!value.trim())throw new Error(`${label} is required.`);return value;}
function exact(value,expected,label){if(value!==expected)throw new Error(`${label} must be ${expected}.`);}

export const requestType = "epiphany.persona_discord_delivery_request";
export const requestSchema = "epiphany.persona_discord_delivery_request.v0";
export const requestSigningPurpose = "epiphany.persona-discord.delivery-request.v0";
export const requestIdentityDomain = "epiphany.persona-discord-delivery-request.identity.v0\0";
export const requestSignatureDomain = "epiphany.persona-discord-delivery-request.signature.v0\0";
export const receiptType = "bifrost.persona_discord_delivery_receipt";
export const receiptSchema = "bifrost.persona_discord_delivery_receipt.v0";
export const receiptSigningPurpose = "bifrost.persona-discord.delivery-receipt.v0";
export const receiptIdentityDomain = "bifrost.persona-discord-delivery-receipt.identity.v0\0";
export const receiptSignatureDomain = "bifrost.persona-discord-delivery-receipt.signature.v0\0";
export const executionType = "bifrost.persona_discord_delivery_execution";
export const executionSchema = "bifrost.persona_discord_delivery_execution.v0";

export function buildPersonaDiscordDeliveryDefinitions(defineDocumentType) {
  const make=(type,schema,name,parse,members)=>defineDocumentType({type,schemaName:type,schemaId:schema,schemaVersion:schema,contentHash:schema,global:false,name,schema:{parse},members});
  return {
    request:make(requestType,requestSchema,"requestId",parseRequest,requestMembers),
    receipt:make(receiptType,receiptSchema,"receiptId",parseReceipt,receiptMembers),
    execution:make(executionType,executionSchema,"requestId",parseExecution),
  };
}

const requestMembers = ["schemaVersion","requestId","effectDocumentId","targetRuntimeId","personaAgentId","channelId","replyToMessageId","content","contentSha256","issuedAt","expiresAt","privateStateExposed","signerIdentityId","signerSignature"].map((memberName,slot)=>({slot,memberName,typeName:memberName==="privateStateExposed"?"boolean":memberName==="signerSignature"?"byte[]":"string",isName:memberName==="requestId"}));
const receiptMembers = ["schemaVersion","receiptId","requestId","requestPayloadSha256","status","channelId","replyToMessageId","messageId","transport","crossingReceiptId","receiptUrl","completedAt","providerIdentityId","privateStateExposed","providerSignature"].map((memberName,slot)=>({slot,memberName,typeName:memberName==="privateStateExposed"?"boolean":memberName==="providerSignature"?"byte[]":"string",isName:memberName==="receiptId"}));

export function requestSigningTuple(v){return [v.schemaVersion,v.requestId,v.effectDocumentId,v.targetRuntimeId,v.personaAgentId,v.channelId,v.replyToMessageId,v.content,v.contentSha256,v.issuedAt,v.expiresAt,v.privateStateExposed,v.signerIdentityId];}
export function receiptSigningTuple(v){return [v.schemaVersion,v.receiptId,v.requestId,v.requestPayloadSha256,v.status,v.channelId,v.replyToMessageId,v.messageId,v.transport,v.crossingReceiptId,v.receiptUrl,v.completedAt,v.providerIdentityId,v.privateStateExposed];}
export function receiptDocumentTuple(v){return [...receiptSigningTuple(v),v.providerSignature];}

export function parseRequest(value){const v=typed(value,requestMembers,"Persona Discord delivery request");closed(v,["schemaName","schemaVersion","requestId","effectDocumentId","targetRuntimeId","personaAgentId","channelId","replyToMessageId","content","contentSha256","issuedAt","expiresAt","privateStateExposed","signerIdentityId","signerSignature"],"request");exact(v.schemaVersion,requestSchema,"request schema");for(const key of ["requestId","effectDocumentId","targetRuntimeId","personaAgentId","channelId","contentSha256","issuedAt","expiresAt","signerIdentityId"])text(v[key],key);content(v.content);optionalText(v.replyToMessageId,"replyToMessageId");if(v.privateStateExposed!==false)throw new Error("request privateStateExposed must be false.");bytes64(v.signerSignature,"signerSignature");sha(v.contentSha256,"contentSha256");const issued=date(v.issuedAt,"issuedAt"),expires=date(v.expiresAt,"expiresAt");if(expires<=issued||expires-issued>120000)throw new Error("request lifetime must be positive and at most 120 seconds.");return v;}
export function parseReceipt(value){const v=typed(value,receiptMembers,"Persona Discord delivery receipt");exact(v.schemaVersion,receiptSchema,"receipt schema");for(const key of ["receiptId","requestId","requestPayloadSha256","status","channelId","completedAt","providerIdentityId"])text(v[key],key);for(const key of ["replyToMessageId","messageId","transport","crossingReceiptId","receiptUrl"])optionalText(v[key],key);if(v.receiptId!==v.requestId)throw new Error("receiptId must equal requestId.");if(!["completed","failed","unknown"].includes(v.status))throw new Error("receipt status is invalid.");if(v.status==="completed"&&[v.messageId,v.transport,v.crossingReceiptId,v.receiptUrl].some(x=>!x))throw new Error("completed receipt lacks exact delivery evidence.");if(v.status!=="completed"&&[v.messageId,v.transport,v.crossingReceiptId,v.receiptUrl].some(Boolean))throw new Error("non-completed receipt may not claim delivery evidence.");if(v.privateStateExposed!==false)throw new Error("receipt privateStateExposed must be false.");bytes64(v.providerSignature,"providerSignature");sha(v.requestPayloadSha256,"requestPayloadSha256");date(v.completedAt,"completedAt");return v;}
export function parseExecution(value){const v=record(value,"Persona Discord execution journal");exact(v.schemaVersion,executionSchema,"execution schema");for(const key of ["requestId","requestPayloadSha256","status","recordedAt"])text(v[key],key);if(!["permit_pending","running","completed","failed","unknown"].includes(v.status))throw new Error("execution status is invalid.");if(v.status==="permit_pending"){text(v.permitNonce,"permitNonce");date(v.permitIssuedAt,"permitIssuedAt");date(v.permitExpiresAt,"permitExpiresAt");if(Date.parse(v.permitExpiresAt)<=Date.parse(v.permitIssuedAt)||Date.parse(v.permitExpiresAt)-Date.parse(v.permitIssuedAt)>5000)throw new Error("permit intent lifetime is invalid");}return v;}
function record(v,label){if(!v||typeof v!=="object"||Array.isArray(v))throw new Error(`${label} must be an object.`);return v;}
function typed(v,members,label){if(Array.isArray(v)){if(v.length!==members.length)throw new Error(`${label} tuple has the wrong arity.`);return Object.fromEntries(members.map(member=>[member.memberName,member.typeName==="byte[]"&&Array.isArray(v[member.slot])?Uint8Array.from(v[member.slot]):v[member.slot]]));}return record(v,label);}
function text(v,label){bounded(v,label,256);}
function bounded(v,label,maxBytes){if(typeof v!=="string"||!v.trim()||Buffer.byteLength(v,"utf8")>maxBytes||/\p{Cc}/u.test(v))throw new Error(`${label} is empty, oversized, or contains control characters.`);}
function content(v){if(typeof v!=="string"||!v.trim()||Buffer.byteLength(v,"utf8")>1900||v.includes("\0"))throw new Error("content is empty, exceeds 1900 UTF-8 bytes, or contains NUL.");}
function optionalText(v,label){if(typeof v!=="string")throw new Error(`${label} must be a string.`);}
function exact(v,w,label){if(v!==w)throw new Error(`${label} must be ${w}.`);}
function bytes64(v,label){if(!(v instanceof Uint8Array)||v.length!==64)throw new Error(`${label} must be 64 bytes.`);}
function sha(v,label){if(!/^sha256-[0-9a-f]{64}$/.test(v))throw new Error(`${label} must be lowercase sha256.`);}
function date(v,label){const parsed=Date.parse(v);if(Number.isNaN(parsed))throw new Error(`${label} must be RFC3339.`);return parsed;}
function closed(v,keys,label){const allowed=new Set(keys);for(const key of Object.keys(v))if(!allowed.has(key))throw new Error(`${label} contains unknown field ${key}.`);}

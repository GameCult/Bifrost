import { createHash } from "node:crypto";
import { resolve } from "node:path";
import { admissionTuple, operatorAdmissionSchema, parseOperatorAdmission, parseOperatorRequest, parseOperatorResult, packetTuple } from "./epiphany-operator-command-documents.mjs";
import { enrollSignedIdentity, loadSignedIdentity, signPurpose, trustAnchor } from "./bifrost-signed-identity.mjs";

export const OPERATOR_SIGNING_PURPOSE="bifrost.operator-command.delivery.v0";
export const defaultOperatorIdentityPath=()=>process.env.BIFROST_EPIPHANY_OPERATOR_PRIVATE_KEY??(process.platform==="win32"?resolve(".bifrost","private","epiphany-operator-ed25519.seed"):"/var/lib/gamecult/bifrost/epiphany-operator/operator-ed25519.seed");

// The transport is deliberately a narrow port. The live CultNet RUDP request/reply
// adapter is not yet present in Bifrost; callers must inject an authenticated port.
export async function admitOperatorRequest({request,ownerDiscordId,actorLink,identity,encode,transport,expectedEpiphanyIdentityId,now=new Date(),ledger=new ImmutableOperatorLedger()}){
  parseOperatorRequest(request);
  if(request.sourceActorDiscordId!==ownerDiscordId)throw new Error("Epiphany operator command requires the exact configured Discord owner.");
  if(!actorLink||actorLink.guildId!==request.discordGuildId||actorLink.discordAuthorId!==request.sourceActorDiscordId||!actorLink.bifrostActorId?.trim()||!actorLink.heimdallActorRef?.trim())throw new Error("Epiphany operator command requires the exact existing Bifrost actor link and Heimdall actor reference.");
  const at=now instanceof Date?now:new Date(now);if(!Number.isFinite(at.getTime())||Date.parse(request.issuedAt)>at.getTime()||Date.parse(request.expiresAt)<at.getTime())throw new Error("Epiphany operator request is not currently valid.");
  const packet={commandId:request.commandId,nonce:request.nonce,sourceEventId:request.sourceEventId,sourceActorId:actorLink.bifrostActorId,discordGuildId:request.discordGuildId,discordChannelId:request.discordChannelId,discordMessageId:request.discordMessageId,targetRuntimeId:request.targetRuntimeId,issuedAt:request.issuedAt,expiresAt:request.expiresAt,command:request.command};
  const packetSha256=`sha256-${createHash("sha256").update(encode(packetTuple(packet))).digest("hex")}`;
  const admission={schemaName:"bifrost.operator_command.delivery",schemaVersion:operatorAdmissionSchema,admissionId:request.requestId,packet,packetSha256,sourceObserverId:"voidbot",sourceObserverRuntimeId:request.producerRuntimeId,provider:"bifrost",bifrostAdmissionReceiptId:request.requestId,authority:"exact_operator_command_only",providerIdentityId:identity.identityId,providerSignature:new Uint8Array()};
  admission.providerSignature=new Uint8Array(signPurpose(identity,OPERATOR_SIGNING_PURPOSE,encode(admissionTuple(admission))));parseOperatorAdmission(admission);
  ledger.admit(request.requestId,request,admission);
  if(!transport||typeof transport.execute!=="function")throw new Error("No Epiphany operator transport port is configured; the live CultNet RUDP adapter is missing.");
  const envelope=await transport.execute(admission);
  if(!envelope||envelope.providerIdentityId!==expectedEpiphanyIdentityId)throw new Error("Operator result did not cross the configured authenticated Epiphany transport boundary.");
  const result=parseOperatorResult(envelope.result);
  if(result.commandId!==packet.commandId||result.packetSha256!==packetSha256||result.targetRuntimeId!==packet.targetRuntimeId)throw new Error("Epiphany operator result is not bound to the admitted command.");
  ledger.complete(request.requestId,result,envelope.providerIdentityId);
  return {admission,result,resultProviderIdentityId:envelope.providerIdentityId};
}

export class ImmutableOperatorLedger{#rows=new Map();admit(id,request,admission){const value={request,admission};const old=this.#rows.get(id);if(old&&stable(old)!==stable(value))throw new Error("operator request identity collision");if(!old)this.#rows.set(id,value);}complete(id,result,providerIdentityId){const row=this.#rows.get(id);if(!row)throw new Error("operator result has no admitted request");const next={...row,result,providerIdentityId};if(row.result&&stable(row)!==stable(next))throw new Error("operator result identity collision");this.#rows.set(id,next);}get(id){return this.#rows.get(id);}}
export async function enrollOperatorIdentity(path=defaultOperatorIdentityPath()){return enrollSignedIdentity(path,"Bifrost Epiphany operator identity");}
export async function loadOperatorIdentity(path=defaultOperatorIdentityPath()){return loadSignedIdentity(path,"Bifrost Epiphany operator identity");}
export {trustAnchor};
function stable(v){return JSON.stringify(v,(_k,x)=>x instanceof Uint8Array?Buffer.from(x).toString("hex"):x);}

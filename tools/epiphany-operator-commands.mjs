import { createHash } from "node:crypto";
import { resolve } from "node:path";
import { admissionTuple,operatorAdmissionSchema,parseOperatorAdmission,parseOperatorRequest,parseOperatorResult,parseOperatorResultReceipt,packetTuple } from "./epiphany-operator-command-documents.mjs";
import { enrollSignedIdentity,loadSignedIdentity,signPurpose,trustAnchor } from "./bifrost-signed-identity.mjs";

export const OPERATOR_SIGNING_PURPOSE="bifrost.operator-command.delivery.v1";
export const defaultOperatorIdentityPath=()=>process.env.BIFROST_EPIPHANY_OPERATOR_PRIVATE_KEY??(process.platform==="win32"?resolve(".bifrost","private","epiphany-operator-ed25519.seed"):"/var/lib/gamecult/bifrost/epiphany-operator/operator-ed25519.seed");

export class OperatorCommandPermanentRefusal extends Error { constructor(code,message){super(message);this.name="OperatorCommandPermanentRefusal";this.code=code;this.retryable=false;} }

export async function admitOperatorRequest({request,ownerDiscordId,actorLink,identity,encode,transport,now=new Date(),ledger}){
  requireDurableLedger(ledger);
  try{parseOperatorRequest(request);}catch(error){throw new OperatorCommandPermanentRefusal("invalid-request",error instanceof Error?error.message:String(error));}
  const existingRequest=await ledger.getRequest(request.requestId),existingAdmission=await ledger.getAdmission(request.requestId),existingCompletion=await ledger.getCompletion(request.commandId);
  if(Boolean(existingRequest)!==Boolean(existingAdmission))throw new Error("operator durable request/admission pair is incomplete");
  requireAuthenticatedTransport(transport);
  if(existingAdmission){
    await compareAdmission(ledger,request,existingAdmission);
    if(existingCompletion){
      parseOperatorResultReceipt(existingCompletion,{allowLegacy:true});
      if(existingCompletion.commandId!==existingAdmission.packet.commandId||existingCompletion.packetSha256!==existingAdmission.packetSha256||existingCompletion.targetRuntimeId!==existingAdmission.packet.targetRuntimeId)refuse("command-id-collision","operator completion is not bound to this exact durable admission");
      parseOperatorAdmission(existingAdmission,{allowLegacy:true});transport.verifyReceipt(existingAdmission,existingCompletion);
      return {admission:existingAdmission,result:existingCompletion.result,receipt:existingCompletion,resultProviderIdentityId:existingCompletion.providerIdentityId,recovered:true};
    }
    return executeAdmission({request,admission:existingAdmission,transport,ledger,recovered:true});
  }
  if(existingCompletion)refuse("command-id-collision","operator command id already belongs to a different durable admission");
  let admission;
  try{
    if(request.sourceActorDiscordId!==ownerDiscordId)refuse("foreign-owner","Epiphany operator command requires the exact configured Discord owner.");
    // This persisted Bifrost actor-link is the admitted mapping authority. Its
    // Heimdall reference is provenance only until a signed live claim exists.
    if(!actorLink||actorLink.guildId!==request.discordGuildId||actorLink.discordAuthorId!==request.sourceActorDiscordId||!actorLink.bifrostActorId?.trim())refuse("missing-actor-link","Epiphany operator command requires the exact admitted Bifrost actor link.");
    const at=now instanceof Date?now:new Date(now);if(!Number.isFinite(at.getTime())||Date.parse(request.issuedAt)>at.getTime()||Date.parse(request.expiresAt)<at.getTime())refuse("expired-new-request","A new Epiphany operator request is not currently valid.");
    const packet={commandId:request.commandId,nonce:request.nonce,sourceEventId:request.sourceEventId,sourceActorId:actorLink.bifrostActorId,discordGuildId:request.discordGuildId,discordChannelId:request.discordChannelId,discordMessageId:request.discordMessageId,targetRuntimeId:request.targetRuntimeId,issuedAt:request.issuedAt,expiresAt:request.expiresAt,command:request.command};
    const packetSha256=`sha256-${createHash("sha256").update(encode(packetTuple(packet))).digest("hex")}`;
    admission={schemaName:"bifrost.operator_command.delivery",schemaVersion:operatorAdmissionSchema,admissionId:request.requestId,packet,packetSha256,sourceObserverId:"voidbot",sourceObserverRuntimeId:request.producerRuntimeId,provider:"bifrost",bifrostAdmissionReceiptId:request.requestId,authority:"exact_operator_command_only",providerIdentityId:identity.identityId,providerSignature:new Uint8Array()};
    admission.providerSignature=new Uint8Array(signPurpose(identity,OPERATOR_SIGNING_PURPOSE,encode(admissionTuple(admission))));parseOperatorAdmission(admission);
  }catch(error){if(error instanceof OperatorCommandPermanentRefusal)throw error;throw new OperatorCommandPermanentRefusal("invalid-request",error instanceof Error?error.message:String(error));}
  await ledger.admit(request.requestId,request,admission);
  return executeAdmission({request,admission,transport,ledger,recovered:false});
}

async function executeAdmission({request,admission,transport,ledger,recovered}){
  const envelope=await transport.execute(admission);
  if(!envelope||envelope.providerIdentityId!==transport.trustedIdentityId)throw new Error("Operator result did not cross the configured authenticated Epiphany transport boundary.");
  const result=parseOperatorResult(envelope.result);
  if(result.commandId!==admission.packet.commandId||result.packetSha256!==admission.packetSha256||result.targetRuntimeId!==admission.packet.targetRuntimeId)throw new Error("Epiphany operator result is not bound to the admitted command.");
  await ledger.complete(request.requestId,result,envelope.providerIdentityId,envelope.receipt);
  return {admission,result,receipt:envelope.receipt,resultProviderIdentityId:envelope.providerIdentityId,recovered};
}

async function compareAdmission(ledger,request,admission){try{await ledger.admit(request.requestId,request,admission);}catch(error){if(/collision/i.test(error instanceof Error?error.message:String(error)))refuse("request-identity-collision",error.message);throw error;}}
function requireDurableLedger(ledger){for(const member of ["admit","complete","getRequest","getAdmission","getCompletion","terminalize","getTerminal"])if(!ledger||typeof ledger[member]!=="function")throw new Error("Epiphany operator admission requires the durable typed collision-ledger port.");}
function requireAuthenticatedTransport(transport){if(!transport||typeof transport.execute!=="function"||typeof transport.verifyReceipt!=="function"||typeof transport.trustedIdentityId!=="string"||!transport.trustedIdentityId)throw new Error("No authenticated Epiphany operator CultNet RUDP transport is configured.");}
function refuse(code,message){throw new OperatorCommandPermanentRefusal(code,message);}
export async function enrollOperatorIdentity(path=defaultOperatorIdentityPath()){return enrollSignedIdentity(path,"Bifrost Epiphany operator identity");}
export async function loadOperatorIdentity(path=defaultOperatorIdentityPath()){return loadSignedIdentity(path,"Bifrost Epiphany operator identity");}
export {trustAnchor};

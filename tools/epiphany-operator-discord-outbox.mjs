import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { buildOperatorDefinitions,operatorRequestTuple } from "./epiphany-operator-command-documents.mjs";

export class EpiphanyOperatorDiscordOutbox {
  constructor(cache,definition,encode){this.cache=cache;this.definition=definition;this.encode=encode;}
  static async open(path,{cultcacheRuntime,encode}={}){const runtime=cultcacheRuntime??loadCultCacheRuntime(),definition=buildOperatorDefinitions(runtime.defineDocumentType).discordDelivery,cache=runtime.CultCache.builder().withDocumentType(definition).withGenericStore(new runtime.SingleFileMessagePackBackingStore(path)).build();if(typeof encode!=="function")throw new Error("operator Discord outbox requires the canonical MessagePack encoder");await cache.pullAllBackingStores();return new EpiphanyOperatorDiscordOutbox(cache,definition,encode);}
  get(requestId){return this.cache.get(this.definition,requestId);}
  isExactDelivery(request){const value=this.get(request.requestId);return Boolean(value&&value.requestPayloadSha256===this.requestDigest(request)&&value.commandId===request.commandId&&value.discordGuildId===request.discordGuildId&&value.discordChannelId===request.discordChannelId&&value.discordInteractionId===request.sourceEventId&&value.targetRuntimeId===request.targetRuntimeId);}
  async publishCompleted(request,receipt){const value=this.base(request,{status:"completed",disposition:receipt.result.disposition,failureCode:"",detail:receipt.result.detail,operatorStatus:receipt.result.operatorStatus,stateStatus:receipt.result.stateStatus,coordinatorAction:receipt.result.coordinatorAction,brakeStatus:receipt.result.brakeStatus,sealedResultPayloadSha256:receipt.resultPayloadSha256,executorSignatureSha256:`sha256-${createHash("sha256").update(receipt.executorSignature).digest("hex")}`,resultProviderIdentityId:receipt.providerIdentityId,recordedAt:receipt.completedAt});return this.put(value);}
  async publishRefused(request,terminal){const value=this.base(request,{status:"refused",disposition:"",failureCode:terminal.failureCode,detail:terminal.failureCode,operatorStatus:"",stateStatus:"",coordinatorAction:"",brakeStatus:"",sealedResultPayloadSha256:"",executorSignatureSha256:"",resultProviderIdentityId:"",recordedAt:terminal.recordedAt});return this.put(value);}
  requestDigest(request){return `sha256-${createHash("sha256").update(this.encode(operatorRequestTuple(request))).digest("hex")}`;}
  base(request,result){return {schemaVersion:"bifrost.discord.epiphany_operator_delivery.v0",deliveryId:request.requestId,requestId:request.requestId,requestPayloadSha256:this.requestDigest(request),commandId:request.commandId,discordGuildId:request.discordGuildId,discordChannelId:request.discordChannelId,discordInteractionId:request.sourceEventId,targetRuntimeId:request.targetRuntimeId,...result,privateStateExposed:false};}
  async put(value){const current=this.get(value.requestId);if(current&&stable(current)!==stable(value))throw new Error("operator Discord delivery identity collision");if(!current)await this.cache.put(this.definition,value.deliveryId,value);return current??value;}
}
function loadCultCacheRuntime(){const root=resolve(import.meta.dirname,".."),projects=resolve(root,".."),cult=resolve(process.env.VOIDBOT_CULTLIB_ROOT||resolve(projects,"CultLib")),pkg=resolve(cult,"packages","cultcache-ts","package.json");if(!existsSync(pkg))throw new Error(`CultCache TypeScript runtime is unavailable at ${pkg}.`);const requireCult=createRequire(pkg);return requireCult("cultcache-ts");}
function stable(v){return JSON.stringify(v,(_k,x)=>x instanceof Uint8Array?Buffer.from(x).toString("hex"):x);}

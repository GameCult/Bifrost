#!/usr/bin/env node
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { chmod, mkdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { buildOperatorDefinitions } from "./epiphany-operator-command-documents.mjs";
import { buildFeedbackDefinitions } from "./persona-feedback-documents.mjs";
import { admitOperatorRequest,enrollOperatorIdentity,loadOperatorIdentity,OperatorCommandPermanentRefusal,trustAnchor } from "./epiphany-operator-commands.mjs";
import { EpiphanyOperatorCultNetRudpTransport,loadEpiphanyOperatorTrustAnchor } from "./epiphany-operator-cultnet-rudp.mjs";
import { DurableOperatorLedger } from "./epiphany-operator-ledger.mjs";
import { EpiphanyOperatorDiscordOutbox } from "./epiphany-operator-discord-outbox.mjs";

const {command,options}=parseArgs(process.argv.slice(2));
if(command==="help"){
  process.stdout.write("epiphany-operator-worker <once|serve|enroll-identity> --request-store <cc> --private-store <cc> --ledger-store <cc> --delivery-store <cc> --private-key <seed> --epiphany-trust-anchor <msgpack> --epiphany-rudp <rudp://host:port> --owner-discord-id <id> [--trust-anchor-out <msgpack>] [--runtime-id <id>] [--interval-ms <n>] [--timeout-ms <n>]\n");
  process.exit(0);
}
await main().catch(error=>{process.stderr.write(`${error instanceof Error?error.message:String(error)}\n`);process.exitCode=1;});

async function main(){
  const runtime=loadRuntime();
  if(command==="enroll-identity")return enrollIdentity(runtime);
  if(!["once","serve"].includes(command))throw new Error("operator worker command must be once, serve, or enroll-identity");
  const operatorDefs=buildOperatorDefinitions(runtime.defineDocumentType);
  const feedbackDefs=buildFeedbackDefinitions(runtime.defineDocumentType);
  const requestStore=requiredOption("request-store","BIFROST_EPIPHANY_OPERATOR_REQUEST_STORE");
  const privateStore=requiredOption("private-store","BIFROST_PERSONA_FEEDBACK_PRIVATE_STORE");
  const ledgerStore=requiredOption("ledger-store","BIFROST_EPIPHANY_OPERATOR_LEDGER_STORE");
  const deliveryStore=requiredOption("delivery-store","BIFROST_EPIPHANY_OPERATOR_DISCORD_DELIVERY_STORE");
  const ownerDiscordId=requiredOption("owner-discord-id","BIFROST_EPIPHANY_OPERATOR_OWNER_DISCORD_ID");
  assertDistinctStores(requestStore,privateStore,ledgerStore,deliveryStore);
  const identity=await loadOperatorIdentity(resolve(requiredOption("private-key","BIFROST_EPIPHANY_OPERATOR_PRIVATE_KEY")));
  const anchor=await loadEpiphanyOperatorTrustAnchor(resolve(requiredOption("epiphany-trust-anchor","BIFROST_EPIPHANY_EXECUTOR_TRUST_ANCHOR")),{decode:runtime.decode});
  const transport=new EpiphanyOperatorCultNetRudpTransport({endpoint:requiredOption("epiphany-rudp","BIFROST_EPIPHANY_OPERATOR_RUDP"),runtimeId:option("runtime-id","BIFROST_RUNTIME_ID")||"bifrost-yggdrasil",trustedEpiphanyIdentity:anchor,timeoutMs:integerOption("timeout-ms",5000,100,60000),cultnetRuntime:runtime.cultnet,msgpackRuntime:runtime.msgpack});
  const ledger=await DurableOperatorLedger.open(resolve(ledgerStore),{cultcacheRuntime:runtime.cultcache});
  const outbox=await EpiphanyOperatorDiscordOutbox.open(resolve(deliveryStore),{cultcacheRuntime:runtime.cultcache,encode:runtime.encode});
  const requests=runtime.CultCache.builder().withDocumentType(operatorDefs.request).withGenericStore(new runtime.SingleFileMessagePackBackingStore(resolve(requestStore))).build();
  const privateCache=runtime.CultCache.builder().withDocumentType(feedbackDefs.actorLink).withGenericStore(new runtime.SingleFileMessagePackBackingStore(resolve(privateStore))).build();
  const interval=integerOption("interval-ms",500,100,60000);
  do{
    const output=await processPending({requests,privateCache,operatorDefs,feedbackDefs,ledger,outbox,ownerDiscordId,identity,transport,encode:runtime.encode});
    process.stdout.write(`${JSON.stringify({schemaVersion:"bifrost.epiphany_operator_worker_pulse.v0",status:output.every(x=>["completed","refused"].includes(x.status))?"completed":"degraded",processed:output.length,results:output,privateStateExposed:false})}\n`);
    if(command==="once")return;
    await new Promise(done=>setTimeout(done,interval));
  }while(true);
}

async function enrollIdentity(runtime){
  const privatePath=resolve(requiredOption("private-key","BIFROST_EPIPHANY_OPERATOR_PRIVATE_KEY"));
  const outputPath=resolve(requiredOption("trust-anchor-out","BIFROST_EPIPHANY_OPERATOR_TRUST_ANCHOR_OUT"));
  if(privatePath===outputPath)throw new Error("operator private identity and public trust anchor must be physically distinct");
  const identity=await enrollOperatorIdentity(privatePath),anchor=trustAnchor(identity,new Date().toISOString());
  await mkdir(resolve(outputPath,".."),{recursive:true});
  await writeFile(outputPath,runtime.encode([anchor.schemaVersion,anchor.identityId,anchor.publicKey,anchor.assurance,anchor.identityCreatedAt,anchor.sourceIdentityRecordSha256]),{flag:"wx",mode:0o640});
  await chmod(outputPath,0o640);
  process.stdout.write(`${JSON.stringify({schemaVersion:"bifrost.epiphany_operator_identity_enrollment.v0",identityId:identity.identityId,trustAnchor:outputPath,privateStateExposed:false})}\n`);
}

async function processPending({requests,privateCache,operatorDefs,feedbackDefs,ledger,outbox,ownerDiscordId,identity,transport,encode}){
  await requests.pullAllBackingStores();await privateCache.pullAllBackingStores();
  const pending=requests.getAll(operatorDefs.request).filter(request=>!outbox.isExactDelivery(request)).sort((a,b)=>a.issuedAt.localeCompare(b.issuedAt));
  const output=[];
  for(const request of pending){
    const terminal=ledger.getTerminal(request.requestId);
    if(terminal){await outbox.publishRefused(request,terminal);output.push(summary(request,"refused","",terminal.failureCode));continue;}
    try{
      const actorLink=privateCache.get(feedbackDefs.actorLink,`${request.discordGuildId}:${request.sourceActorDiscordId}`);
      const result=await admitOperatorRequest({request,ownerDiscordId,actorLink,identity,encode,transport,ledger});
      await outbox.publishCompleted(request,result.receipt);
      output.push(summary(request,"completed",result.result.disposition,result.result.detail));
    }catch(error){
      if(error instanceof OperatorCommandPermanentRefusal){
        const value={schemaVersion:"bifrost.operator_command.terminal.v0",requestId:request.requestId,commandId:request.commandId,status:"refused",failureCode:error.code,recordedAt:new Date().toISOString(),retryable:false,privateStateExposed:false};
        await ledger.terminalize(value);await outbox.publishRefused(request,value);
      }
      output.push(summary(request,error instanceof OperatorCommandPermanentRefusal?"refused":"retryable-failure","",error instanceof OperatorCommandPermanentRefusal?error.code:"transport-or-ledger-unavailable"));
    }
  }
  return output;
}

function summary(request,status,disposition,detail){return {requestId:request.requestId,commandId:request.commandId,status,disposition,detail};}
function loadRuntime(){const root=resolve(import.meta.dirname,".."),projects=resolve(root,".."),cult=resolve(process.env.VOIDBOT_CULTLIB_ROOT||resolve(projects,"CultLib")),cachePkg=resolve(cult,"packages","cultcache-ts","package.json"),netPkg=resolve(cult,"packages","cultnet-ts","package.json");if(!existsSync(cachePkg)||!existsSync(netPkg))throw new Error("CultLib CultCache/CultNet TypeScript runtimes are unavailable");const cr=createRequire(cachePkg),nr=createRequire(netPkg),cultcache=cr("cultcache-ts"),msgpack=nr("@msgpack/msgpack");return {cultcache,cultnet:nr("cultnet-ts"),msgpack,encode:msgpack.encode,decode:msgpack.decode,CultCache:cultcache.CultCache,SingleFileMessagePackBackingStore:cultcache.SingleFileMessagePackBackingStore,defineDocumentType:cultcache.defineDocumentType};}
function parseArgs(values){const command=values.shift()??"help",options={},allowed=new Set(["request-store","private-store","ledger-store","delivery-store","private-key","trust-anchor-out","epiphany-trust-anchor","epiphany-rudp","owner-discord-id","runtime-id","interval-ms","timeout-ms"]);while(values.length){const flag=values.shift();if(!flag?.startsWith("--")||!values.length)throw new Error(`invalid operator worker option ${flag??""}`);const key=flag.slice(2);if(!allowed.has(key))throw new Error(`unknown operator worker option --${key}`);if(Object.hasOwn(options,key))throw new Error(`duplicate operator worker option --${key}`);options[key]=values.shift();}return {command,options};}
function option(name,environment){return options[name]??process.env[environment];}
function requiredOption(name,environment){const value=option(name,environment);if(typeof value!=="string"||!value.trim())throw new Error(`operator worker requires --${name} or ${environment}`);return value;}
function integerOption(name,fallback,min,max){const raw=options[name],value=raw===undefined?fallback:Number(raw);if(!Number.isInteger(value)||value<min||value>max)throw new Error(`operator worker --${name} must be ${min}..=${max}`);return value;}
function assertDistinctStores(...paths){const resolved=paths.map(path=>resolve(path).toLowerCase());if(new Set(resolved).size!==resolved.length)throw new Error("operator request, actor-link, immutable ledger, and Discord delivery stores must be physically distinct");}

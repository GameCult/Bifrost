import test from "node:test";
import assert from "node:assert/strict";
import {spawn} from "node:child_process";
import {createHash} from "node:crypto";
import {createRequire} from "node:module";
import {mkdtemp,readFile,writeFile} from "node:fs/promises";
import {tmpdir} from "node:os";
import {resolve} from "node:path";
import {enrollDomainIdentity,signDomainPurpose} from "../tools/bifrost-signed-identity.mjs";
import {receiptIdentityDomain,receiptSignatureDomain,receiptSigningPurpose,receiptSigningTuple,requestSigningTuple} from "../tools/persona-discord-delivery-documents.mjs";
import {startPersonaDiscordDeliveryRudpServer} from "../tools/persona-discord-rudp.mjs";

const epiphany=resolve(import.meta.dirname,"..","..","Epiphany");

test("Rust Persona mouth and Node Bifrost exchange one signed request and terminal receipt over CultNet RUDP",async()=>{
  const root=await mkdtemp(resolve(tmpdir(),"persona-rudp-cross-language-")),runtime=loadRuntime(),deliveryIdentity=await enrollDomainIdentity(resolve(root,"delivery.seed"),"delivery identity",receiptIdentityDomain),receiptAnchor=resolve(root,"receipt-anchor.msgpack"),requestAnchor=resolve(root,"request-anchor.msgpack"),requestStore=resolve(root,"requests.cc"),receiptStore=resolve(root,"receipts.cc"),identityStore=resolve(root,"mouth.cc");
  await writeFile(receiptAnchor,runtime.msgpack.encode(["gamecult.service_trust_anchor.v1","bifrost-persona-discord-delivery:bifrost-discord-yggdrasil:v0","bifrost-persona-discord-delivery","bifrost-discord-yggdrasil",deliveryIdentity.identityId,new Uint8Array(deliveryIdentity.publicKey),"ed25519",receiptSigningPurpose,"bifrost.persona_discord_delivery_receipt.v0","root",Date.now(),null,false]));
  let admitted;
  const server=await startPersonaDiscordDeliveryRudpServer({runtime,endpoint:"rudp://127.0.0.1:0",onRequest:async request=>{admitted=request;const digest=`sha256-${createHash("sha256").update(runtime.msgpack.encode(requestSigningTuple(request))).digest("hex")}`,receipt={schemaVersion:"bifrost.persona_discord_delivery_receipt.v0",receiptId:request.requestId,requestId:request.requestId,requestPayloadSha256:digest,status:"completed",channelId:request.channelId,replyToMessageId:request.replyToMessageId,messageId:"node-rudp-message",transport:"bifrost.discord-post",crossingReceiptId:"node-rudp-crossing",receiptUrl:`https://discord.com/channels/g/${request.channelId}/node-rudp-message`,completedAt:new Date().toISOString(),providerIdentityId:deliveryIdentity.identityId,privateStateExposed:false,providerSignature:new Uint8Array()};receipt.providerSignature=new Uint8Array(signDomainPurpose(deliveryIdentity,receiptSignatureDomain,receiptSigningPurpose,runtime.msgpack.encode(receiptSigningTuple(receipt))));return receipt;}});
  try{
    const output=await run("cargo",["run","--quiet","--manifest-path",resolve(epiphany,"epiphany-core","Cargo.toml"),"--bin","epiphany-persona-discord-rudp-client-fixture","--","--request-store",requestStore,"--receipt-store",receiptStore,"--identity-store",identityStore,"--request-anchor",requestAnchor,"--receipt-anchor",receiptAnchor,"--endpoint",`127.0.0.1:${server.address.port}`],{cwd:epiphany,env:{...process.env,CARGO_TARGET_DIR:"C:\\Users\\Meta\\.cargo-target-codex"}});
    assert.equal(output.code,0,`${output.stderr}\nserver diagnostics: ${JSON.stringify(server.diagnostics)}`);const result=JSON.parse(output.stdout.trim());assert.equal(result.status,"completed");assert.equal(result.messageId,"node-rudp-message");assert.equal(admitted.targetRuntimeId,"epiphany-starfire");assert.ok((await readFile(requestStore)).length>0);assert.ok((await readFile(receiptStore)).length>0);
  }finally{server.close();}
});

function loadRuntime(){const cult=resolve(import.meta.dirname,"..","..","CultLib"),nr=createRequire(resolve(cult,"packages","cultnet-ts","package.json")),cultnet=nr("cultnet-ts"),msgpack=nr("@msgpack/msgpack");return {cultnet,msgpack};}
function run(command,args,options){return new Promise((resolveRun,reject)=>{const child=spawn(command,args,{...options,stdio:["ignore","pipe","pipe"]});let stdout="",stderr="";child.stdout.on("data",chunk=>stdout+=chunk);child.stderr.on("data",chunk=>stderr+=chunk);child.once("error",reject);child.once("close",code=>resolveRun({code,stdout,stderr}));});}

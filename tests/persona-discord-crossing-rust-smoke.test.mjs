import test from "node:test";
import assert from "node:assert/strict";
import {spawnSync} from "node:child_process";
import {chmod,mkdtemp,writeFile} from "node:fs/promises";
import {tmpdir} from "node:os";
import {resolve} from "node:path";
import {createRequire} from "node:module";
import {enrollDomainIdentity} from "../tools/bifrost-signed-identity.mjs";
import {receiptIdentityDomain} from "../tools/persona-discord-delivery-documents.mjs";
import {processPersonaDiscordDeliveries} from "../tools/persona-discord-delivery.mjs";

const epiphany=resolve(import.meta.dirname,"..","..","EpiphanyAgent"),cult=resolve(import.meta.dirname,"..","..","CultLib");
function runtime(){const cr=createRequire(resolve(cult,"packages","cultcache-ts","package.json")),nr=createRequire(resolve(cult,"packages","cultnet-ts","package.json")),cc=cr("cultcache-ts"),mp=nr("@msgpack/msgpack");return {CultCache:cc.CultCache,SingleFileMessagePackBackingStore:cc.SingleFileMessagePackBackingStore,defineDocumentType:cc.defineDocumentType,encode:mp.encode,decode:mp.decode};}

test("Bifrost consumes a Rust-authored request store through its read-only crossing directory",async()=>{
  const root=await mkdtemp(resolve(tmpdir(),"rust-bifrost-crossing-")),requestDir=resolve(root,"request"),receiptDir=resolve(root,"receipt"),requestStore=resolve(requestDir,"requests.cc"),anchor=resolve(root,"mouth-anchor.msgpack"),identity=resolve(root,"mouth.cc"),deliveryKey=resolve(root,"delivery.seed"),bridge=resolve(root,"bridge.mjs");
  const cargo=spawnSync("cargo",["run","--quiet","--manifest-path",resolve(epiphany,"epiphany-core","Cargo.toml"),"--bin","epiphany-persona-discord-crossing-fixture","--","--request-store",requestStore,"--identity-store",identity,"--request-anchor",anchor],{cwd:epiphany,encoding:"utf8",timeout:180000,windowsHide:true});
  assert.equal(cargo.status,0,cargo.stderr||cargo.stdout);
  await chmod(requestStore,0o444);
  await enrollDomainIdentity(deliveryKey,"delivery",receiptIdentityDomain);
  await writeFile(bridge,`console.log(JSON.stringify({action:"discord-post",ok:true,channelId:"123",messageId:"rust-smoke-message",transport:"smoke",crossingReceiptId:"rust-smoke-receipt",url:"https://discord.invalid/rust-smoke"}))`);
  const r=runtime(),results=await processPersonaDiscordDeliveries({runtime:r,requestStorePath:requestStore,receiptStorePath:resolve(receiptDir,"receipts.cc"),executionStorePath:resolve(root,"private","executions.cc"),epiphanyTrustAnchorPath:anchor,privateKeyPath:deliveryKey,targetAdmitted:()=>true,requestPermit:async({requestPayloadSha256})=>({permitId:`permit:${requestPayloadSha256}`,expiresAt:new Date(Date.now()+5000).toISOString()}),bridgeCliPath:bridge,bifrostRoot:root});
  assert.equal(results[0]?.status,"completed");
  assert.equal(results[0]?.requestId,"rust-crossing-smoke-1");
});

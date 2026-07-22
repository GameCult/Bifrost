import test from "node:test";
import assert from "node:assert/strict";
import { createHash, createPublicKey, verify } from "node:crypto";
import { createRequire } from "node:module";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { enrollBifrostFeedbackIdentity, providerHealthIdentity, signFeedbackAdmission } from "../tools/bifrost-feedback-identity.mjs";
import { createPersonaFeedbackHealthPublisher, createSignedHealthRawPut, createSignedPersonaFeedbackHealth, signedHealthSourceRole } from "../tools/persona-feedback-idunn-health.mjs";

const root=resolve(import.meta.dirname,".."),cult=resolve(root,"..","CultLib"),require=createRequire(resolve(cult,"packages","cultnet-ts","package.json")),{encode,decode}=require("@msgpack/msgpack");
const idDomain=Buffer.from("gamecult.provider-health.identity.v1\0"),signatureDomain=Buffer.from("gamecult.provider-health.signature.v1\0"),purpose=Buffer.from("idunn.signed_daemon_health.v1");

test("Persona-feedback emits the exact canonical signed daemon-health tuple",async()=>{
  assert.equal(signedHealthSourceRole,"daemon-health-publisher");
  const identity=await identityFixture(),incarnation="019f523f-f650-7001-be75-b4428985652b",signed=createSignedPersonaFeedbackHealth({daemonId:"yggdrasil-bifrost-persona-feedback",healthContract:"bifrost.cultnet-rudp-persona-feedback-health",state:"active",detail:"ready",identity,publisherIncarnationId:incarnation,publisherSequence:7,observedAtUnixMillis:1773950400123}),record=decode(signed.payload,{useBigInt64:true});
  assert.equal(record.length,17);assert.deepEqual(record.slice(0,10),["idunn.signed_daemon_health.v1","yggdrasil-bifrost-persona-feedback","bifrost.cultnet-rudp-persona-feedback-health","bifrost-persona-feedback","active","ready",signed.providerIdentityId,incarnation,7,1773950400123n]);assert.deepEqual(record.slice(10,14),[null,null,null,null]);assert.ok(record[15] instanceof Uint8Array);assert.equal(record[14],"ed25519");assert.equal(record[15].length,64);assert.equal(record[16],false);
  const put=createSignedHealthRawPut("yggdrasil-bifrost-persona-feedback",signed);assert.equal(put.document.recordKey,"yggdrasil-bifrost-persona-feedback");assert.equal(put.document.sourceRuntimeId,"bifrost-persona-feedback");assert.equal(put.document.sourceRole,"daemon-health-publisher");assert.equal(put.document.schemaId,"idunn.signed_daemon_health");assert.deepEqual(put.document.payload,signed.payload);
  const canonical=[...record];canonical[15]=new Uint8Array();assert.equal(verifyProvider(identity,encode(canonical,{useBigInt64:true}),record[15]),true);
  assert.equal(signed.providerIdentityId,createHash("sha256").update(idDomain).update(identity.publicKey).digest("hex"));assert.notEqual(signed.providerIdentityId,identity.identityId);
});

test("provider-health signature rejects purpose, key, and payload substitution",async()=>{
  const identity=await identityFixture(),other=await identityFixture(),signed=createSignedPersonaFeedbackHealth({daemonId:"daemon",healthContract:"contract",state:"degraded",detail:"readiness-degraded",identity,publisherIncarnationId:"019f523f-f650-7001-be75-b4428985652b",publisherSequence:1,observedAtUnixMillis:1773950400123}),record=decode(signed.payload,{useBigInt64:true}),canonical=[...record];canonical[15]=new Uint8Array();
  const canonicalPayload=encode(canonical,{useBigInt64:true});assert.equal(verifyProvider(identity,canonicalPayload,record[15]),true);assert.equal(verifyProvider(other,canonicalPayload,record[15]),false);
  const mutated=[...canonical];mutated[4]="active";assert.equal(verifyProvider(identity,encode(mutated,{useBigInt64:true}),record[15]),false);
  const oldDomainSignature=signFeedbackAdmission(identity,canonicalPayload);assert.equal(verifyProvider(identity,canonicalPayload,oldDomainSignature),false);
  const unsignedDiagnostic=[record[1],record[4],record[5],new Date(Number(record[9])).toISOString(),record[2],"diagnostic-only","cultnet.transport.rudp.v0"];assert.equal(verifyProvider(identity,encode(unsignedDiagnostic),record[15]),false);
});

test("publisher sequence and process incarnation are mandatory protocol state",async()=>{
  const identity=await identityFixture(),base={daemonId:"daemon",healthContract:"contract",state:"warming",detail:"starting",identity,publisherIncarnationId:"019f523f-f650-7001-be75-b4428985652b",publisherSequence:1,observedAtUnixMillis:1773950400123};
  for(const bad of [{publisherSequence:0},{publisherSequence:1.5},{publisherIncarnationId:"process-1"},{state:"healthy"},{detail:"private database error"}])assert.throws(()=>createSignedPersonaFeedbackHealth({...base,...bad}));
  const publisher=createPersonaFeedbackHealthPublisher(identity,base.publisherIncarnationId),first=publisher.next({daemonId:base.daemonId,healthContract:base.healthContract,state:base.state,detail:base.detail,observedAtUnixMillis:base.observedAtUnixMillis}),second=publisher.next({daemonId:base.daemonId,healthContract:base.healthContract,state:base.state,detail:base.detail,observedAtUnixMillis:base.observedAtUnixMillis+1});assert.equal(decode(first.payload)[7],decode(second.payload)[7]);assert.equal(decode(second.payload)[8],decode(first.payload)[8]+1);
});

async function identityFixture(){const dir=await mkdtemp(resolve(tmpdir(),"bifrost-provider-health-"));return enrollBifrostFeedbackIdentity(resolve(dir,"feedback.seed"));}
function verifyProvider(identity,payload,signature){const key=createPublicKey({key:Buffer.concat([Buffer.from("302a300506032b6570032100","hex"),Buffer.from(identity.publicKey)]),format:"der",type:"spki"}),message=Buffer.concat([signatureDomain,u64(purpose.length),purpose,u64(payload.length),Buffer.from(payload)]);return verify(null,message,key,Buffer.from(signature));}
function u64(value){const out=Buffer.alloc(8);out.writeBigUInt64BE(BigInt(value));return out;}

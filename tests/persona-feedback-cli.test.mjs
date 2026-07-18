import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash, createPublicKey, verify } from "node:crypto";
import { createRequire } from "node:module";
import { mkdtemp, stat, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { buildFeedbackDefinitions } from "../tools/persona-feedback-documents.mjs";

const root=resolve(import.meta.dirname,".."), cult=resolve(root,"..","CultLib");
const cr=createRequire(resolve(cult,"packages","cultcache-ts","package.json"));
const mr=createRequire(resolve(cult,"packages","cultmesh-ts","package.json"));
const {defineDocumentType}=cr("cultcache-ts"), {CultMesh}=mr("cultmesh-ts"), defs=buildFeedbackDefinitions(defineDocumentType);
const nr=createRequire(resolve(cult,"packages","cultnet-ts","package.json")),{decode,encode}=nr("@msgpack/msgpack");

test("Bifrost admits bound feedback as pressure only and exports CultNet",async()=>{
  const dir=await mkdtemp(resolve(tmpdir(),"bifrost-feedback-")), store=resolve(dir,"provider.cc"), observationStore=resolve(dir,"observations.cc"), deliveryStore=resolve(dir,"delivery.cc"), unusedDeliveryDefault=resolve(dir,"unused-delivery.cc"), out=resolve(dir,"feedback.msgpack"), key=resolve(dir,"identity.seed"), anchor=resolve(dir,"anchor.msgpack");
  cliNoStore("enroll-identity","--private-key",key,"--trust-anchor-out",anchor);
  cli("bind-target",store,"--guild-id","g","--channel-id","c","--persona-id","epiphany","--repo","GameCult/Epiphany","--runtime-id","epiphany-yggdrasil","--producer-runtime-id","voidbot-yggdrasil","--source-visibility","organization","--data-classification","organization_feedback","--delivery-store",deliveryStore);
  await putEvent(observationStore,event("one","human-one"));
  const sourceBefore=await readEvent(observationStore,"one");
  const served=cli("serve",store,"--observation-store",observationStore,"--once","true","--private-key",key,"--delivery-store",unusedDeliveryDefault,"--interval-ms","1");
  assert.equal(served.scanned,1);assert.equal(served.results[0].ok,true);
  assert.deepEqual(await readEvent(observationStore,"one"),sourceBefore);
  const n=await CultMesh.createNode(store,{documents:[defs.binding,defs.actorLink,defs.receipt,defs.delivery]});await n.cache?.pullAllBackingStores?.();
  const first={delivery:(await n.get(defs.delivery,"one"))?.value??await n.get(defs.delivery,"one"),receipt:(await n.get(defs.receipt,"one"))?.value??await n.get(defs.receipt,"one")};
  assert.equal(first.receipt.classification,"unlinked_social_feedback");
  assert.equal(first.delivery.authority,"feedback_only");
  assert.equal(first.delivery.packet.privateStateIncluded,false);
  assert.equal(first.delivery.packet.sourceVisibility,"organization");
  assert.equal(first.delivery.packet.dataClassification,"organization_feedback");
  assert.match(first.delivery.packetSha256,/^sha256-[a-f0-9]{64}$/);
  assert.equal(Object.keys(first.delivery.providerSignature).length,64);
  const signed=(await n.get(defs.delivery,"one"))?.value??await n.get(defs.delivery,"one"), trust=decode(await readFile(anchor));
  assert.equal(signed.packetSha256,`sha256-${createHash("sha256").update(encode(packetTuple(signed.packet))).digest("hex")}`);
  assert.equal(signed.sourceObserverId,"voidbot");assert.equal(signed.provider,"bifrost");
  const payload=encode([signed.schemaVersion,signed.admissionId,packetTuple(signed.packet),signed.packetSha256,signed.sourceObserverId,signed.sourceObserverRuntimeId,signed.provider,signed.bifrostAdmissionReceiptId,signed.authority,signed.providerIdentityId]);
  const purpose=Buffer.from("bifrost.persona-feedback.delivery.v0"),domain=Buffer.from("epiphany.host-incarnation.signature.v0\0"),message=Buffer.concat([domain,u64(purpose.length),purpose,u64(payload.length),payload]);
  assert.equal(trust.length,6); assert.equal(trust[0],"epiphany.host_identity_trust_anchor.v0"); assert.equal(trust[1],signed.providerIdentityId);
  const publicKey=createPublicKey({key:Buffer.concat([Buffer.from("302a300506032b6570032100","hex"),Buffer.from(trust[2])]),format:"der",type:"spki"});
  assert.equal(verify(null,message,publicKey,Buffer.from(signed.providerSignature)),true);
  assert.equal(first.receipt.grantsWorkAuthority,false);
  cli("link-actor",store,"--guild-id","g","--discord-author-id","human-two","--bifrost-actor-id","member:42","--heimdall-actor-ref","heimdall:42");
  await putEvent(observationStore,event("two","human-two"));
  assert.equal(cli("process",store,"--observation-store",observationStore,"--event-id","two","--private-key",key,"--delivery-store",deliveryStore).receipt.classification,"linked_governance_feedback");
  const replay=cli("process",store,"--observation-store",observationStore,"--event-id","two","--private-key",key,"--delivery-store",deliveryStore);
  assert.equal(replay.receiptId,"two");
  const exported=cli("export",store,"--out",out);
  assert.ok(exported.documentCount>=4); assert.ok((await stat(out)).size>0);
  const deliveryCache=cr("cultcache-ts").CultCache.builder().withDocumentType(defs.delivery).withGenericStore(new (cr("cultcache-ts").SingleFileMessagePackBackingStore)(deliveryStore)).build();await deliveryCache.pullAllBackingStores();assert.equal(deliveryCache.getAll(defs.delivery).length,2);
  const beforeStatus=await Promise.all([store,observationStore,deliveryStore,key].map(path=>readFile(path)));
  const readiness=cli("status",store,"--observation-store",observationStore,"--private-key",key);
  assert.equal(readiness.schemaVersion,"bifrost.persona_feedback.readiness.v0");assert.equal(readiness.status,"ready");assert.equal(readiness.bindingCount,1);assert.equal(readiness.privateStateExposed,false);
  const afterStatus=await Promise.all([store,observationStore,deliveryStore,key].map(path=>readFile(path)));assert.deepEqual(afterStatus,beforeStatus);
  const Store=cr("cultcache-ts").SingleFileMessagePackBackingStore;
  assert.deepEqual([...new Set((await new Store(observationStore).pullAll()).map(entry=>entry.type))],["voidbot.discord.persona_feedback_event"]);
  assert.equal((await new Store(store).pullAll()).some(entry=>entry.type==="voidbot.discord.persona_feedback_event"),false);
  assert.deepEqual([...new Set((await new Store(deliveryStore).pullAll()).map(entry=>entry.type))],["bifrost.persona_feedback.delivery"]);
  await assert.rejects(stat(unusedDeliveryDefault));
});

test("Bifrost rejects an event without exact target binding",async()=>{const dir=await mkdtemp(resolve(tmpdir(),"bifrost-feedback-")),store=resolve(dir,"provider.cc"),observationStore=resolve(dir,"observations.cc");await putEvent(observationStore,event("wrong","human","other-runtime"));const r=raw("process",store,"--observation-store",observationStore,"--event-id","wrong");assert.notEqual(r.status,0);assert.match(r.stderr,/No exact Bifrost target binding/);assert.equal((await readEvent(observationStore,"wrong")).status,"pending");});

test("Bifrost rejects aliased observation, private, and delivery stores",async()=>{const dir=await mkdtemp(resolve(tmpdir(),"bifrost-feedback-")),store=resolve(dir,"provider.cc"),observations=resolve(dir,"observations.cc");await putEvent(observations,event("alias","human"));const r=raw("bind-target",store,"--observation-store",observations,"--guild-id","g","--channel-id","c","--persona-id","epiphany","--repo","GameCult/Epiphany","--runtime-id","epiphany-yggdrasil","--producer-runtime-id","voidbot-yggdrasil","--source-visibility","organization","--data-classification","organization_feedback","--delivery-store",store);assert.notEqual(r.status,0);assert.match(r.stderr,/distinct canonical paths/);});

test("Bifrost rejects mismatched classification and malformed observations",async()=>{const dir=await mkdtemp(resolve(tmpdir(),"bifrost-feedback-")),store=resolve(dir,"provider.cc"),observations=resolve(dir,"observations.cc"),delivery=resolve(dir,"delivery.cc");const mismatch=raw("bind-target",store,"--observation-store",observations,"--guild-id","g","--channel-id","c","--persona-id","epiphany","--repo","GameCult/Epiphany","--runtime-id","epiphany-yggdrasil","--producer-runtime-id","voidbot-yggdrasil","--source-visibility","private","--data-classification","public_feedback","--delivery-store",delivery);assert.notEqual(mismatch.status,0);assert.match(mismatch.stderr,/exact admitted pair/);for(const malformed of [{observedAt:"yesterday"},{addressingMode:"ambient"},{authorityClass:"work"},{status:"admitted"},{payloadHash:"not-a-digest"}])await assert.rejects(putEvent(observations,{...event(`bad-${Object.keys(malformed)[0]}`,"human"),...malformed}));});

function event(id,author,targetRuntimeId="epiphany-yggdrasil"){const content=`feedback ${id}`;return {schemaName:"voidbot.discord.persona_feedback_event",schemaVersion:"voidbot.discord.persona_feedback_event.v0",eventId:id,observedAt:new Date().toISOString(),guildId:"g",channelId:"c",messageId:`m-${id}`,authorId:author,authorName:author,addressingMode:"role",targetPersonaId:"epiphany",targetRepoName:"GameCult/Epiphany",targetRuntimeId,content,payloadHash:createHash("sha256").update(content).digest("hex"),producerId:"voidbot",producerRuntimeId:"voidbot-yggdrasil",authorityClass:"feedback_only",status:"pending"};}
async function putEvent(store,value){const n=await CultMesh.createNode(store,{documents:[defs.event]});await n.put(defs.event,value.eventId,value);await n.flush?.();}
async function readEvent(store,id){const n=await CultMesh.createNode(store,{documents:[defs.event]});await n.cache?.pullAllBackingStores?.();return (await n.get(defs.event,id))?.value??await n.get(defs.event,id);}
function raw(command,store,...args){return spawnSync(process.execPath,[resolve(root,"tools","persona-feedback.mjs"),command,"--store",store,...args],{cwd:root,encoding:"utf8"});}
function cli(command,store,...args){const r=raw(command,store,...args);assert.equal(r.status,0,r.stderr||r.stdout);return JSON.parse(r.stdout);}
function cliNoStore(command,...args){const r=spawnSync(process.execPath,[resolve(root,"tools","persona-feedback.mjs"),command,...args],{cwd:root,encoding:"utf8"});assert.equal(r.status,0,r.stderr||r.stdout);return JSON.parse(r.stdout);}
function packetTuple(p){return [p.feedbackId,p.sourceEventId,p.sourceActorId,p.actorClassification,p.actorLinkRefs,p.discordGuildId,p.discordChannelId,p.discordMessageId,p.targetRuntimeId,p.targetRepository,p.targetPersonaId,p.sourceRoomId,p.feedbackText,p.contentSha256,p.sourceDiscussionRefs,p.sourceVisibility,p.dataClassification,p.privateStateIncluded];}
function u64(v){const b=Buffer.alloc(8);b.writeBigUInt64BE(BigInt(v));return b;}

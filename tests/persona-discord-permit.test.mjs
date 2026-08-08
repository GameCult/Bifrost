import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { enrollDomainIdentity, signDomainPurpose } from "../tools/bifrost-signed-identity.mjs";
import { permitIdentityDomain, permitPurpose, permitSchema, permitSignatureDomain, permitTuple, parseEpiphanyPermitAnchorTuple, validatePermit } from "../tools/persona-discord-permit.mjs";

const requireCultNet=createRequire(resolve(import.meta.dirname,"..","..","CultLib","packages","cultnet-ts","package.json"));
const {encode}=requireCultNet("@msgpack/msgpack");

test("Rust-shaped permit anchors and signatures retain byte authority",async()=>{const root=await mkdtemp(resolve(tmpdir(),"persona-permit-rust-shape-")),identity=await enrollDomainIdentity(resolve(root,"permit.seed"),"permit identity",permitIdentityDomain),now=Date.now(),anchor=parseEpiphanyPermitAnchorTuple(["gamecult.service_trust_anchor.v1","anchor","epiphany-persona-discord-permit","epiphany-starfire",identity.identityId,Array.from(identity.publicKey),"ed25519",permitPurpose,permitSchema,"root",now-1000,null,false],"epiphany-starfire",now),request={requestId:"request-1",requestPayloadSha256:`sha256-${"a".repeat(64)}`,targetRuntimeId:"epiphany-starfire",nonce:"nonce-1",requesterIdentityId:"requester"},permit={schemaVersion:permitSchema,permitId:`permit:${request.requestPayloadSha256}`,requestId:request.requestId,requestPayloadSha256:request.requestPayloadSha256,targetRuntimeId:request.targetRuntimeId,nonce:request.nonce,requesterIdentityId:request.requesterIdentityId,brakeStateDocumentId:"brake",brakeStateDocumentSha256:`sha256-${"b".repeat(64)}`,brakeObservedAt:new Date(now-100).toISOString(),issuedAt:new Date(now).toISOString(),expiresAt:new Date(now+4000).toISOString(),providerIdentityId:identity.identityId,privateStateExposed:false,providerSignature:[]};permit.providerSignature=Array.from(signDomainPurpose(identity,permitSignatureDomain,permitPurpose,encode(permitTuple(permit))));const parsed=validatePermit(permit,request,anchor,encode);assert.ok(parsed.providerSignature instanceof Uint8Array);assert.equal(parsed.providerSignature.length,64);});

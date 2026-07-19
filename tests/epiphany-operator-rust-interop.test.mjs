import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { admissionTuple,parseOperatorAdmission } from "../tools/epiphany-operator-command-documents.mjs";
import { OPERATOR_SIGNING_PURPOSE } from "../tools/epiphany-operator-commands.mjs";
import { loadEpiphanyOperatorTrustAnchor,verifySealedReceipt } from "../tools/epiphany-operator-cultnet-rudp.mjs";
import { verifyPurpose } from "../tools/bifrost-signed-identity.mjs";

const fixture=process.env.EPIPHANY_OPERATOR_RUST_FIXTURE??resolve("..","EpiphanyAgent",".epiphany-smoke","operator-command-interop-rust"),requireCult=createRequire(resolve("E:/Projects/CultLib/packages/cultnet-ts/package.json")),{decode,encode}=requireCult("@msgpack/msgpack");
test("consumes and verifies the Rust-emitted operator admission and sealed receipt",{skip:!existsSync(resolve(fixture,"manifest.json"))&&"Rust interop fixture is not present in this standalone checkout"},async()=>{const manifest=JSON.parse(await readFile(resolve(fixture,"manifest.json"),"utf8")),admissionBytes=await readFile(resolve(fixture,manifest.admissionFile)),receiptBytes=await readFile(resolve(fixture,manifest.sealedResultFile));assert.equal(`sha256-${createHash("sha256").update(admissionBytes).digest("hex")}`,manifest.admissionSha256);assert.equal(`sha256-${createHash("sha256").update(receiptBytes).digest("hex")}`,manifest.sealedResultSha256);const admission=parseOperatorAdmission(decode(admissionBytes)),receipt=decode(receiptBytes),bifrostAnchor=await loadEpiphanyOperatorTrustAnchor(resolve(fixture,manifest.bifrostRawTrustAnchorFile),{decode}),executorAnchor=await loadEpiphanyOperatorTrustAnchor(resolve(fixture,manifest.executorRawTrustAnchorFile),{decode});assert.equal(verifyPurpose(bifrostAnchor.publicKey,OPERATOR_SIGNING_PURPOSE,encode(admissionTuple(admission)),admission.providerSignature),true);assert.equal(verifySealedReceipt({receipt,admission,trustedIdentity:executorAnchor,encode}).commandId,admission.packet.commandId);assert.throws(()=>verifySealedReceipt({receipt:{...receipt,packetSha256:"sha256-hostile"},admission,trustedIdentity:executorAnchor,encode}),/not bound/);assert.equal(manifest.rudpConnectionId,0xe91f0001);assert.equal(manifest.privateStateExposed,false);});

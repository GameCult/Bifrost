import { createHash } from "node:crypto";
import { createSocket } from "node:dgram";
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import {
  operatorAdmissionType,
  legacyOperatorResultReceiptSchema,
  operatorResultReceiptType,
  parseOperatorAdmission,
  parseOperatorResultReceipt,
  resultReceiptTuple,
} from "./epiphany-operator-command-documents.mjs";
import { verifyPurpose } from "./bifrost-signed-identity.mjs";

export const EPIPHANY_RESULT_SIGNING_PURPOSE="epiphany.operator-command.sealed-result.v1";
export const LEGACY_EPIPHANY_RESULT_SIGNING_PURPOSE="epiphany.operator-command.sealed-result.v0";
const RUDP_CONNECTION_ID=0xe91f0001;

export class EpiphanyOperatorCultNetRudpTransport {
  constructor({endpoint,runtimeId="bifrost-yggdrasil",trustedEpiphanyIdentity,timeoutMs=5000,cultnetRuntime,msgpackRuntime,socketFactory=createSocket,connectionFactory}={}){
    this.endpoint=parseEndpoint(endpoint);
    this.runtimeId=required(runtimeId,"Bifrost transport runtime id");
    this.trustedIdentity=parseTrustAnchor(trustedEpiphanyIdentity);
    if(!Number.isInteger(timeoutMs)||timeoutMs<100||timeoutMs>60000)throw new Error("Epiphany operator result timeout must be 100..=60000 milliseconds.");
    this.timeoutMs=timeoutMs;
    const loaded=(!cultnetRuntime||!msgpackRuntime)?loadCultNetRuntime():{};
    this.cultnet=cultnetRuntime??loaded.cultnet;
    this.msgpack=msgpackRuntime??loaded.msgpack;
    this.socketFactory=socketFactory;
    this.connectionFactory=connectionFactory??(options=>new this.cultnet.CultNetRudpSocketTransportConnection(options));
    this.tail=Promise.resolve();
  }

  get trustedIdentityId(){return this.trustedIdentity.identityId;}
  verifyReceipt(admission,receipt){return verifySealedReceipt({receipt,admission,trustedIdentity:this.trustedIdentity,encode:this.msgpack.encode});}

  execute(admission){
    const work=this.tail.then(()=>this.#executeOne(admission));
    this.tail=work.catch(()=>undefined);
    return work;
  }

  async #executeOne(admission){
    parseOperatorAdmission(admission,{allowLegacy:true});
    const socket=this.socketFactory("udp4");
    await bindSocket(socket);
    const connection=this.connectionFactory({runtimeId:this.runtimeId,socket,mode:"client",remoteHost:this.endpoint.host,remotePort:this.endpoint.port,connectionId:RUDP_CONNECTION_ID,resendDelayMs:50,resendPollMs:10});
    try{
      connection.connect();
      await waitForConnected(connection,this.timeoutMs);
      const response=waitForReceipt(connection,this.msgpack,this.timeoutMs,admission);
      const message={schemaVersion:"cultnet.document_put_raw.v0",messageId:`operator-request-${admission.admissionId}`,document:{schemaId:operatorAdmissionType,recordKey:admission.admissionId,storedAt:admission.packet.issuedAt,payloadEncoding:"messagepack",payload:this.msgpack.encode(admission),sourceRuntimeId:admission.sourceObserverRuntimeId,sourceAgentId:admission.providerIdentityId,sourceRole:"bifrost-operator-admission",tags:["cultnet.transport.rudp.v0"]}};
      connection.send("schema",this.msgpack.encode(this.cultnet.encodeCultNetMessageForWire(message,"cultnet.schema.v0")));
      const receipt=await response;
      this.verifyReceipt(admission,receipt);
      return {providerIdentityId:receipt.providerIdentityId,result:receipt.result,receipt};
    }finally{connection.close();}
  }
}

export async function loadEpiphanyOperatorTrustAnchor(path,{decode}={}){const runtime=decode?{decode}:loadCultNetRuntime().msgpack,value=runtime.decode(await readFile(path));if(!Array.isArray(value)||value.length!==6)throw new Error("Epiphany operator trust anchor must be the raw compact 6-field MessagePack crossing artifact.");return parseTrustAnchor({schemaVersion:value[0],identityId:value[1],publicKey:value[2],assurance:value[3],identityCreatedAt:value[4],sourceIdentityRecordSha256:value[5]});}

export function verifySealedReceipt({receipt,admission,trustedIdentity,encode}){
  parseOperatorAdmission(admission,{allowLegacy:true});parseOperatorResultReceipt(receipt,{allowLegacy:true});const anchor=parseTrustAnchor(trustedIdentity);
  if(receipt.providerIdentityId!==anchor.identityId)throw new Error("Epiphany sealed result used an untrusted host identity.");
  if(receipt.commandId!==admission.packet.commandId||receipt.packetSha256!==admission.packetSha256||receipt.targetRuntimeId!==admission.packet.targetRuntimeId)throw new Error("Epiphany sealed result receipt is not bound to the admitted command.");
  if(receipt.result.commandId!==receipt.commandId||receipt.result.packetSha256!==receipt.packetSha256||receipt.result.targetRuntimeId!==receipt.targetRuntimeId||receipt.result.completedAt!==receipt.completedAt)throw new Error("Epiphany sealed result receipt substituted its embedded application result.");
  // Expiry gates first admission, not exact crash recovery. Epiphany may seal
  // an already-admitted command after its lease while recovering the one
  // idempotent consequence; Bifrost's durable ledger owns whether such a
  // post-expiry retransmission was authorized.
  const completed=Date.parse(receipt.completedAt),issued=Date.parse(admission.packet.issuedAt);
  if(completed<issued)throw new Error("Epiphany sealed result predates the admitted command.");
  const digest=`sha256-${createHash("sha256").update(encode(receipt.result)).digest("hex")}`;
  if(digest!==receipt.resultPayloadSha256)throw new Error("Epiphany sealed result payload digest is invalid.");
  const purpose=receipt.schemaVersion===legacyOperatorResultReceiptSchema?LEGACY_EPIPHANY_RESULT_SIGNING_PURPOSE:EPIPHANY_RESULT_SIGNING_PURPOSE;
  if(!verifyPurpose(anchor.publicKey,purpose,encode(resultReceiptTuple(receipt)),receipt.executorSignature))throw new Error("Epiphany sealed result signature is invalid.");
  return receipt;
}

function waitForReceipt(connection,msgpack,timeoutMs,admission){return new Promise((resolveReceipt,reject)=>{const finish=(fn,value)=>{clearTimeout(timer);connection.off?.("frame",onFrame);connection.off?.("error",onError);fn(value);};const onError=error=>finish(reject,error);const onFrame=frame=>{try{if(frame.channelId!=="schema")throw new Error("Epiphany operator response arrived on a foreign CultNet channel.");const message=msgpack.decode(frame.payload);if(!message||message.schemaVersion!=="cultnet.document_put_raw.v0")throw new Error("Epiphany operator response is not a CultNet raw typed document.");const d=message.document;if(!d||d.schemaId!==operatorResultReceiptType||d.recordKey!==admission.packet.commandId||d.payloadEncoding!=="messagepack"||d.sourceRole!=="epiphany-operator-command-executor"||d.sourceRuntimeId!==admission.packet.targetRuntimeId)throw new Error("Epiphany operator response envelope substituted result authority.");const receipt=msgpack.decode(d.payload);if(d.sourceAgentId!==receipt?.providerIdentityId)throw new Error("Epiphany operator response envelope substituted executor identity.");finish(resolveReceipt,receipt);}catch(error){finish(reject,error);}};const timer=setTimeout(()=>finish(reject,new Error("Timed out waiting for the correlated Epiphany application result.")),timeoutMs);connection.on("frame",onFrame);connection.on("error",onError);});}
async function waitForConnected(connection,timeoutMs){const started=Date.now();while(!connection.connected){if(Date.now()-started>=timeoutMs)throw new Error("Timed out establishing the Epiphany CultNet RUDP session.");await new Promise(done=>setTimeout(done,5));}}
function bindSocket(socket){return new Promise((resolveBind,reject)=>{const fail=error=>{socket.off("listening",ready);reject(error)};const ready=()=>{socket.off("error",fail);resolveBind()};socket.once("error",fail);socket.once("listening",ready);socket.bind(0,"0.0.0.0");});}
function parseEndpoint(value){required(value,"Epiphany operator RUDP endpoint");const url=new URL(value);if(url.protocol!=="rudp:"||!url.hostname||!url.port||(url.pathname&&url.pathname!=="/"))throw new Error("Epiphany operator endpoint must be rudp://host:port.");const port=Number(url.port);if(!Number.isInteger(port)||port<1||port>65535)throw new Error("Epiphany operator endpoint port is invalid.");return {host:url.hostname,port};}
function parseTrustAnchor(value){if(!value||typeof value!=="object"||Array.isArray(value))throw new Error("Trusted Epiphany host identity is required.");const keys=Object.keys(value).sort(),expected=["assurance","identityCreatedAt","identityId","publicKey","schemaVersion","sourceIdentityRecordSha256"].sort();if(keys.join("\0")!==expected.join("\0")||value.schemaVersion!=="epiphany.host_identity_trust_anchor.v0"||typeof value.identityId!=="string"||!value.assurance?.trim()||!/^sha256-[0-9a-f]{64}$/.test(value.sourceIdentityRecordSha256)||!Number.isFinite(Date.parse(value.identityCreatedAt)))throw new Error("Trusted Epiphany host identity violates its exact schema.");value.publicKey=value.publicKey instanceof Uint8Array?value.publicKey:Array.isArray(value.publicKey)&&value.publicKey.every(x=>Number.isInteger(x)&&x>=0&&x<=255)?Uint8Array.from(value.publicKey):null;if(!value.publicKey||value.publicKey.length!==32)throw new Error("Trusted Epiphany host identity public key must be 32 bytes.");const derived=createHash("sha256").update(Buffer.from("epiphany.host-incarnation.identity.v0\0")).update(value.publicKey).digest("hex");if(derived!==value.identityId)throw new Error("Trusted Epiphany host identity id does not match its public key.");return value;}
function loadCultNetRuntime(){const root=resolve(import.meta.dirname,".."),projects=resolve(root,".."),cult=resolve(process.env.VOIDBOT_CULTLIB_ROOT||resolve(projects,"CultLib")),pkg=resolve(cult,"packages","cultnet-ts","package.json");if(!existsSync(pkg))throw new Error(`CultNet TypeScript runtime is unavailable at ${pkg}.`);const requireCult=createRequire(pkg);return {cultnet:requireCult("cultnet-ts"),msgpack:requireCult("@msgpack/msgpack")};}
function required(value,label){if(typeof value!=="string"||!value.trim())throw new Error(`${label} is required.`);return value;}

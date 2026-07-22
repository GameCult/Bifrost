import dgram from "node:dgram";
import { randomUUID } from "node:crypto";
import { createRequire } from "node:module";
import { resolve } from "node:path";
import { providerHealthIdentity, signProviderHealth } from "./bifrost-feedback-identity.mjs";

const root=resolve(import.meta.dirname,".."),projects=resolve(root,".."),cult=resolve(process.env.VOIDBOT_CULTLIB_ROOT||resolve(projects,"CultLib"));
const require=createRequire(resolve(cult,"packages","cultnet-ts","package.json"));
const {encodeCultNetMessageForWire,encodeRudpPacket}=require("cultnet-ts"),{encode}=require("@msgpack/msgpack");
const protocol="cultnet.transport.rudp.v0",schema="idunn.signed_daemon_health.v1",sourceRuntimeId="bifrost-persona-feedback";
export const signedHealthSourceRole="daemon-health-publisher";
const states=new Set(["active","warming","degraded","failed"]),reasons=new Set(["ready","starting","readiness-degraded","unavailable"]);

export function createPersonaFeedbackHealthPublisher(identity,publisherIncarnationId=randomUUID()){
  if(!identity)throw new Error("Bifrost Persona-feedback provider-health identity is required.");
  if(!isUuid(publisherIncarnationId))throw new Error("Bifrost Persona-feedback publisher incarnation must be a UUID.");
  let publisherSequence=0;
  return {identity,publisherIncarnationId,next(input){return createSignedPersonaFeedbackHealth({...input,identity,publisherIncarnationId,publisherSequence:++publisherSequence});}};
}

export function createSignedPersonaFeedbackHealth(input){
  requireText(input.daemonId,"daemon id");requireText(input.healthContract,"health contract");
  if(!states.has(input.state))throw new Error(`Bifrost Persona-feedback health state is invalid: ${input.state}`);
  if(!reasons.has(input.detail))throw new Error("Bifrost Persona-feedback health detail must be a bounded generic reason.");
  if(!input.identity)throw new Error("Bifrost Persona-feedback provider-health identity is required.");
  if(!isUuid(input.publisherIncarnationId))throw new Error("Bifrost Persona-feedback publisher incarnation must be a UUID.");
  if(!Number.isSafeInteger(input.publisherSequence)||input.publisherSequence<=0)throw new Error("Bifrost Persona-feedback publisher sequence must be a positive safe integer.");
  const observed=Number(input.observedAtUnixMillis??Date.now());if(!Number.isSafeInteger(observed)||observed<=0)throw new Error("Bifrost Persona-feedback observation time must be positive Unix milliseconds.");
  // The shared CultNet contract marks signatures as portable bytes, so both
  // Rust and JavaScript lower this field as MessagePack binary.
  // Safe JavaScript numbers above u32 lower as float64. Rust's u64 lowers as
  // uint64, so retain the timestamp as BigInt on this signed wire surface.
  const provider=providerHealthIdentity(input.identity),record=[schema,input.daemonId,input.healthContract,sourceRuntimeId,input.state,input.detail,provider.identityId,input.publisherIncarnationId,input.publisherSequence,BigInt(observed),null,null,null,null,"ed25519",new Uint8Array(),false];
  const proof=signProviderHealth(input.identity,canonicalHealthEncode(record));if(proof.identityId!==provider.identityId)throw new Error("Provider-health signer identity derivation disagrees with the packet.");record[15]=new Uint8Array(proof.signature);
  return {record,payload:canonicalHealthEncode(record),providerIdentityId:provider.identityId,observedAtUnixMillis:observed};
}

function canonicalHealthEncode(value){return encode(value,{useBigInt64:true});}

export async function publishPersonaFeedbackHealth(input){
  if(!input.endpoint)throw new Error("Bifrost Persona-feedback Idunn health endpoint is required.");
  if(!input.publisher||typeof input.publisher.next!=="function")throw new Error("Bifrost Persona-feedback process-scoped health publisher is required.");
  const endpoint=parseEndpoint(input.endpoint),signed=input.publisher.next(input),socket=dgram.createSocket(endpoint.host.includes(":")?"udp6":"udp4");
  try{
    await new Promise((ok,fail)=>{socket.once("error",fail);socket.bind(0,endpoint.host.includes(":")?"::":"0.0.0.0",()=>{socket.off("error",fail);ok();});});
    await send(socket,endpoint,packet("connect",1,new Uint8Array(),"control"));await delay(300);
    const incarnation=signed.record[7],sequence=signed.record[8],storedAt=new Date(signed.observedAtUnixMillis).toISOString(),signedMessage=rawPut(`bifrost-feedback-signed-health:${incarnation}:${sequence}`,"idunn.signed_daemon_health",input.daemonId,storedAt,signed.payload,signedHealthSourceRole);
    await send(socket,endpoint,packet("data",2,encode(encodeCultNetMessageForWire(signedMessage,"cultnet.schema.v0")),"schema"));
    if(input.publishUnsignedDiagnostic===true){
      const diagnosticPayload=encode([input.daemonId,input.state,input.detail,storedAt,input.healthContract,"diagnostic-only",protocol]),diagnosticMessage=rawPut(`bifrost-feedback-unsigned-diagnostic:${incarnation}:${sequence}`,"idunn.daemon_health",input.daemonId,storedAt,diagnosticPayload,"unsigned-health-diagnostic");
      await send(socket,endpoint,packet("data",3,encode(encodeCultNetMessageForWire(diagnosticMessage,"cultnet.schema.v0")),"schema"));
    }
    await delay(1000);
  }finally{socket.close();}
}

function rawPut(messageId,schemaId,recordKey,storedAt,payload,sourceRole){return {schemaVersion:"cultnet.document_put_raw.v0",messageId,document:{schemaId,recordKey,storedAt,payloadEncoding:"messagepack",payload,sourceRuntimeId,sourceRole,tags:[protocol]}};}
export function parseEndpoint(value){const text=String(value||"").trim(),ipv6=text.match(/^\[([^\]]+)\]:(\d+)$/);if(ipv6)return {host:ipv6[1],port:port(ipv6[2])};const split=text.lastIndexOf(":");if(split<=0)throw new Error(`Idunn RUDP endpoint must be host:port, got "${value}".`);return {host:text.slice(0,split),port:port(text.slice(split+1))};}
function port(value){const result=Number(value);if(!Number.isInteger(result)||result<=0||result>65535)throw new Error(`Idunn RUDP endpoint port is invalid: ${value}`);return result;}
function packet(packetType,sequence,payload,channelId){return {packetType,connectionId:0x1d0d0001,sequence,ack:0,ackMask:0,channelId,reliable:true,ordered:true,sequenced:false,payload};}
async function send(socket,endpoint,value){const wire=encodeRudpPacket(value);await new Promise((ok,fail)=>socket.send(wire,endpoint.port,endpoint.host,error=>error?fail(error):ok()));}
function delay(ms){return new Promise(ok=>setTimeout(ok,ms));}
function requireText(value,label){if(typeof value!=="string"||!value.trim()||value.length>256||/[\u0000-\u001f\u007f]/.test(value))throw new Error(`Bifrost Persona-feedback ${label} is invalid.`);}
function isUuid(value){return typeof value==="string"&&/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);}

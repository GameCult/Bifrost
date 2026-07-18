import dgram from "node:dgram";
import { createRequire } from "node:module";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const projects = resolve(root, "..");
const cult = resolve(process.env.VOIDBOT_CULTLIB_ROOT || resolve(projects, "CultLib"));
const require = createRequire(resolve(cult, "packages", "cultnet-ts", "package.json"));
const { encodeCultNetMessageForWire, encodeRudpPacket } = require("cultnet-ts");
const { encode } = require("@msgpack/msgpack");
const protocol = "cultnet.transport.rudp.v0";

export async function publishPersonaFeedbackHealth(input) {
  if (!input.endpoint) throw new Error("Bifrost Persona-feedback Idunn health endpoint is required.");
  const endpoint = parseEndpoint(input.endpoint);
  const socket = dgram.createSocket(endpoint.host.includes(":") ? "udp6" : "udp4");
  try {
    await new Promise((ok, fail) => { socket.once("error", fail); socket.bind(0, endpoint.host.includes(":") ? "::" : "0.0.0.0", () => { socket.off("error", fail); ok(); }); });
    await send(socket, endpoint, packet("connect", 1, new Uint8Array(), "control"));
    await delay(300);
    const observedAt = input.observedAt || new Date().toISOString();
    const payload = encode([input.daemonId, input.state, input.detail, observedAt, input.healthContract, "daemon-published", protocol]);
    const message = {schemaVersion:"cultnet.document_put_raw.v0",messageId:`bifrost-feedback-health:${observedAt.replace(/[:.]/g,"-")}`,document:{schemaId:"idunn.daemon_health",recordKey:input.daemonId,storedAt:observedAt,payloadEncoding:"messagepack",payload,sourceRuntimeId:"bifrost-persona-feedback",sourceRole:"daemon-health-publisher",tags:[protocol]}};
    await send(socket, endpoint, packet("data", 2, encode(encodeCultNetMessageForWire(message, "cultnet.schema.v0")), "schema"));
    await delay(1000);
  } finally { socket.close(); }
}

export function parseEndpoint(value) {
  const text=String(value||"").trim(), ipv6=text.match(/^\[([^\]]+)\]:(\d+)$/);
  if(ipv6)return {host:ipv6[1],port:port(ipv6[2])};
  const split=text.lastIndexOf(":");if(split<=0)throw new Error(`Idunn RUDP endpoint must be host:port, got "${value}".`);
  return {host:text.slice(0,split),port:port(text.slice(split+1))};
}
function port(value){const result=Number(value);if(!Number.isInteger(result)||result<=0||result>65535)throw new Error(`Idunn RUDP endpoint port is invalid: ${value}`);return result;}
function packet(packetType,sequence,payload,channelId){return {packetType,connectionId:0x1d0d0001,sequence,ack:0,ackMask:0,channelId,reliable:true,ordered:true,sequenced:false,payload};}
async function send(socket,endpoint,value){const wire=encodeRudpPacket(value);await new Promise((ok,fail)=>socket.send(wire,endpoint.port,endpoint.host,error=>error?fail(error):ok()));}
function delay(ms){return new Promise(ok=>setTimeout(ok,ms));}

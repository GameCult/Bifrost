import {createSocket} from "node:dgram";
import {parseRequest,receiptDocumentTuple} from "./persona-discord-delivery-documents.mjs";

export const personaDiscordDeliveryConnectionId=0xe91f0003;

export async function startPersonaDiscordDeliveryRudpServer({runtime,endpoint,runtimeId="bifrost-discord-yggdrasil",onRequest,socketFactory=createSocket,connectionFactory}){
  const {host,port}=parseEndpoint(endpoint),socket=socketFactory("udp4");
  await bind(socket,host,port);
  const connection=(connectionFactory??(options=>new runtime.cultnet.CultNetRudpSocketTransportConnection(options)))({runtimeId,socket,mode:"server",connectionId:personaDiscordDeliveryConnectionId,initialSequence:100,resendDelayMs:50,resendPollMs:10});
  let busy=false;const diagnostics={datagrams:0,frames:0,responses:0,errors:[]};socket.on("message",()=>diagnostics.datagrams++);
  const frame=async value=>{
    diagnostics.frames++;
    if(busy)return;
    busy=true;
    try{
      if(value.channelId!=="schema")throw new Error("Persona delivery request arrived on a foreign channel");
      const message=runtime.cultnet.parseCultNetMessage(runtime.msgpack.decode(value.payload),"cultnet.schema.v0"),document=message.document;
      if(message.schemaVersion!=="cultnet.document_put_raw.v0"||document.schemaId!=="epiphany.persona_discord_delivery_request.v0"||document.payloadEncoding!=="messagepack"||document.sourceRole!=="epiphany-persona-mouth"||document.sourceRuntimeId!=="epiphany-starfire"||!Array.isArray(document.tags)||document.tags.length!==1||document.tags[0]!=="cultnet.transport.rudp.v0")throw new Error("Persona delivery request envelope substituted authority");
      const request=parseRequest(runtime.msgpack.decode(document.payload));
      if(document.recordKey!==request?.requestId||document.sourceAgentId!==request?.signerIdentityId||request?.targetRuntimeId!=="epiphany-starfire")throw new Error("Persona delivery request envelope is not bound to its signed payload");
      const receipt=await onRequest(request);
      const response={schemaVersion:"cultnet.document_put_raw.v0",messageId:`persona-delivery-receipt-${receipt.requestId}`,document:{schemaId:"bifrost.persona_discord_delivery_receipt.v0",recordKey:receipt.requestId,storedAt:receipt.completedAt,payloadEncoding:"messagepack",payload:runtime.msgpack.encode(receiptDocumentTuple(receipt)),sourceRuntimeId:runtimeId,sourceAgentId:receipt.providerIdentityId,sourceRole:"bifrost-persona-discord-delivery",tags:["cultnet.transport.rudp.v0"]}};
      connection.send("schema",runtime.msgpack.encode(runtime.cultnet.encodeCultNetMessageForWire(response,"cultnet.schema.v0")));diagnostics.responses++;
    }catch(error){const message=error instanceof Error?error.message:String(error);diagnostics.errors.push(message);console.error(`Bifrost Persona delivery RUDP rejected frame: ${message}`);}finally{busy=false;}
  };
  connection.on("frame",frame);
  return {address:socket.address(),diagnostics,close(){connection.off?.("frame",frame);connection.close();}};
}

function parseEndpoint(value){const url=new URL(value);if(url.protocol!=="rudp:"||!url.hostname||!url.port)throw new Error("Persona delivery listener must be rudp://host:port");return {host:url.hostname,port:Number(url.port)};}
function bind(socket,host,port){return new Promise((resolve,reject)=>{socket.once("error",reject);socket.once("listening",resolve);socket.bind(port,host);});}

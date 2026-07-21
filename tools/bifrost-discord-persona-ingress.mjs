import { createHash } from "node:crypto";

const GATEWAY_VERSION = 10;
const GATEWAY_INTENTS = (1 << 0) | (1 << 9) | (1 << 15);

export function conversationFromDiscordMessage(message, botUserId) {
  if (!message || typeof message !== "object" || message.author?.bot === true) return null;
  const guildId = text(message.guild_id);
  const channelId = text(message.channel_id);
  const messageId = text(message.id);
  const authorId = text(message.author?.id);
  const observedAt = Date.parse(String(message.timestamp ?? ""));
  if (!guildId || !channelId || !messageId || !authorId || !botUserId || !Number.isFinite(observedAt)) return null;
  const mentioned = Array.isArray(message.mentions)
    && message.mentions.some((mention) => String(mention?.id ?? "") === botUserId);
  const replied = String(message.referenced_message?.author?.id ?? "") === botUserId;
  if (!mentioned && !replied) return null;
  const content = String(message.content ?? "")
    .replace(new RegExp(`<@!?${escapeRegex(botUserId)}>`, "g"), " ")
    .replace(/\s+/g, " ")
    .trim();
  if (!content || Buffer.byteLength(content, "utf8") > 1200) return null;
  return {
    guildId,
    channelId,
    messageId,
    authorId,
    authorName: text(message.member?.nick) || text(message.author?.global_name) || text(message.author?.username) || authorId,
    content,
    addressingMode: mentioned ? "text" : "reply",
    observedAt: new Date(observedAt).toISOString(),
    payloadHash: createHash("sha256").update(content, "utf8").digest("hex"),
  };
}

export function discordGatewayReady(state, nowMillis = Date.now(), maxAckAgeMillis = 120000) {
  return state?.connected === true
    && text(state.sessionId) !== ""
    && Number.isFinite(state.lastHeartbeatAckAtMillis)
    && state.lastHeartbeatAckAtMillis > 0
    && nowMillis >= state.lastHeartbeatAckAtMillis
    && nowMillis - state.lastHeartbeatAckAtMillis <= maxAckAgeMillis;
}

export async function serveDiscordPersonaIngress({ token, runtimeId, onConversation, onReady = () => {}, onDisconnect = () => {}, onState = () => {} }) {
  if (!text(token)) throw new Error("Bifrost Discord Persona ingress requires a bot token.");
  if (!text(runtimeId)) throw new Error("Bifrost Discord Persona ingress requires a runtime id.");
  if (typeof onConversation !== "function") throw new Error("Bifrost Discord Persona ingress requires a conversation sink.");
  onState({connected:false,sessionId:"",lastHeartbeatAckAtMillis:0,phase:"discovering"});
  const gateway = await discordJson("https://discord.com/api/v10/gateway/bot", token);
  if (!text(gateway?.url)) throw new Error("Discord gateway discovery returned no URL.");
  onState({connected:false,sessionId:"",lastHeartbeatAckAtMillis:0,phase:"discovered"});
  let sequence = null, sessionId = "", resumeUrl = gateway.url, botUserId = "", stopped = false;
  while (!stopped) {
    try {
      const result = await gatewaySession({ token, runtimeId, url: resumeUrl, sequence, sessionId, botUserId, onConversation, onReady, onState });
      sequence = result.sequence; sessionId = result.sessionId; resumeUrl = result.resumeUrl; botUserId = result.botUserId;
      stopped = result.stopped;
    } catch (error) {
      if (error?.fatal === true) throw error;
      onState({connected:false,sessionId,botUserId,lastHeartbeatAckAtMillis:0,phase:"disconnected"});
      onDisconnect(error);
      await delay(2000);
    }
  }
}

async function gatewaySession(state) {
  const socket = new WebSocket(`${state.url}/?v=${GATEWAY_VERSION}&encoding=json`);
  let sequence=state.sequence,sessionId=state.sessionId,resumeUrl=state.url,botUserId=state.botUserId,heartbeatTimer=null,heartbeatAck=true,stopped=false;
  await new Promise((resolve, reject) => {
    const cleanup=()=>{if(heartbeatTimer)clearInterval(heartbeatTimer);};
    socket.addEventListener("error",()=>{cleanup();reject(new Error("Discord gateway socket failed."));},{once:true});
    socket.addEventListener("close",(event)=>{cleanup();state.onState({connected:false,sessionId,botUserId,lastHeartbeatAckAtMillis:0,phase:"disconnected"});if([4004,4013,4014].includes(event.code)){stopped=true;const error=new Error(`Discord permanently rejected the Bifrost gateway session (close ${event.code}).`);error.fatal=true;reject(error);return;}resolve();},{once:true});
    socket.addEventListener("message",async event=>{
      try {
        const frame=JSON.parse(String(event.data));
        if(Number.isInteger(frame.s))sequence=frame.s;
        if(frame.op===10){
          const interval=Number(frame.d?.heartbeat_interval);if(!Number.isFinite(interval)||interval<1000)throw new Error("Discord gateway supplied an invalid heartbeat interval.");
          const heartbeat=()=>{if(!heartbeatAck){socket.close(4000,"heartbeat timeout");return;}heartbeatAck=false;socket.send(JSON.stringify({op:1,d:sequence}));};
          heartbeatTimer=setInterval(heartbeat,interval);heartbeat();
          if(sessionId&&sequence!==null)socket.send(JSON.stringify({op:6,d:{token:state.token,session_id:sessionId,seq:sequence}}));
          else socket.send(JSON.stringify({op:2,d:{token:state.token,intents:GATEWAY_INTENTS,properties:{os:process.platform,browser:"bifrost",device:"bifrost"}}}));
        } else if(frame.op===11){heartbeatAck=true;state.onState({connected:true,sessionId,botUserId,lastHeartbeatAckAtMillis:Date.now(),phase:"heartbeat-acknowledged"});}
        else if(frame.op===7)socket.close(4000,"server reconnect");
        else if(frame.op===9){sessionId="";sequence=null;socket.close(4000,"invalid session");}
        else if(frame.op===0&&frame.t==="READY"){
          sessionId=text(frame.d?.session_id);resumeUrl=text(frame.d?.resume_gateway_url)||state.url;botUserId=text(frame.d?.user?.id);if(!sessionId||!botUserId)throw new Error("Discord READY omitted session or bot identity.");state.onState({connected:true,sessionId,botUserId,lastHeartbeatAckAtMillis:0,phase:"ready-awaiting-heartbeat-ack"});await state.onReady({runtimeId:state.runtimeId,botUserId,sessionId});
        } else if(frame.op===0&&frame.t==="RESUMED"){state.onState({connected:true,sessionId,botUserId,lastHeartbeatAckAtMillis:0,phase:"resumed-awaiting-heartbeat-ack"});await state.onReady({runtimeId:state.runtimeId,botUserId,sessionId});}
        else if(frame.op===0&&frame.t==="MESSAGE_CREATE"){
          const conversation=conversationFromDiscordMessage(frame.d,botUserId);if(conversation)await state.onConversation(conversation);
        }
      } catch(error){socket.close(4000,"ingress failure");reject(error);}
    });
  });
  return {sequence,sessionId,resumeUrl,botUserId,stopped};
}

async function discordJson(url, token) {
  const response=await fetch(url,{headers:{authorization:`Bot ${token}`},redirect:"error",signal:AbortSignal.timeout(15000)});
  if(!response.ok)throw new Error(`Discord gateway discovery failed with HTTP ${response.status}.`);
  return response.json();
}
function text(value){return typeof value==="string"?value.trim():"";}
function escapeRegex(value){return value.replace(/[.*+?^${}()|[\]\\]/g,"\\$&");}
function delay(ms){return new Promise(resolve=>setTimeout(resolve,ms));}

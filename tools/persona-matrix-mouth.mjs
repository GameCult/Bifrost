// The Matrix ear and mouth of one Persona. One process per Persona.
//
// Ear: /sync the configured rooms; every message from another sender becomes a
// delivery kept in memory. The /sync position (next_batch) is persisted only when
// an ack covers it, so a restart before the ack re-delivers instead of losing.
// Mouth: a send request becomes one PUT with txnId = request_id (a retry is
// idempotent) and only while the instance brake is released.
// Ring: the first delivery of an idle window starts the Persona's one-shot unit.
// Starting is a request; the unit's own gate decides.
//
// The Matrix access token is read here, from a systemd credential, and nowhere
// else. It is never put in a document, an environment passed to a child, or a log.
//
// Everything outside this process is a port (fetch, state, brake, ring, clock), so
// the core runs against a fake homeserver with no CultLib and no daemon.
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { createSocket } from "node:dgram";
import { resolve, join } from "node:path";
import { pathToFileURL } from "node:url";
import * as docs from "./persona-chat-documents.mjs";

const API = "/_matrix/client/v3";
const BRAKE_TYPE = "eureka.control.brake.v1";
const CONTEXT = 20;
const RING_WINDOW_MS = 5 * 60 * 1000;

export function createMouth({
  persona, homeserver, rooms, token, fetch: httpFetch = globalThis.fetch, state, brake, ring,
  now = Date.now, log = () => {}, syncTimeoutMs = 30000,
}) {
  if (!persona || !homeserver || !token || !rooms?.length) throw new Error("persona, homeserver, rooms and token are required");
  const say = message => log(redact(String(message), token));
  const roomIdByAlias = new Map();
  const aliasByRoomId = new Map();
  const history = new Map(); // room_id -> last CONTEXT messages, oldest first
  const displayNames = new Map();
  let selfId = null;
  let cursor = null; // the /sync position we are reading from
  let seqCounter = 0;
  let pending = []; // deliveries not yet acked
  let batches = []; // { token, maxSeq }: positions awaiting an ack
  let committed = null; // the position that is persisted
  let lastRingAt = -Infinity;
  let started = false;

  async function matrix(method, path, body) {
    let response;
    try {
      response = await httpFetch(`${homeserver}${API}${path}`, {
        method,
        headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch (error) {
      throw new Error(`homeserver unreachable: ${redact(error?.message ?? String(error), token)}`);
    }
    if (!response.ok) throw new Error(`homeserver answered ${response.status} for ${method} ${path.split("?")[0]}`);
    return response.json();
  }

  async function start() {
    const saved = await state.load();
    cursor = committed = saved?.nextBatch ?? null;
    seqCounter = saved?.seq ?? 0;
    selfId = (await matrix("GET", "/account/whoami")).user_id;
    for (const alias of rooms) {
      const { room_id: roomId } = await matrix("GET", `/directory/room/${encodeURIComponent(alias)}`);
      await matrix("POST", `/join/${encodeURIComponent(roomId)}`, {});
      roomIdByAlias.set(alias, roomId);
      aliasByRoomId.set(roomId, alias);
    }
    started = true;
  }

  // One /sync round. Returns the deliveries it made.
  async function syncOnce() {
    if (!started) await start();
    const filter = encodeURIComponent(JSON.stringify({ room: { timeline: { limit: CONTEXT } } }));
    const since = cursor === null ? "" : `&since=${encodeURIComponent(cursor)}`;
    const reply = await matrix("GET", `/sync?timeout=${cursor === null ? 0 : syncTimeoutMs}&filter=${filter}${since}`);
    const baseline = cursor === null; // first ever sync: history is context, not mail
    const made = [];
    for (const [roomId, room] of Object.entries(reply.rooms?.join ?? {})) {
      const alias = aliasByRoomId.get(roomId);
      if (!alias) continue;
      for (const event of [...(room.state?.events ?? []), ...(room.timeline?.events ?? [])]) {
        if (event.type === "m.room.member" && event.content?.displayname) displayNames.set(event.state_key, event.content.displayname);
      }
      for (const event of room.timeline?.events ?? []) {
        if (event.type !== "m.room.message" || typeof event.content?.body !== "string") continue;
        const entry = { sender: event.sender, text: event.content.body, atUnixMs: event.origin_server_ts ?? now() };
        const context = (history.get(roomId) ?? []).slice(-CONTEXT);
        if (!baseline && event.sender !== selfId) {
          made.push({
            seq: ++seqCounter, roomAlias: alias, roomId, eventId: event.event_id, sender: event.sender,
            senderDisplay: displayNames.get(event.sender) ?? localpart(event.sender), text: entry.text, atUnixMs: entry.atUnixMs,
            context: context.map(c => ({ ...c })),
          });
        }
        history.set(roomId, [...context, entry].slice(-CONTEXT));
      }
    }
    const wasIdle = pending.length === 0;
    pending.push(...made);
    cursor = reply.next_batch;
    batches.push({ token: reply.next_batch, maxSeq: seqCounter });
    await commit();
    if (made.length > 0) await state.save({ nextBatch: committed, seq: seqCounter }); // sequence numbers never repeat across a restart
    if (made.length > 0 && (wasIdle || now() - lastRingAt >= RING_WINDOW_MS)) {
      lastRingAt = now();
      Promise.resolve().then(ring).catch(error => say(`ring failed: ${error?.message ?? error}`));
    }
    return made;
  }

  // The persisted position is the newest batch whose deliveries are all acked.
  async function commit(throughSeq = pending.length ? pending[0].seq - 1 : seqCounter) {
    let covered = null;
    for (const b of batches) { if (b.maxSeq <= throughSeq) covered = b; else break; }
    if (covered && covered.token !== committed) {
      committed = covered.token;
      await state.save({ nextBatch: committed, seq: seqCounter });
    }
    batches = batches.filter(b => b.maxSeq > throughSeq);
  }

  async function ack(throughSeq) {
    pending = pending.filter(d => d.seq > throughSeq);
    await commit(pending.length ? pending[0].seq - 1 : seqCounter);
  }

  function pendingAfter(afterSeq) {
    return pending.filter(d => d.seq > afterSeq).map(d => ({ ...d }));
  }

  async function send({ requestId, persona: asked, roomAlias, text }) {
    const refuse = refusal => ({ requestId, eventId: null, refusal });
    if (asked !== persona) return refuse("PersonaMismatch");
    let released = false;
    try { released = (await brake()) === true; } catch { released = false; }
    if (!released) return refuse("Brake");
    if (typeof text !== "string" || text.trim() === "") return refuse("EmptyText");
    if (!started) await start();
    const roomId = roomIdByAlias.get(roomAlias);
    if (!roomId) return refuse("UnknownRoom");
    try {
      const sent = await matrix("PUT", `/rooms/${encodeURIComponent(roomId)}/send/m.room.message/${encodeURIComponent(requestId)}`, { msgtype: "m.text", body: text });
      return { requestId, eventId: sent.event_id, refusal: null };
    } catch (error) {
      say(`send failed: ${error.message}`);
      return refuse("SendFailed");
    }
  }

  async function run(signal) {
    while (!signal?.aborted) {
      try { await syncOnce(); } catch (error) {
        say(`sync failed: ${error.message}`);
        await new Promise(r => { const t = setTimeout(r, 5000); signal?.addEventListener("abort", () => { clearTimeout(t); r(); }, { once: true }); });
      }
    }
  }

  return { start, syncOnce, ack, pendingAfter, send, run, get pendingCount() { return pending.length; } };
}

export function redact(message, token) {
  return token ? message.split(token).join("[redacted]") : message;
}

function localpart(userId) {
  return userId.replace(/^@/, "").split(":")[0];
}

// Ring: start the Persona's one-shot unit. No retry loop, no token in its environment.
export function createRing({ instance, run = execFile, log = () => {} }) {
  return () => new Promise(done => {
    run("systemctl", ["start", "--no-block", `mind-persona@${instance}.service`], { env: { PATH: process.env.PATH ?? "" } }, error => {
      if (error) log(`ring failed: ${error.message}`);
      done();
    });
  });
}

// Brake: released only when the instance's control store decodes to a released
// brake. Absent, undecodable or held reads are all "not released".
export function createBrakeReader({ controlPath, readStore, decode }) {
  return async () => {
    try {
      const read = readStore(controlPath);
      if (read.absent) return false;
      const envelope = read.envelopes.find(e => e.type === BRAKE_TYPE && e.key === "brake");
      if (!envelope) return false;
      const value = decode(new Uint8Array(envelope.payload));
      const released = Array.isArray(value) ? value[0] : value instanceof Map ? value.get(0) ?? value.get("released") : value?.[0] ?? value?.["0"] ?? value?.released;
      return released === true;
    } catch {
      return false;
    }
  };
}

// The RUDP connection id is derived from the credential, so a peer without it cannot open the session.
export function connectionIdFor(connectionKey) {
  return createHash("sha256").update(connectionKey).digest().readUInt32BE(0);
}

// CultNet RUDP endpoint on loopback. Requests arrive as document_put_raw messages.
export async function startChatEndpoint({ mouth, persona, runtime, port, connectionKey, runtimeId, socketFactory = createSocket, host = "127.0.0.1", log = () => {} }) {
  if (!/^127\.\d+\.\d+\.\d+$/.test(host)) throw new Error("the chat endpoint listens on loopback only");
  const { msgpack, cultnet } = runtime;
  const socket = socketFactory("udp4");
  await new Promise((ok, fail) => { socket.once("error", fail); socket.once("listening", ok); socket.bind(port, host); });
  const connectionId = connectionIdFor(connectionKey);
  const connection = new cultnet.CultNetRudpSocketTransportConnection({ runtimeId, socket, mode: "server", connectionId, initialSequence: 100, resendDelayMs: 50, resendPollMs: 10, maxFragmentBytes: 1200 });
  const respond = (kind, recordKey, tuple) => connection.send("schema", msgpack.encode(cultnet.encodeCultNetMessageForWire(
    docs.envelope({ kind, recordKey, payload: msgpack.encode(tuple), sourceRuntimeId: runtimeId, sourceAgentId: persona, sourceRole: "bifrost-persona-matrix-mouth" }), "cultnet.schema.v0")));
  let busy = Promise.resolve();
  const handle = async value => {
    try {
      if (value.channelId !== "schema") throw new Error("chat request arrived on a foreign channel");
      const message = cultnet.parseCultNetMessage(msgpack.decode(value.payload), "cultnet.schema.v0");
      const document = docs.checkEnvelope(message, [docs.kinds.pendingRequest, docs.kinds.ack, docs.kinds.sendRequest]);
      const tuple = msgpack.decode(document.payload);
      if (document.schemaId === docs.kinds.pendingRequest) {
        const request = docs.parsePendingRequest(tuple);
        if (request.persona !== persona) throw new Error("chat request names another Persona");
        respond(docs.kinds.pending, persona, docs.pendingTuple({ persona, deliveries: mouth.pendingAfter(request.afterSeq) }));
      } else if (document.schemaId === docs.kinds.ack) {
        const request = docs.parseAck(tuple);
        if (request.persona !== persona) throw new Error("chat ack names another Persona");
        await mouth.ack(request.throughSeq);
      } else {
        const request = docs.parseSendRequest(tuple);
        const receipt = await mouth.send(request);
        respond(docs.kinds.sendReceipt, receipt.requestId, docs.sendReceiptTuple(receipt));
      }
    } catch (error) {
      log(`chat endpoint rejected a frame: ${error?.message ?? error}`);
    }
  };
  const frame = value => { busy = busy.then(() => handle(value)); };
  connection.on("frame", frame);
  return { address: socket.address(), close() { connection.off?.("frame", frame); connection.close(); } };
}

export function cultcacheState({ runtime, path }) {
  const def = runtime.defineDocumentType({
    type: "gamecult.persona.chat_cursor.v1", schemaName: "gamecult.persona.chat_cursor.v1", schemaId: "gamecult.persona.chat_cursor.v1",
    schemaVersion: "gamecult.persona.chat_cursor.v1", contentHash: "gamecult.persona.chat_cursor.v1", global: false, name: "key",
    schema: { parse: v => ({ key: String(v.key), nextBatch: v.nextBatch ?? null, seq: Number(v.seq) }) },
  });
  const cache = runtime.CultCache.builder().withDocumentType(def).withGenericStore(new runtime.SingleFileMessagePackBackingStore(path)).build();
  const unwrap = v => (Array.isArray(v) ? v[0] : v?.value ?? v);
  return {
    async load() { await cache.pullAllBackingStores(); return unwrap(cache.get(def, "cursor")) ?? null; },
    async save({ nextBatch, seq }) { await cache.put(def, "cursor", { key: "cursor", nextBatch, seq }); },
  };
}

export function loadRuntime(cultRoot) {
  const at = name => createRequire(resolve(cultRoot, "packages", name, "package.json"));
  const cc = at("cultcache-ts")("@gamecult/cultcache-ts"), cn = at("cultnet-ts")("cultnet-ts"), mp = at("cultnet-ts")("@msgpack/msgpack");
  return { CultCache: cc.CultCache, SingleFileMessagePackBackingStore: cc.SingleFileMessagePackBackingStore, defineDocumentType: cc.defineDocumentType, readSingleFileStore: cc.readSingleFileStore, cultnet: cn, msgpack: mp };
}

function required(env, name) {
  if (!env[name]) throw new Error(`${name} is required`);
  return env[name];
}
function credential(env, name) {
  return readFileSync(join(required(env, "CREDENTIALS_DIRECTORY"), name), "utf8").trim();
}

export async function main(env = process.env) {
  const persona = required(env, "PERSONA");
  const instance = env.MIND_INSTANCE ?? persona; // the unit template is instanced by the Persona's mind
  if (!/^[A-Za-z0-9_-]{1,64}$/.test(instance)) throw new Error("MIND_INSTANCE is not a slug");
  const token = credential(env, "matrix-token");
  const runtime = loadRuntime(resolve(env.CULTLIB_ROOT ?? resolve(import.meta.dirname, "..", "..", "CultLib")));
  const log = line => console.error(line);
  const controlPath = join(env.CONTROL_ROOT ?? "/etc/gamecult/minds", instance, "control.cc");
  const mouth = createMouth({
    persona, token, log,
    homeserver: required(env, "MATRIX_HOMESERVER").replace(/\/$/, ""),
    rooms: required(env, "MATRIX_ROOMS").split(",").map(s => s.trim()).filter(Boolean),
    state: cultcacheState({ runtime, path: env.STATE_PATH ?? join(required(env, "STATE_DIRECTORY"), "cursor.cc") }),
    brake: createBrakeReader({ controlPath, readStore: runtime.readSingleFileStore, decode: runtime.msgpack.decode }),
    ring: createRing({ instance, log }),
  });
  await mouth.start();
  const listen = required(env, "CHAT_RUDP_LISTEN"); // host:port, loopback
  const colon = listen.lastIndexOf(":");
  await startChatEndpoint({ mouth, persona, runtime, host: listen.slice(0, colon), port: Number(listen.slice(colon + 1)), connectionKey: credential(env, "chat-key"), runtimeId: `bifrost-persona-mouth-${persona}`, log });
  await mouth.run();
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(error?.message ?? String(error)); process.exitCode = 1; });
}

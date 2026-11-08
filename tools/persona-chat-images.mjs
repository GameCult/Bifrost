// Chat images for the Matrix mouth: fetch one m.image attachment from the Persona's own homeserver, decide what it
// is from its bytes alone, and keep it in a per-Persona store the Persona's reader opens by reference.
//
// Nothing in the bytes or in the event is trusted. The type is the magic bytes (the declared mime and the filename
// are never read), the size and pixel counts are bounded before anything decodes the image, and every name on disk is
// generated here from hashes. Refusals are a fixed word list so a refusal can be printed without printing sender text.
import { createHash, randomBytes } from "node:crypto";
import { chmodSync, closeSync, fsyncSync, mkdirSync, openSync, readdirSync, renameSync, statSync, unlinkSync, writeSync } from "node:fs";
import { join } from "node:path";

export const MAX_BYTES = 5 * 1024 * 1024;
export const MAX_PIXELS = 40_000_000;
export const ROOM_CAP_BYTES = 50 * 1024 * 1024;
export const RETAIN_MS = 24 * 60 * 60 * 1000;
export const FILE_MODE = 0o640;
export const DIR_MODE = 0o2750; // setgid is kept: the group of the Persona's directory is inherited by what lands in it
const FETCH_TIMEOUT_MS = 30000;

// Why an image was not stored. Printed in the delivery's text line, so only these words ever are.
export const refusals = Object.freeze(["encrypted", "no-url", "fetch-failed", "too-big", "wrong-type", "bad-header", "too-many-pixels", "store-failed"]);

const MXC = /^mxc:\/\/([A-Za-z0-9.:_-]+)\/([A-Za-z0-9_-]+)$/;
const HEX64 = /^[0-9a-f]{64}$/;
export const IMAGE_PATH = /^[0-9a-f]{64}\/[0-9a-f]{64}(\.[a-z0-9]{1,5})?$/;
const EXT = { "image/png": "png", "image/jpeg": "jpg", "image/gif": "gif", "image/webp": "webp" };
export const MIMES = Object.freeze(Object.keys(EXT));
export const extensionOf = mime => EXT[mime];

const u16le = (b, o) => b[o] | (b[o + 1] << 8);
const u16be = (b, o) => (b[o] << 8) | b[o + 1];
const u32be = (b, o) => b.readUInt32BE(o);
const ascii = (b, from, to) => b.toString("latin1", from, to);

// The type and the pixel dimensions from the header alone, never decoding. null when the bytes are none of the four
// types; { mime, width: 0, height: 0 } when the type is known and the header will not parse.
export function sniff(b) {
  if (b.length >= 8 && b.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) {
    const ok = b.length >= 24 && ascii(b, 12, 16) === "IHDR";
    return { mime: "image/png", width: ok ? u32be(b, 16) : 0, height: ok ? u32be(b, 20) : 0 };
  }
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return { mime: "image/jpeg", ...jpegSize(b) };
  if (b.length >= 6 && (ascii(b, 0, 6) === "GIF87a" || ascii(b, 0, 6) === "GIF89a")) {
    return { mime: "image/gif", width: b.length >= 10 ? u16le(b, 6) : 0, height: b.length >= 10 ? u16le(b, 8) : 0 };
  }
  if (b.length >= 12 && ascii(b, 0, 4) === "RIFF" && ascii(b, 8, 12) === "WEBP") return { mime: "image/webp", ...webpSize(b) };
  return null;
}

function jpegSize(b) {
  let i = 2;
  while (i + 4 <= b.length) {
    if (b[i] !== 0xff) return { width: 0, height: 0 };
    while (b[i] === 0xff && i + 1 < b.length) i++; // fill bytes
    const marker = b[i++];
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd9)) continue; // no length
    if (i + 2 > b.length) break;
    const length = u16be(b, i);
    const frame = marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc;
    if (frame) return i + 7 <= b.length ? { height: u16be(b, i + 3), width: u16be(b, i + 5) } : { width: 0, height: 0 };
    if (marker === 0xda || length < 2) break; // scan data before any frame header: unparsable
    i += length;
  }
  return { width: 0, height: 0 };
}

function webpSize(b) {
  const bad = { width: 0, height: 0 };
  const chunk = ascii(b, 12, 16);
  if (chunk === "VP8 ") return b.length >= 30 && b[23] === 0x9d && b[24] === 0x01 && b[25] === 0x2a ? { width: u16le(b, 26) & 0x3fff, height: u16le(b, 28) & 0x3fff } : bad;
  if (chunk === "VP8L") {
    if (b.length < 25 || b[20] !== 0x2f) return bad;
    return { width: 1 + (b[21] | ((b[22] & 0x3f) << 8)), height: 1 + ((b[22] >> 6) | (b[23] << 2) | ((b[24] & 0x0f) << 10)) };
  }
  if (chunk === "VP8X") return b.length >= 30 ? { width: 1 + (b[24] | (b[25] << 8) | (b[26] << 16)), height: 1 + (b[27] | (b[28] << 8) | (b[29] << 16)) } : bad;
  return bad;
}

// The checked type of `bytes`: { mime, ext } or { refusal }.
export function classify(bytes) {
  const found = sniff(bytes);
  if (!found) return { refusal: "wrong-type" };
  if (!(found.width > 0 && found.height > 0)) return { refusal: "bad-header" };
  if (found.width * found.height > MAX_PIXELS) return { refusal: "too-many-pixels" };
  return { mime: found.mime, ext: EXT[found.mime] };
}

// GET the attachment named by `mxc` as this Persona: its token, its homeserver, the authenticated media path. The
// body is read stream-wise and abandoned the moment it passes the cap, whatever Content-Length said.
// Returns { bytes, mime, ext } or { refusal }.
export async function fetchImage({ fetch: httpFetch, homeserver, token, mxc }) {
  const named = typeof mxc === "string" ? MXC.exec(mxc) : null;
  if (!named) return { refusal: "no-url" };
  const signal = AbortSignal.timeout(FETCH_TIMEOUT_MS);
  try {
    const response = await httpFetch(`${homeserver}/_matrix/client/v1/media/download/${named[1]}/${named[2]}`, {
      method: "GET", headers: { authorization: `Bearer ${token}` }, redirect: "error", signal,
    });
    if (!response.ok || !response.body) return { refusal: "fetch-failed" };
    const declared = Number(response.headers.get("content-length"));
    if (response.headers.get("content-length") !== null && Number.isFinite(declared) && declared > MAX_BYTES) {
      await response.body.cancel().catch(() => {});
      return { refusal: "too-big" };
    }
    const chunks = [];
    let total = 0;
    const reader = response.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.length;
      if (total > MAX_BYTES) {
        await reader.cancel().catch(() => {});
        return { refusal: "too-big" };
      }
      chunks.push(value);
    }
    const bytes = Buffer.concat(chunks);
    const kind = classify(bytes);
    return kind.refusal ? kind : { bytes, ...kind };
  } catch {
    return { refusal: "fetch-failed" };
  }
}

const sha256 = data => createHash("sha256").update(data).digest("hex");

// One Persona's store: `<root>/<hex sha256 of room id>/<hex sha256 of bytes>[.ext]`. The mouth is its only writer.
// A stored image leaves when it is at least RETAIN_MS old and the delivery that carried it is acked, whichever is
// later; a room holds at most ROOM_CAP_BYTES, oldest evicted first. What is on disk at start is old and acked: a
// delivery still pending after a restart is made again by the next sync and stores its image again.
export function createImageStore({ root, now = Date.now, log = () => {} }) {
  const entries = new Map(); // relative path -> { room, size, storedAt, seq }  (seq: the newest delivery carrying it, null once acked)
  let loaded = false;

  function load() {
    if (loaded) return;
    loaded = true;
    for (const room of safeList(root)) {
      if (!HEX64.test(room)) continue;
      for (const name of safeList(join(root, room))) {
        const path = `${room}/${name}`;
        if (name.startsWith(".tmp-")) { try { unlinkSync(join(root, path)); } catch { /* gone */ } continue; }
        if (!IMAGE_PATH.test(path)) continue;
        try { const s = statSync(join(root, path)); if (s.isFile()) entries.set(path, { room, size: s.size, storedAt: s.mtimeMs, seq: null }); } catch { /* gone */ }
      }
    }
  }

  function drop(path) {
    try { unlinkSync(join(root, path)); } catch { /* already gone */ }
    entries.delete(path);
  }

  function sweep() {
    load();
    const at = now();
    for (const [path, e] of [...entries]) if (e.seq === null && at - e.storedAt >= RETAIN_MS) drop(path);
  }

  // Store `bytes` (already classified as `mime`) for `roomId`. Returns the delivery reference.
  function put(roomId, bytes, mime) {
    load();
    const room = sha256(roomId);
    const digest = sha256(bytes);
    const ext = EXT[mime];
    const path = `${room}/${digest}.${ext}`;
    const dir = join(root, room);
    const created = mkdirSync(root, { recursive: true });
    if (created) chmodSync(root, 0o750);
    mkdirSync(dir, { recursive: true });
    chmodSync(dir, DIR_MODE);
    const temp = join(dir, `.tmp-${randomBytes(8).toString("hex")}`);
    const fd = openSync(temp, "wx", FILE_MODE);
    try {
      writeSync(fd, bytes);
      fsyncSync(fd);
    } finally { closeSync(fd); }
    chmodSync(temp, FILE_MODE);
    try { renameSync(temp, join(dir, `${digest}.${ext}`)); } catch (error) { try { unlinkSync(temp); } catch { /* gone */ } throw error; }
    entries.set(path, { room, size: bytes.length, storedAt: now(), seq: entries.get(path)?.seq ?? null });
    evict(room, path);
    sweep();
    return { path, mime, size: bytes.length, sha256: digest };
  }

  function evict(room, keep) {
    const here = [...entries].filter(([, e]) => e.room === room);
    let total = here.reduce((n, [, e]) => n + e.size, 0);
    for (const [path, e] of here.sort((a, b) => a[1].storedAt - b[1].storedAt)) {
      if (total <= ROOM_CAP_BYTES) break;
      if (path === keep) continue;
      drop(path);
      total -= e.size;
    }
  }

  // The delivery numbered `seq` carries the image at `path`: it is kept at least until that delivery is acked.
  function hold(path, seq) {
    const e = entries.get(path);
    if (e) e.seq = Math.max(e.seq ?? 0, seq);
  }

  // Deliveries through `throughSeq` are acked.
  function acked(throughSeq) {
    load();
    for (const e of entries.values()) if (e.seq !== null && e.seq <= throughSeq) e.seq = null;
    sweep();
  }

  return { put, hold, acked, sweep, get size() { load(); return entries.size; } };
}

function safeList(dir) {
  try { return readdirSync(dir); } catch { return []; }
}

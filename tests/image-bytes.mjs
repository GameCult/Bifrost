// Header-only image bytes for tests: just enough of each format for the mouth to sniff its type and size.
export const png = (w = 4, h = 4, pad = 0) => {
  const b = Buffer.alloc(33 + pad);
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]).copy(b);
  b.write("IHDR", 12, "latin1");
  b.writeUInt32BE(w, 16);
  b.writeUInt32BE(h, 20);
  return b;
};
export const jpeg = (w = 4, h = 4) => Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xc0, 0, 11, 8, h >> 8, h & 255, w >> 8, w & 255, 1, 1, 0x11, 0, 0xff, 0xda, 0, 2]);
export const gif = (w = 4, h = 4) => Buffer.from([...Buffer.from("GIF89a", "latin1"), w & 255, w >> 8, h & 255, h >> 8, 0, 0, 0]);
const riff = (chunk, body) => { const b = Buffer.alloc(12 + 8 + body.length); b.write("RIFF", 0, "latin1"); b.writeUInt32LE(b.length - 8, 4); b.write("WEBP", 8, "latin1"); b.write(chunk, 12, "latin1"); b.writeUInt32LE(body.length, 16); body.copy(b, 20); return b; };
export const webpX = (w = 4, h = 4) => { const x = Buffer.alloc(10); x.writeUIntLE(w - 1, 4, 3); x.writeUIntLE(h - 1, 7, 3); return riff("VP8X", x); };
export const webpL = (w = 4, h = 4) => { const x = Buffer.alloc(5); x[0] = 0x2f; const bits = (w - 1) | ((h - 1) << 14); x.writeUInt32LE(bits >>> 0, 1); return riff("VP8L", x); };
export const webpLossy = (w = 4, h = 4) => { const x = Buffer.alloc(10); Buffer.from([0x9d, 0x01, 0x2a]).copy(x, 3); x.writeUInt16LE(w, 6); x.writeUInt16LE(h, 8); return riff("VP8 ", x); };
export const svg = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>');


// A Persona's image directory as the installer leaves it: group-inherited and setgid (2750), empty. Not the mouth's to chmod.
import { chmodSync, mkdirSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
export function personaStoreRoot() {
  const root = join(mkdtempSync(join(tmpdir(), "images-")), "eureka");
  mkdirSync(root);
  chmodSync(root, 0o2750);
  return root;
}

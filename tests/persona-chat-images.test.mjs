// The image fetch and store behind the Matrix mouth's chat images: what is accepted by its bytes, what is bounded,
// what the store writes and when it lets go. Fake fetch, temp directories, no network and no CultLib.
import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { chmodSync, chownSync, existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { FETCH_TIMEOUT_MS, MAX_BYTES, MAX_PIXELS, RETAIN_MS, ROOM_CAP_BYTES, classify, createImageStore, fetchImage, sniff } from "../tools/persona-chat-images.mjs";
import * as docs from "../tools/persona-chat-documents.mjs";
import { gif, jpeg, personaStoreRoot, png, svg, webpL, webpLossy, webpX } from "./image-bytes.mjs";

const sha = data => createHash("sha256").update(data).digest("hex");
const HS = "http://hs.test";
const mxc = "mxc://gamecult.test/AbC_123-x";

// A fake fetch answering one request with `body` (bytes, or a function giving a ReadableStream) and `headers`.
function fakeFetch({ status = 200, headers = {}, body }) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, init });
    return new Response(typeof body === "function" ? body() : body, { status, headers });
  };
  fn.calls = calls;
  return fn;
}
const get = fetchLike => fetchImage({ fetch: fetchLike, homeserver: HS, token: "tok-own", mxc });

test("fetch uses this Persona's token on the authenticated media path of its own homeserver, without following redirects", async () => {
  const f = fakeFetch({ body: png() });
  const got = await get(f);
  assert.equal(got.mime, "image/png");
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, `${HS}/_matrix/client/v1/media/download/gamecult.test/AbC_123-x`);
  assert.equal(f.calls[0].init.headers.authorization, "Bearer tok-own");
  assert.equal(f.calls[0].init.redirect, "error");
});

test("a redirect is refused, not followed: a fetch that honours redirect:error fails the request", async () => {
  // The stub behaves as fetch does: with redirect "error" a redirect response throws; with "follow" it would serve the image from elsewhere.
  const redirecting = async (url, init) => {
    if (init.redirect === "error") throw new TypeError("fetch failed: redirect");
    return new Response(png(), { status: 200 });
  };
  assert.deepEqual(await get(redirecting), { refusal: "fetch-failed" });
});

test("a fetch that never answers is abandoned at the timeout, and the default is 30 s", async () => {
  assert.equal(FETCH_TIMEOUT_MS, 30000);
  let seen;
  const hanging = (url, init) => new Promise((_, reject) => { seen = init.signal; init.signal.addEventListener("abort", () => reject(init.signal.reason)); });
  const started = Date.now();
  assert.deepEqual(await fetchImage({ fetch: hanging, homeserver: HS, token: "tok-own", mxc, timeoutMs: 50 }), { refusal: "fetch-failed" });
  assert.ok(seen instanceof AbortSignal && seen.aborted);
  assert.ok(Date.now() - started < 5000);
});

test("only a plain mxc url is fetched: no other scheme, no path climbing, nothing missing", async () => {
  for (const url of [undefined, null, 5, "https://evil.example/x.png", "mxc://gamecult.test/../etc", "mxc://gamecult.test/a/b", "mxc://gamecult.test/a?x=1", "mxc://gamecult.test/", "http://gamecult.test/_matrix/media/x"]) {
    const f = fakeFetch({ body: png() });
    assert.deepEqual(await fetchImage({ fetch: f, homeserver: HS, token: "t", mxc: url }), { refusal: "no-url" }, String(url));
    assert.equal(f.calls.length, 0);
  }
});

test("the type is decided by the magic bytes: png, jpeg, gif and webp pass; svg and everything else are refused", async () => {
  for (const [bytes, mime] of [[png(), "image/png"], [jpeg(), "image/jpeg"], [gif(), "image/gif"], [webpX(), "image/webp"], [webpL(), "image/webp"], [webpLossy(), "image/webp"]]) {
    const got = await get(fakeFetch({ body: bytes }));
    assert.equal(got.mime, mime);
    assert.deepEqual(got.bytes, bytes);
  }
  for (const bytes of [svg, Buffer.from("<html></html>"), Buffer.from("%PDF-1.7"), Buffer.alloc(0), Buffer.from([0x42, 0x4d, 0, 0, 0, 0, 0, 0]), Buffer.from("RIFF....WAVEfmt ", "latin1")]) {
    assert.deepEqual(await get(fakeFetch({ body: bytes, headers: { "content-type": "image/png" } })), { refusal: "wrong-type" });
  }
});

test("the declared type is never read: an svg labelled image/png is refused, a png labelled svg is accepted as png", async () => {
  assert.deepEqual(await get(fakeFetch({ body: svg, headers: { "content-type": "image/png" } })), { refusal: "wrong-type" });
  const got = await get(fakeFetch({ body: png(), headers: { "content-type": "image/svg+xml" } }));
  assert.equal(got.mime, "image/png");
});

test("a Content-Length over 5 MiB is refused whatever the body turns out to be", async () => {
  const body = () => new ReadableStream({ pull(c) { c.enqueue(png()); c.close(); } });
  assert.deepEqual(await get(fakeFetch({ headers: { "content-length": String(MAX_BYTES + 1) }, body })), { refusal: "too-big" });
  assert.equal((await get(fakeFetch({ headers: { "content-length": String(MAX_BYTES) }, body }))).mime, "image/png", "at the cap it is read");
});

test("the bytes actually read are capped at 5 MiB with a lying or absent Content-Length, and the stream is abandoned", async () => {
  for (const headers of [{}, { "content-length": "100" }]) {
    let pulled = 0;
    const chunk = Buffer.alloc(1024 * 1024);
    const body = () => new ReadableStream({ pull(c) { pulled++; c.enqueue(chunk); if (pulled === 8) c.close(); } });
    assert.deepEqual(await get(fakeFetch({ headers, body })), { refusal: "too-big" });
    assert.ok(pulled <= 7, `the stream was read ${pulled} chunks past the cap`);
  }
});

test("exactly 5 MiB is allowed", async () => {
  const got = await get(fakeFetch({ body: png(4, 4, MAX_BYTES - 33) }));
  assert.equal(got.bytes.length, MAX_BYTES);
});

test("the pixel cap is read from each header without decoding: 40 MP passes, one pixel row more is refused", () => {
  assert.equal(MAX_PIXELS, 8000 * 5000);
  for (const make of [png, jpeg, gif, webpX, webpL, webpLossy]) {
    assert.ok(classify(make(8000, 5000)).mime, `${make.name} at the cap`);
    assert.equal(classify(make(8000, 5001)).refusal, "too-many-pixels", `${make.name} over the cap`);
  }
});

test("a header that will not parse is refused: truncated, no dimensions, zero size, or no frame before the scan", () => {
  assert.equal(classify(png().subarray(0, 20)).refusal, "bad-header");
  assert.equal(classify(png(0, 5)).refusal, "bad-header");
  assert.equal(classify(Buffer.concat([png().subarray(0, 12), Buffer.from("IDAT"), png().subarray(16)])).refusal, "bad-header");
  assert.equal(classify(jpeg().subarray(0, 12)).refusal, "bad-header");
  assert.equal(classify(Buffer.from([0xff, 0xd8, 0xff, 0xda, 0, 2, 0, 0])).refusal, "bad-header");
  assert.equal(classify(gif().subarray(0, 8)).refusal, "bad-header");
  assert.equal(classify(riffOnly()).refusal, "bad-header");
  assert.equal(sniff(Buffer.from([1, 2, 3])), null);
});
function riffOnly() { const b = Buffer.alloc(24); b.write("RIFF", 0, "latin1"); b.write("WEBP", 8, "latin1"); b.write("VP8X", 12, "latin1"); return b; }

test("an error status, a missing body or a throwing fetch is a fetch failure", async () => {
  assert.deepEqual(await get(fakeFetch({ status: 404, body: png() })), { refusal: "fetch-failed" });
  assert.deepEqual(await get(fakeFetch({ status: 200, body: null })), { refusal: "fetch-failed" });
  assert.deepEqual(await fetchImage({ fetch: async () => { throw new Error("boom tok-own"); }, homeserver: HS, token: "tok-own", mxc }), { refusal: "fetch-failed" });
});

// ---- the store ----

function storeIn(clock = { t: Date.now() }) {
  const root = personaStoreRoot();
  return { root, clock, store: createImageStore({ root, now: () => clock.t }) };
}
const files = root => readdirSync(root).flatMap(d => readdirSync(join(root, d)).map(f => `${d}/${f}`));

test("the layout is <sha256 of the room id>/<sha256 of the bytes>.<ext>, generated here, relative and without climbing", () => {
  const { root, store } = storeIn();
  const room = "!Room:gamecult.test";
  const bytes = png(7, 9);
  const ref = store.put(room, bytes, "image/png");
  assert.deepEqual(ref, { path: `${sha(room)}/${sha(bytes)}.png`, mime: "image/png", size: bytes.length, sha256: sha(bytes) });
  assert.deepEqual(readFileSync(join(root, ref.path)), bytes);
  assert.ok(!ref.path.startsWith("/") && !ref.path.split("/").includes(".."));
  assert.match(ref.path, /^[0-9a-f]{64}\/[0-9a-f]{64}\.[a-z0-9]{1,5}$/);
  for (const [make, mime, ext] of [[jpeg, "image/jpeg", "jpg"], [gif, "image/gif", "gif"], [webpX, "image/webp", "webp"]]) assert.ok(store.put(room, make(), mime).path.endsWith(`.${ext}`));
  assert.equal(files(root).length, 4);
});

test("a sender-chosen name never reaches the disk: the store takes no name at all, and a hostile room id is only hashed", () => {
  const { root, store } = storeIn();
  assert.equal(store.put.length, 3);
  store.put("!../../../etc:x", png(), "image/png");
  assert.deepEqual(readdirSync(root).filter(d => !/^[0-9a-f]{64}$/.test(d)), []);
});

test("files are 0640 and room directories 0750 plus the setgid they inherit, whatever the umask, and no temp file is left", () => {
  const old = process.umask(0o077);
  try {
    const { root, store } = storeIn();
    const ref = store.put("!r:x", png(), "image/png");
    assert.equal(statSync(join(root, ref.path)).mode & 0o7777, 0o640);
    assert.equal(statSync(join(root, ref.path, "..")).mode & 0o7777, 0o2750);
    assert.equal(statSync(root).mode & 0o7777, 0o2750, "the Persona's directory is the installer's: untouched");
    assert.deepEqual(readdirSync(join(root, ref.path, "..")).filter(n => n.startsWith(".tmp-")), []);
  } finally { process.umask(old); }
});

test("a room directory takes the Persona directory's group by inheritance: it is never chmodded, so a non-member's chmod cannot strip setgid", () => {
  // bifrost is not in the Persona's group. Stand in for that with a group the test user belongs to that is not its own.
  const other = process.getgroups().find(g => g !== process.getgid());
  if (other === undefined) return; // no second group to inherit from: the next test covers the missing-setgid refusal
  const { root, store } = storeIn();
  try { chownSync(root, process.getuid(), other); } catch { return; } // a sandbox that will not hand the directory to that group cannot show inheritance
  chmodSync(root, 0o2750);
  const ref = store.put("!r:x", png(), "image/png");
  assert.equal(statSync(join(root, ref.path, "..")).gid, other);
  assert.equal(statSync(join(root, ref.path)).gid, other);
  assert.equal(statSync(join(root, ref.path, "..")).mode & 0o2000, 0o2000);
});

test("a Persona directory without setgid refuses the image: nothing is written, and the directories are not chmodded to hide it", () => {
  const { root, store } = storeIn();
  chmodSync(root, 0o750);
  assert.throws(() => store.put("!r:x", png(), "image/png"), { code: "ENOSETGID" });
  assert.deepEqual(files(root), []);
  assert.equal(statSync(root).mode & 0o7777, 0o750, "the installer's directory keeps its mode");
  assert.equal(statSync(join(root, sha("!r:x"))).mode & 0o7777, 0o750, "the room directory it made is left as mkdir made it");
});

test("a write that cannot finish leaves no temp file and no partial image under the final name", () => {
  const { root, store } = storeIn();
  const bytes = png(6, 6);
  mkdirSync(join(root, sha("!r:x"), `${sha(bytes)}.png`), { recursive: true }); // the final name is taken by a directory: the rename fails
  assert.throws(() => store.put("!r:x", bytes, "image/png"));
  assert.deepEqual(readdirSync(join(root, sha("!r:x"))).filter(n => n.startsWith(".tmp-")), []);
});

test("a stored image stays until it is at least 24 h old and acked, whichever is later", () => {
  const { root, clock, store } = storeIn();
  const ref = store.put("!r:x", png(), "image/png");
  store.hold(ref.path, 5);
  clock.t += RETAIN_MS + 1000;
  store.sweep();
  assert.ok(existsSync(join(root, ref.path)), "old but not acked: kept");
  store.acked(4);
  assert.ok(existsSync(join(root, ref.path)), "an ack short of its delivery: kept");
  store.acked(5);
  assert.ok(!existsSync(join(root, ref.path)), "old and acked: gone");

  const young = store.put("!r:x", png(9, 9), "image/png");
  store.hold(young.path, 6);
  store.acked(6);
  clock.t += RETAIN_MS - 1000;
  store.sweep();
  assert.ok(existsSync(join(root, young.path)), "acked but under 24 h: kept");
  clock.t += 2000;
  store.sweep();
  assert.ok(!existsSync(join(root, young.path)), "acked and 24 h old: gone");
});

test("after a restart what is on disk counts as acked and a stray temp file is removed", () => {
  const { root, clock, store } = storeIn();
  const ref = store.put("!r:x", png(), "image/png");
  const dir = join(root, ref.path, "..");
  const stray = join(dir, ".tmp-dead");
  writeFileSync(stray, "x");
  const again = createImageStore({ root, now: () => clock.t + RETAIN_MS * 2 });
  again.sweep();
  assert.ok(!existsSync(join(root, ref.path)));
  assert.ok(!existsSync(stray));
});

test("a room holds at most 50 MiB: the oldest go first, other rooms are untouched, the newest is kept", () => {
  const { root, clock, store } = storeIn();
  const big = n => png(4, 4, 5 * 1024 * 1024 - 33 - n); // distinct bytes, just under 5 MiB each, so ten fit and eleven do not
  const other = store.put("!other:x", big(900), "image/png");
  clock.t += 1;
  const refs = [];
  for (let i = 0; i < 10; i++) { clock.t += 1; const b = big(i); b[40] = i + 1; refs.push(store.put("!r:x", b, "image/png")); }
  assert.ok(refs.every(r => existsSync(join(root, r.path))), "ten images under the cap are all kept");
  clock.t += 1;
  const b = big(50); b[40] = 99;
  const newest = store.put("!r:x", b, "image/png");
  assert.ok(!existsSync(join(root, refs[0].path)), "the oldest is evicted");
  assert.ok(refs.slice(1).every(r => existsSync(join(root, r.path))));
  assert.ok(existsSync(join(root, newest.path)));
  assert.ok(existsSync(join(root, other.path)), "another room's image is not counted");
  const total = refs.slice(1).concat(newest).reduce((n, r) => n + r.size, 0);
  assert.ok(total <= ROOM_CAP_BYTES);
});

// ---- the delivery reference ----

const ref = { path: `${"cd".repeat(32)}/${"ab".repeat(32)}.png`, mime: "image/png", size: 2048, sha256: "ab".repeat(32) };
const base = { seq: 1, roomAlias: "#r:x", roomId: "!r:x", eventId: "$e", sender: "@a:x", senderDisplay: "A", text: "t", atUnixMs: 1, context: [], mentions: [], isDirect: false };

test("a delivery is a 12-tuple whose index 11 is nil or [path, mime, size, sha256]", () => {
  const none = docs.deliveryTuple({ ...base, image: null });
  assert.equal(none.length, 12);
  assert.equal(none[11], null);
  assert.equal(docs.deliveryTuple(base)[11], null, "no image given reads as none");
  const some = docs.deliveryTuple({ ...base, image: ref });
  assert.equal(some.length, 12);
  assert.deepEqual(some[11], [ref.path, ref.mime, ref.size, ref.sha256]);
  assert.deepEqual(docs.parseDelivery(some).image, ref);
  assert.equal(docs.parseDelivery(none).image, null);
  assert.throws(() => docs.parseDelivery(some.slice(0, 11)), /12-field/, "the 11-field delivery is gone: one shape");
  assert.equal(docs.kinds.pending, "gamecult.persona.chat_pending.v2");
});

test("an image path is never absolute, never climbs, never another shape; mime, size and hash are bounded", () => {
  const bad = patch => () => docs.deliveryTuple({ ...base, image: { ...ref, ...patch } });
  for (const path of [`/${ref.path}`, `../${ref.path}`, `${"cd".repeat(32)}/../${"ab".repeat(32)}.png`, `${"cd".repeat(32)}/${"ab".repeat(32)}.png/`, "etc/passwd", `${"CD".repeat(32)}/${"ab".repeat(32)}.png`,
    `${"cd".repeat(32)}/${"ab".repeat(32)}.toolong`, `${"cd".repeat(32)}/${"ab".repeat(32)}.jpg`, `${"cd".repeat(32)}/${"ef".repeat(32)}.png`, `${"cd".repeat(32)}/${"ab".repeat(32)}.`, `${"cd".repeat(31)}/${"ab".repeat(32)}.png`, "", 5]) {
    assert.throws(bad({ path }), Error, String(path));
  }
  assert.throws(bad({ mime: "image/svg+xml" }));
  assert.throws(bad({ mime: "image/png ; x" }));
  assert.throws(bad({ size: MAX_BYTES + 1 }));
  assert.throws(bad({ size: 0 }));
  assert.throws(bad({ size: 1.5 }));
  assert.throws(bad({ sha256: "AB".repeat(32) }));
  assert.throws(() => docs.parseDelivery([...docs.deliveryTuple({ ...base, image: null }).slice(0, 11), ["../x", "image/png", 1, "a".repeat(64)]]));
  const bare = { ...ref, path: `${"cd".repeat(32)}/${"ab".repeat(32)}` };
  assert.deepEqual(docs.deliveryTuple({ ...base, image: bare })[11][0], bare.path, "the extension is optional");
});

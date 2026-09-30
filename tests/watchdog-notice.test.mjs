import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { spawnSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, readdir, stat, writeFile, copyFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

// The fixture is the store Idunn's incident cut wrote: GameCult/Idunn branch
// hands/watchdog-idunn-incident, commit 7f7528b33e8e5c607c19cc7f8f8a8da4def50fca
// ("Add the idunn.operator_incident.v1 wire fixture"), tests/fixtures/ there.
// It holds two records: closed-target (closed, recovered) and open-target.
const FIXTURE = resolve(import.meta.dirname, "fixtures", "idunn.operator_incident.v1.cc");
const FIXTURE_SHA256 = "f446770b07da99204e9036ef521f3136be6251e5b0a68e4e9533a89f25f18cab";
const CLOSED_KEY = "continuity-exhausted:closed-target:1700000000000";
const OPEN_KEY = "continuity-exhausted:open-target:1700000100000";
const TOOL = resolve(import.meta.dirname, "..", "tools", "watchdog-notice.mjs");
const BRIDGE = resolve(import.meta.dirname, "..", "tools", "bifrost-bridge.mjs");
const RECIPIENT = "424242RECIPIENTCANARY";

const OPENED_TEXT = (subject, at) => `Idunn incident opened: continuity-exhausted on ${subject} at ${at}`;
const T_OPEN = "2023-11-14T22:13:20.000Z";
const T_CLOSE = "2023-11-14T22:15:00.000Z";
const CLOSED_TEXT = `Idunn incident closed (recovered): continuity-exhausted on closed-target, opened ${T_OPEN}, closed ${T_CLOSE}`;

// CultLib is the sibling ../CultLib, like the other tests.
const cult = resolve(import.meta.dirname, "..", "..", "CultLib");
const cache = createRequire(resolve(cult, "packages", "cultcache-ts", "package.json"))("@gamecult/cultcache-ts");
const msgpack = createRequire(resolve(cult, "packages", "cultnet-ts", "package.json"))("@msgpack/msgpack");

// A bridge double that logs each call (with the content it was handed) and
// behaves as the `mode` file says: ok, fail, or crash (kills its parent, the
// reader, after logging, as a power cut between send and journal would).
const FAKE_BRIDGE = `
import { appendFileSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name) => args[args.indexOf("--" + name) + 1];
const calls = resolve(here, "calls.jsonl");
const n = (() => { try { return readFileSync(calls, "utf8").trim().split("\\n").length; } catch { return 0; } })();
appendFileSync(calls, JSON.stringify({ args, content: readFileSync(opt("content-file"), "utf8") }) + "\\n");
const mode = readFileSync(resolve(here, "mode"), "utf8").trim();
if (mode === "crash") { process.kill(process.ppid, "SIGKILL"); process.exit(0); }
if (mode === "fail") { process.stderr.write("boom " + opt("recipient-id")); process.stdout.write("boom " + opt("recipient-id")); process.exit(1); }
process.stdout.write(JSON.stringify({ action: "discord-dm", ok: true, messageId: "m" + (n + 1) }));
`;

async function world(prefix, { incidents = FIXTURE, mode = "ok" } = {}) {
  const dir = await mkdtemp(resolve(tmpdir(), prefix));
  const w = {
    dir,
    incidents: resolve(dir, "idunn", "incidents.cc"),
    journal: resolve(dir, "bifrost", "journal.cc"),
    receipts: resolve(dir, "bifrost", "receipts.cc"),
    bridge: resolve(dir, "fake-bridge.mjs"),
    calls: resolve(dir, "calls.jsonl"),
  };
  await mkdir(resolve(dir, "idunn"));
  await writeFile(w.bridge, FAKE_BRIDGE);
  await writeFile(resolve(dir, "mode"), mode);
  if (incidents) await copyFile(incidents, w.incidents);
  w.setMode = (value) => writeFile(resolve(dir, "mode"), value);
  w.run = (extra = {}) => run(w, extra);
  w.status = () => runStatus(w);
  w.posts = async () => (existsSync(w.calls) ? (await readFile(w.calls, "utf8")).trim().split("\n").map((line) => JSON.parse(line)) : []);
  return w;
}

function run(w, { env = {}, bridge = w.bridge } = {}) {
  return spawnSync(process.execPath, [TOOL, "process", "--incident-store", w.incidents, "--journal-store", w.journal, "--receipt-store", w.receipts, "--bridge-cli", bridge], {
    encoding: "utf8",
    env: { ...process.env, DISCORD_OWNER_ID: RECIPIENT, ...env },
  });
}

function runStatus(w) {
  const result = spawnSync(process.execPath, [TOOL, "status", "--journal-store", w.journal], { encoding: "utf8", env: process.env });
  return { ...result, rows: result.status === null || !result.stdout ? [] : JSON.parse(result.stdout) };
}

const opt = (post, name) => post.args[post.args.indexOf(`--${name}`) + 1];
const notice = (post) => opt(post, "cultmesh-command-id").split("#")[1];

// Builds an incident store from the fixture's own envelopes with the payload
// tuples rewritten by `edit`.
async function variantStore(path, edit) {
  const source = await new cache.SingleFileMessagePackBackingStore(FIXTURE).pullAll();
  const tuples = edit(source.map((envelope) => msgpack.decode(envelope.payload)));
  const envelopes = tuples.map((tuple, index) => ({ ...source[0], key: `variant-${index}-${tuple[1]}`, payload: msgpack.encode(tuple), schemaId: source[0].schemaId, catalogEntry: source[0].catalogEntry }));
  await new cache.SingleFileMessagePackBackingStore(path).pushAll(envelopes);
}

test("the Idunn fixture decodes as the incidents Idunn wrote", async () => {
  const bytes = await readFile(FIXTURE);
  assert.equal(createHash("sha256").update(bytes).digest("hex"), FIXTURE_SHA256);
  const w = await world("wn-fixture-");
  const result = w.run();
  assert.equal(result.status, 0, result.stderr);
  const posts = await w.posts();
  const byKey = Object.fromEntries(posts.map((post) => [opt(post, "cultmesh-command-id"), post.content]));
  assert.deepEqual(byKey, {
    [`${CLOSED_KEY}#opened`]: OPENED_TEXT("closed-target", T_OPEN),
    [`${CLOSED_KEY}#closed`]: CLOSED_TEXT,
    [`${OPEN_KEY}#opened`]: OPENED_TEXT("open-target", T_CLOSE),
  });
});

test("one incident yields one opening post across runs", async () => {
  const w = await world("wn-once-");
  assert.equal(w.run().status, 0);
  assert.equal(w.run().status, 0);
  const posts = await w.posts();
  const openings = posts.filter((post) => notice(post) === "opened");
  assert.equal(openings.length, 2);
  assert.deepEqual(openings.map((post) => opt(post, "source-id")).sort(), [CLOSED_KEY, OPEN_KEY]);
  assert.equal(posts.length, 3, "no second run posts again");
  for (const post of posts) {
    const key = opt(post, "cultmesh-command-id");
    assert.equal(opt(post, "recipient-id"), RECIPIENT);
    assert.equal(opt(post, "nonce"), createHash("sha256").update(key).digest("hex").slice(0, 25));
    assert.equal(opt(post, "receipt-store"), w.receipts);
    assert.equal(opt(post, "source-kind"), "idunn-operator-incident");
  }
});

test("closure is posted once and only after a completed opening", async () => {
  const w = await world("wn-closure-");
  assert.equal(w.run().status, 0);
  assert.equal(w.run().status, 0);
  const closures = (await w.posts()).filter((post) => notice(post) === "closed");
  assert.equal(closures.length, 1);
  assert.equal(opt(closures[0], "source-id"), CLOSED_KEY);

  // An opening that never completes blocks its closure, now and later.
  const failing = await world("wn-closure-failed-", { mode: "fail" });
  for (let run = 0; run < 5; run += 1) failing.run();
  await failing.setMode("ok");
  failing.run();
  failing.run();
  assert.equal((await failing.posts()).filter((post) => notice(post) === "closed").length, 0);
  const rows = (await failing.status()).rows;
  assert.deepEqual(rows.map((row) => `${row.notice}:${row.status}:${row.attempts}`), ["opened:failed:5", "opened:failed:5"]);

  const crashed = await world("wn-closure-unknown-", { mode: "crash" });
  assert.equal(crashed.run().status, null, "the reader died mid-send");
  await crashed.setMode("ok");
  crashed.run();
  crashed.run();
  assert.equal((await crashed.posts()).filter((post) => notice(post) === "closed").length, 0);
});

test("a running entry becomes unknown and is never re-posted", async () => {
  const w = await world("wn-running-", { mode: "crash" });
  assert.equal(w.run().status, null);
  assert.equal((await w.status()).rows[0].status, "running");
  await w.setMode("ok");
  const recovered = w.run();
  assert.notEqual(recovered.status, 0, "an unknown entry is a delivery failure");
  assert.equal(JSON.parse(recovered.stdout).unknown, 1);
  const posts = await w.posts();
  assert.equal(posts.filter((post) => opt(post, "cultmesh-command-id") === `${CLOSED_KEY}#opened`).length, 1, "the interrupted notice is not sent again");
  const rows = (await w.status()).rows;
  assert.equal(rows.find((row) => row.incidentKey === CLOSED_KEY && row.notice === "opened").status, "unknown");
  assert.notEqual(w.run().status, 0, "still failing on the next run");
  assert.equal((await w.posts()).length, posts.length);
});

test("a failed send is retried with the same nonce up to five attempts", async () => {
  const w = await world("wn-retry-", { mode: "fail" });
  const exits = [];
  for (let run = 0; run < 5; run += 1) exits.push(w.run().status);
  assert.deepEqual(exits, [0, 0, 0, 0, 1], "non-zero once attempts are exhausted");
  const open = (await w.posts()).filter((post) => opt(post, "source-id") === OPEN_KEY);
  assert.equal(open.length, 5);
  assert.equal(new Set(open.map((post) => opt(post, "nonce"))).size, 1);
  const before = (await w.posts()).length;
  const again = w.run();
  assert.notEqual(again.status, 0);
  assert.equal((await w.posts()).length, before, "no spawn after the fifth attempt");
  const status = await w.status();
  assert.notEqual(status.status, 0);
  for (const text of [again.stdout, again.stderr, status.stdout, await readFile(w.journal, "latin1")]) {
    assert.ok(!text.includes("RECIPIENTCANARY"), "bridge output and recipient never reach the reader's output or journal");
  }
});

test("the reader never writes or locks the incident store", async () => {
  const w = await world("wn-readonly-");
  const before = { bytes: await readFile(w.incidents), mtime: (await stat(w.incidents)).mtimeMs };
  assert.equal(w.run().status, 0);
  assert.equal(w.run().status, 0);
  assert.deepEqual(await readFile(w.incidents), before.bytes);
  assert.equal((await stat(w.incidents)).mtimeMs, before.mtime);
  assert.deepEqual(await readdir(resolve(w.dir, "idunn")), ["incidents.cc"], "no lock or temp file beside the store");
});

test("notice content carries no sensitive text", async () => {
  const w = await world("wn-egress-", { incidents: null });
  await variantStore(w.incidents, (tuples) => {
    const [closed, open] = tuples;
    const longer = [...open, "extra-CANARYFIELD"];
    const pathSubject = ["idunn.operator_incident.v1", `continuity-exhausted:/etc/CANARYPATH:${open[4]}`, "continuity-exhausted", "/etc/CANARYPATH", open[4], null, null];
    const otherSchema = ["idunn.operator_incident.v0", "CANARYSCHEMA", "continuity-exhausted", "x", 1, null, null];
    return [closed, longer, pathSubject, otherSchema];
  });
  const result = w.run();
  assert.notEqual(result.status, 0, "refused records fail the run");
  assert.deepEqual(JSON.parse(result.stdout), { skipped: 1, refused: 2, unknown: 0, exhausted: 0 });
  const posts = await w.posts();
  assert.deepEqual(posts.map((post) => opt(post, "source-id")), [CLOSED_KEY, CLOSED_KEY]);
  for (const post of posts) assert.match(post.content, /^Idunn incident (opened: continuity-exhausted on closed-target at \d{4}-[\d-]+T[\d:.]+Z|closed \(recovered\): continuity-exhausted on closed-target, opened [\dTZ:.-]+, closed [\dTZ:.-]+)$/);
  for (const text of [result.stdout, result.stderr, (await w.status()).stdout, await readFile(w.journal, "latin1"), JSON.stringify(posts)]) {
    assert.ok(!text.includes("CANARY"), "no byte of a refused record is echoed");
  }
});

test("no recipient means no journal write and a non-zero exit", async () => {
  for (const value of [undefined, "", "   "]) {
    const w = await world("wn-recipient-");
    const env = value === undefined ? { DISCORD_OWNER_ID: undefined } : { DISCORD_OWNER_ID: value };
    const result = spawnSync(process.execPath, [TOOL, "process", "--incident-store", w.incidents, "--journal-store", w.journal, "--receipt-store", w.receipts, "--bridge-cli", w.bridge], { encoding: "utf8", env: { ...process.env, ...env } });
    assert.notEqual(result.status, 0);
    assert.ok(!existsSync(w.journal));
    assert.deepEqual(await w.posts(), []);
  }
});

test("an absent incident store is a clean no-op", async () => {
  const w = await world("wn-absent-", { incidents: null });
  const result = w.run();
  assert.equal(result.status, 0, result.stderr);
  assert.ok(!existsSync(w.journal));
  assert.deepEqual(await w.posts(), []);
});

test("terminal entries are pruned only after Idunn retires the incident", async () => {
  const w = await world("wn-prune-");
  assert.equal(w.run().status, 0);
  const keys = async () => (await w.status()).rows.map((row) => `${row.incidentKey}#${row.notice}`).sort();
  const all = [`${CLOSED_KEY}#closed`, `${CLOSED_KEY}#opened`, `${OPEN_KEY}#opened`];
  assert.deepEqual(await keys(), all);
  assert.equal(w.run().status, 0);
  assert.deepEqual(await keys(), all, "entries stay while Idunn still lists the incident");
  await variantStore(w.incidents, (tuples) => [tuples[1]]);
  assert.equal(w.run().status, 0);
  assert.deepEqual(await keys(), [`${OPEN_KEY}#opened`]);
  await variantStore(w.incidents, () => []);
  assert.equal(w.run().status, 0);
  assert.deepEqual(await keys(), []);
  assert.equal((await w.posts()).length, 3, "retirement never reposts");
});

test("errors name a code and never echo a path or an input", async () => {
  const w = await world("wn-canary-", { incidents: null });
  await mkdir(resolve(w.dir, "CANARYDIR"));
  const corrupt = resolve(w.dir, "CANARYDIR", "incidents.cc");
  await writeFile(corrupt, "not a cultcache store CANARYBYTES");
  const result = spawnSync(process.execPath, [TOOL, "process", "--incident-store", corrupt, "--journal-store", resolve(w.dir, "CANARYDIR", "j.cc"), "--receipt-store", w.receipts, "--bridge-cli", w.bridge], { encoding: "utf8", env: { ...process.env, DISCORD_OWNER_ID: RECIPIENT } });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /incident-store-unreadable/);
  assert.ok(!(result.stdout + result.stderr).includes("CANARY"));
});

// The real bridge, reached through the real reader, with Discord's HTTP API
// stubbed at fetch (NODE_OPTIONS reaches the spawned bridge).
async function fetchStub(dir) {
  const log = resolve(dir, "fetch.jsonl");
  const stub = resolve(dir, "fetch-stub.mjs");
  await writeFile(stub, `
import { appendFileSync } from "node:fs";
globalThis.fetch = async (url, init) => {
  appendFileSync(${JSON.stringify(log)}, JSON.stringify({ url: String(url), body: JSON.parse(init.body) }) + "\\n");
  const body = String(url).endsWith("/users/@me/channels") ? { id: "dm-channel" } : { id: "message-1" };
  return new Response(JSON.stringify(body), { status: 200 });
};
`);
  return { log, options: `--import=${pathToFileURL(stub).href}` };
}
const fetches = async (log) => (existsSync(log) ? (await readFile(log, "utf8")).trim().split("\n").map((line) => JSON.parse(line)) : []);

test("discord-dm carries nonce and enforce_nonce when asked", async () => {
  const dir = await mkdtemp(resolve(tmpdir(), "wn-bridge-"));
  const stub = await fetchStub(dir);
  const env = { ...process.env, NODE_OPTIONS: stub.options, BIFROST_DISCORD_BOT_TOKEN: "test-token-not-real" };
  const dm = (...extra) => spawnSync(process.execPath, [BRIDGE, "discord-dm", "--recipient-id", "1", "--content", "hi", "--cultmesh-command-id", "cmd", "--receipt-store", resolve(dir, "r.cc"), ...extra], { encoding: "utf8", env });

  const with_nonce = dm("--nonce", "abc123");
  assert.equal(with_nonce.status, 0, with_nonce.stderr);
  let posted = (await fetches(stub.log)).filter((entry) => entry.url.endsWith("/channels/dm-channel/messages"));
  assert.equal(posted.at(-1).body.nonce, "abc123");
  assert.equal(posted.at(-1).body.enforce_nonce, true);

  const without = dm();
  assert.equal(without.status, 0, without.stderr);
  posted = (await fetches(stub.log)).filter((entry) => entry.url.endsWith("/channels/dm-channel/messages"));
  assert.ok(!("nonce" in posted.at(-1).body) && !("enforce_nonce" in posted.at(-1).body));

  const before = (await fetches(stub.log)).length;
  const tooLong = dm("--nonce", "x".repeat(26));
  assert.notEqual(tooLong.status, 0);
  assert.equal((await fetches(stub.log)).length, before, "an over-long nonce posts nothing");
});

test("the reader posts through the real bridge with its nonce", async () => {
  const w = await world("wn-real-bridge-");
  const stub = await fetchStub(w.dir);
  const result = w.run({ bridge: BRIDGE, env: { NODE_OPTIONS: stub.options, BIFROST_DISCORD_BOT_TOKEN: "test-token-not-real" } });
  assert.equal(result.status, 0, result.stderr);
  const posted = (await fetches(stub.log)).filter((entry) => entry.url.endsWith("/messages"));
  assert.deepEqual(posted.map((entry) => entry.body.content).sort(), [CLOSED_TEXT, OPENED_TEXT("closed-target", T_OPEN), OPENED_TEXT("open-target", T_CLOSE)].sort());
  for (const entry of posted) {
    assert.equal(entry.body.nonce.length, 25);
    assert.equal(entry.body.enforce_nonce, true);
  }
  assert.ok(existsSync(w.receipts), "the bridge wrote its crossing receipts to the reader's receipt store");
  assert.deepEqual((await w.status()).rows.map((row) => row.status), ["completed", "completed", "completed"]);
});


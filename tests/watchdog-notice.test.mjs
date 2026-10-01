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

const OPENED_TEXT = (subject, at) => `Idunn incident opened: continuity-exhausted on \`${subject}\` at ${at}`;
const T_OPEN = "2023-11-14T22:13:20.000Z";
const T_CLOSE = "2023-11-14T22:15:00.000Z";
const CLOSED_TEXT = `Idunn incident closed (recovered): continuity-exhausted on \`closed-target\`, opened ${T_OPEN}, closed ${T_CLOSE}`;

// CultLib is the sibling ../CultLib, like the other tests.
const cult = resolve(import.meta.dirname, "..", "..", "CultLib");
const cache = createRequire(resolve(cult, "packages", "cultcache-ts", "package.json"))("@gamecult/cultcache-ts");
const msgpack = createRequire(resolve(cult, "packages", "cultnet-ts", "package.json"))("@msgpack/msgpack");

// A bridge double that logs each call (with the content it was handed) and
// behaves as the `mode` file says: ok; fail (exit 75, retryable); die
// (exit 1 after "sending"); signal (killed after "sending"); hang (never answers after
// "sending"); flood (answers with more stdout than spawnSync buffers); or crash (kills its
// parent, the reader, after logging, as a power cut between send and journal would).
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
if (mode === "fail" || mode === "die") { process.stderr.write("boom " + opt("recipient-id")); process.stdout.write("boom " + opt("recipient-id")); process.exit(mode === "fail" ? 75 : 1); }
if (mode === "signal") process.kill(process.pid, "SIGKILL");
if (mode === "hang") setTimeout(() => {}, 30000);
else if (mode === "flood") process.stdout.write("x".repeat(2 * 1024 * 1024));
else
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
  w.status = (env) => runStatus(w, env);
  w.posts = async () => (existsSync(w.calls) ? (await readFile(w.calls, "utf8")).trim().split("\n").map((line) => JSON.parse(line)) : []);
  return w;
}

// The reader reads Date.now(). A test drives the backoff the way it stubs fetch:
// a preload (fake-clock.mjs, passed to the reader as --import) fixes Date.now
// to the minute (from T0) the run happens at. The reader has no clock seam.
const T0 = 1_800_000_000_000;
const CLOCK = resolve(import.meta.dirname, "fake-clock.mjs");
const at = (minutes) => ({ FAKE_NOW_MS: String(T0 + Math.round(minutes * 60_000)) });

function run(w, { env = {}, bridge = w.bridge } = {}) {
  return spawnSync(process.execPath, ["--import", CLOCK, TOOL, "process", "--incident-store", w.incidents, "--journal-store", w.journal, "--receipt-store", w.receipts, "--bridge-cli", bridge], {
    encoding: "utf8",
    env: { ...process.env, DISCORD_OWNER_ID: RECIPIENT, ...env },
  });
}

function runStatus(w, env = {}) {
  const result = spawnSync(process.execPath, ["--import", CLOCK, TOOL, "status", "--journal-store", w.journal], { encoding: "utf8", env: { ...process.env, ...env } });
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
  for (const minute of [0, 1, 3, 7, 15]) failing.run({ env: at(minute) });
  assert.equal((await failing.posts()).filter((post) => notice(post) === "closed").length, 0);
  const rows = (await failing.status()).rows;
  assert.deepEqual(rows.map((row) => `${row.notice}:${row.status}:${row.attempts}`), ["opened:failed:5", "opened:failed:5"]);
  await failing.setMode("ok");
  assert.equal(failing.run({ env: at(31) }).status, 0);
  assert.equal(failing.run({ env: at(32) }).status, 0);
  assert.deepEqual((await failing.posts()).filter((post) => notice(post) === "closed").map((post) => opt(post, "source-id")), [CLOSED_KEY], "the closure follows the completed opening, once");

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
  assert.notEqual(w.run({ env: at(100_000) }).status, 0, "still failing, and not re-posted, long past any backoff");
  assert.equal((await w.posts()).length, posts.length);
});

test("an entry stamped ahead of the clock is eligible now, not deferred by the skew", async () => {
  const w = await world("wn-skew-", { mode: "fail" });
  w.run({ env: at(1000) });
  assert.equal((await w.posts()).length, 2, "both openings tried once, stamped at minute 1000");
  w.run({ env: at(0) });
  assert.equal((await w.posts()).length, 4, "the clock is behind the stamp: retried at once");
  assert.equal((await w.status(at(0))).rows[0].nextEligibleAt, new Date(T0 + 2 * 60_000).toISOString(), "the retry rewrote the stamp from the corrected clock");
  w.run({ env: at(1.5) });
  assert.equal((await w.posts()).length, 4, "the cadence holds from the rewritten stamp");
  w.run({ env: at(2) });
  assert.equal((await w.posts()).length, 6);
});

const backoffMinutes = [0, 1, 3, 7, 15, 31, 63, 123, 183];

test("a failed notice is retried after its backoff and never exhausted", async () => {
  const w = await world("wn-retry-", { mode: "fail" });
  // Run at every expected spawn minute and one minute before it: only the former may spawn.
  const probes = [...new Set(backoffMinutes.flatMap((minute) => [minute - 1, minute]).filter((minute) => minute >= 0))].sort((a, b) => a - b);
  const spawned = [];
  for (const minute of probes) {
    const before = (await w.posts()).filter((post) => opt(post, "source-id") === OPEN_KEY).length;
    w.run({ env: at(minute) });
    const after = (await w.posts()).filter((post) => opt(post, "source-id") === OPEN_KEY).length;
    if (after > before) spawned.push(minute);
  }
  assert.deepEqual(spawned, backoffMinutes, "attempts at 1, 2, 4, 8, 16, 32 then every 60 minutes after the last attempt");
  const open = (await w.posts()).filter((post) => opt(post, "source-id") === OPEN_KEY);
  assert.equal(new Set(open.map((post) => opt(post, "nonce"))).size, 1, "every attempt carries the same nonce");
  const rows = (await w.status()).rows;
  assert.deepEqual(rows.map((row) => `${row.status}:${row.attempts}:${row.lastError}`), ["failed:9:bridge-not-sent", "failed:9:bridge-not-sent"], "never a terminal state");
  const final = w.run({ env: at(183) });
  const status = await w.status();
  assert.notEqual(final.status, 0);
  assert.notEqual(status.status, 0);
  for (const text of [final.stdout, final.stderr, status.stdout, await readFile(w.journal, "latin1")]) {
    assert.ok(!text.includes("RECIPIENTCANARY"), "bridge output and recipient never reach the reader's output or journal");
  }
});

test("a failed notice is not retried before its delay", async () => {
  const w = await world("wn-delay-", { mode: "fail" });
  const count = async () => (await w.posts()).length;
  w.run({ env: at(0) });
  assert.equal(await count(), 2, "both openings tried once");
  w.run({ env: at(0.99) });
  w.run({ env: at(0) });
  assert.equal(await count(), 2, "a run inside the delay spawns nothing");
  const row = (await w.status()).rows[0];
  assert.equal(row.attempts, 1);
  assert.equal(row.nextEligibleAt, new Date(T0 + 60_000).toISOString(), "status names when the entry is next eligible");
  w.run({ env: at(1) });
  assert.equal(await count(), 4, "eligible once the delay has passed");
  assert.equal((await w.status()).rows[0].nextEligibleAt, new Date(T0 + 3 * 60_000).toISOString());
});

test("the unit signals from the third failed attempt while still retrying", async () => {
  const w = await world("wn-signal-", { mode: "fail" });
  const exits = [];
  for (const minute of [0, 1, 3]) exits.push(w.run({ env: at(minute) }).status);
  assert.deepEqual(exits, [0, 0, 1], "zero after attempts 1 and 2, non-zero from 3");
  assert.equal((await w.status()).status, 1);
  const before = (await w.posts()).length;
  assert.equal(w.run({ env: at(7) }).status, 1);
  assert.equal((await w.posts()).length, before + 2, "attempt 4 still spawns");
  assert.ok((await w.status()).rows.every((row) => row.status === "failed" && row.attempts === 4));
});

test("a notice that finally sends after many failures posts once", async () => {
  const w = await world("wn-finally-", { mode: "fail" });
  for (const minute of [0, 1, 3, 7, 15]) w.run({ env: at(minute) });
  await w.setMode("ok");
  assert.equal(w.run({ env: at(31) }).status, 0);
  const open = (await w.posts()).filter((post) => opt(post, "source-id") === OPEN_KEY);
  assert.equal(open.length, 6, "five failures and one success");
  assert.equal(new Set(open.map((post) => opt(post, "nonce"))).size, 1, "the success carries the same nonce");
  const before = (await w.posts()).length;
  for (const minute of [32, 100, 1000]) assert.equal(w.run({ env: at(minute) }).status, 0);
  assert.equal((await w.posts()).length, before, "completed is never posted again");
  assert.deepEqual((await w.status()).rows.map((row) => `${row.status}:${row.attempts}`), ["completed:1", "completed:6", "completed:6"]);
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
    const pathSubject = ["idunn.operator_incident.v1", `continuity-exhausted:/etc/CANARYPATH x:${open[4]}`, "continuity-exhausted", "/etc/CANARYPATH x", open[4], null, null];
    const otherSchema = ["idunn.operator_incident.v0", "CANARYSCHEMA", "continuity-exhausted", "x", 1, null, null];
    return [closed, longer, pathSubject, otherSchema];
  });
  const result = w.run();
  assert.notEqual(result.status, 0, "refused records fail the run");
  assert.deepEqual(JSON.parse(result.stdout), { skipped: 1, refused: 2, unknown: 0, failing: 0 });
  const posts = await w.posts();
  assert.deepEqual(posts.map((post) => opt(post, "source-id")), [CLOSED_KEY, CLOSED_KEY]);
  for (const post of posts) assert.match(post.content, /^Idunn incident (opened: continuity-exhausted on `closed-target` at \d{4}-[\d-]+T[\d:.]+Z|closed \(recovered\): continuity-exhausted on `closed-target`, opened [\dTZ:.-]+, closed [\dTZ:.-]+)$/);
  for (const text of [result.stdout, result.stderr, (await w.status()).stdout, await readFile(w.journal, "latin1"), JSON.stringify(posts)]) {
    for (const canary of ["CANARYFIELD", "CANARYPATH", "CANARYSCHEMA"]) assert.ok(!text.includes(canary), "no byte of a refused record is echoed");
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

test("an emptied or restored incident store never re-sends", async () => {
  const w = await world("wn-restore-");
  assert.equal(w.run().status, 0);
  const keys = async () => (await w.status()).rows.map((row) => `${row.incidentKey}#${row.notice}`).sort();
  const all = [`${CLOSED_KEY}#closed`, `${CLOSED_KEY}#opened`, `${OPEN_KEY}#opened`];
  assert.deepEqual(await keys(), all);
  await variantStore(w.incidents, (tuples) => [tuples[1]]);
  w.run();
  await variantStore(w.incidents, () => []);
  w.run();
  await writeFile(w.incidents, "");
  w.run();
  assert.deepEqual(await keys(), all, "absence from Idunn's store deletes nothing");
  await copyFile(FIXTURE, w.incidents);
  assert.equal(w.run().status, 0);
  assert.equal((await w.posts()).length, 3, "exactly one post per notice across all views of the store");
});

test("a bridge that dies after sending is unknown and never re-posted", async () => {
  for (const mode of ["die", "signal"]) {
    const w = await world(`wn-${mode}-`, { mode });
    const first = w.run();
    assert.notEqual(first.status, 0);
    assert.equal(JSON.parse(first.stdout).unknown, 2);
    assert.notEqual((await w.status()).status, 0, "status exits non-zero while an entry is unknown");
    await w.setMode("ok");
    assert.notEqual(w.run({ env: at(1000) }).status, 0, "far past any backoff");
    assert.notEqual(w.run({ env: at(100_000) }).status, 0);
    const posts = await w.posts();
    assert.equal(posts.length, 2, "one opening post per incident, no retry, no closure");
    assert.ok(posts.every((post) => notice(post) === "opened"));
    assert.deepEqual((await w.status()).rows.map((row) => `${row.notice}:${row.status}:${row.attempts}:${row.lastError}`), ["opened:unknown:1:bridge-exit-unknown", "opened:unknown:1:bridge-exit-unknown"]);
    assert.ok(!(await readFile(w.journal, "latin1")).includes("RECIPIENTCANARY"));
  }
});

test("a spawn timeout and a spawn error are unknown, never failed, never re-posted", async () => {
  for (const mode of ["hang", "flood"]) {
    const w = await world(`wn-${mode}-`, { mode });
    const first = w.run({ env: { WATCHDOG_NOTICE_BRIDGE_TIMEOUT_MS: "1500" } });
    assert.notEqual(first.status, 0, mode);
    assert.equal(JSON.parse(first.stdout).unknown, 2, mode);
    await w.setMode("ok");
    assert.notEqual(w.run({ env: at(1000) }).status, 0, "far past any backoff");
    assert.notEqual(w.run({ env: at(100_000) }).status, 0);
    const posts = await w.posts();
    assert.equal(posts.length, 2, `${mode}: one opening post per incident, no retry, no closure`);
    assert.ok(posts.every((post) => notice(post) === "opened"));
    assert.deepEqual((await w.status()).rows.map((row) => `${row.notice}:${row.status}:${row.attempts}:${row.lastError}`), ["opened:unknown:1:bridge-exit-unknown", "opened:unknown:1:bridge-exit-unknown"], mode);
  }
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
  const channels = String(url).endsWith("/users/@me/channels");
  const behavior = process.env.STUB_BEHAVIOR;
  if (channels && behavior === "dm-channel-500") return new Response("{}", { status: 500 });
  const rejected = /^message-(\\d{3})$/.exec(behavior ?? "");
  if (!channels && rejected) return new Response("{}", { status: Number(rejected[1]) });
  if (!channels && behavior === "message-network-error") throw new TypeError("fetch failed");
  return new Response(JSON.stringify(channels ? { id: "dm-channel" } : { id: "message-1" }), { status: 200 });
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
  assert.deepEqual(posted.at(-1).body.allowed_mentions, { parse: [] }, "no mention is ever parsed");

  const without = dm();
  assert.equal(without.status, 0, without.stderr);
  posted = (await fetches(stub.log)).filter((entry) => entry.url.endsWith("/channels/dm-channel/messages"));
  assert.ok(!("nonce" in posted.at(-1).body) && !("enforce_nonce" in posted.at(-1).body));

  const before = (await fetches(stub.log)).length;
  const tooLong = dm("--nonce", "x".repeat(26));
  assert.notEqual(tooLong.status, 0);
  assert.equal((await fetches(stub.log)).length, before, "an over-long nonce posts nothing");
});

test("discord-dm exits 75 only before delivery", async () => {
  const dir = await mkdtemp(resolve(tmpdir(), "wn-exit75-"));
  const stub = await fetchStub(dir);
  const dm = (behavior, { token = "test-token-not-real", recipient = "1" } = {}) => {
    const env = { ...process.env, NODE_OPTIONS: stub.options, BIFROST_DISCORD_BOT_TOKEN: token, DISCORD_BOT_TOKEN: token };
    if (behavior) env.STUB_BEHAVIOR = behavior;
    if (!token) { delete env.BIFROST_DISCORD_BOT_TOKEN; delete env.DISCORD_BOT_TOKEN; }
    const args = ["discord-dm", ...(recipient ? ["--recipient-id", recipient] : []), "--content", "hi", "--cultmesh-command-id", "cmd", "--receipt-store", resolve(dir, "r.cc")];
    return spawnSync(process.execPath, [BRIDGE, ...args], { encoding: "utf8", env });
  };
  const messagePosts = async () => (await fetches(stub.log)).filter((entry) => entry.url.endsWith("/messages")).length;

  assert.equal(dm(undefined, { token: "" }).status, 75, "missing token");
  assert.equal(dm(undefined, { recipient: "" }).status, 75, "missing argument");
  assert.equal(dm("dm-channel-500").status, 75, "DM channel failure");
  assert.equal(await messagePosts(), 0, "nothing was posted by any of those");
  assert.equal(dm("message-400").status, 75, "a 4xx answer to the message POST");
  assert.equal(await messagePosts(), 1);
  for (const status of [500, 502, 503, 429]) {
    const before = await messagePosts();
    assert.equal(dm(`message-${status}`).status, 75, `a ${status} answer is retryable, not unknown`);
    assert.equal(await messagePosts(), before + 1);
  }
  const lost = dm("message-network-error");
  assert.equal(lost.status, 1, "a network error after the message POST was sent is not exit 75");
  assert.equal(await messagePosts(), 6);
  assert.equal(dm().status, 0);
});

test("a 5xx or 429 on the message POST is retried with the same nonce", async () => {
  for (const status of [502, 429]) {
    const w = await world(`wn-${status}-`);
    const stub = await fetchStub(w.dir);
    const env = { NODE_OPTIONS: stub.options, BIFROST_DISCORD_BOT_TOKEN: "test-token-not-real" };
    const messages = async () => (await fetches(stub.log)).filter((entry) => entry.url.endsWith("/messages"));
    for (let tick = 1; tick <= 2; tick += 1) {
      const result = w.run({ bridge: BRIDGE, env: { ...env, ...at(tick === 1 ? 0 : 1), STUB_BEHAVIOR: `message-${status}` } });
      assert.equal(result.status, 0, result.stderr);
      assert.deepEqual((await w.status()).rows.map((row) => `${row.status}:${row.attempts}`), [`failed:${tick}`, `failed:${tick}`]);
    }
    const sent = await messages();
    assert.equal(sent.length, 4, "two openings, each tried twice");
    const nonces = Map.groupBy(sent, (entry) => entry.body.content);
    assert.equal(nonces.size, 2);
    for (const group of nonces.values()) assert.equal(new Set(group.map((entry) => entry.body.nonce)).size, 1, "a retry reuses its nonce");
    assert.equal(w.run({ bridge: BRIDGE, env: { ...env, ...at(3) } }).status, 0);
    assert.deepEqual((await w.status()).rows.map((row) => row.status), ["completed", "completed", "completed"]);
  }
});

test("every subject Idunn accepts is delivered, inside backticks", async () => {
  const good = ["gamecult/odin", "odin:store", "_odin", "a".repeat(256), "Odin.v2-x"];
  const bad = ["a".repeat(257), "has space", "back`tick", "new\nline", "", "ün"];
  const w = await world("wn-subject-", { incidents: null });
  await variantStore(w.incidents, (tuples) => {
    return [...good, ...bad].map((subject, index) => {
      const opened = 1_700_000_200_000 + index;
      return ["idunn.operator_incident.v1", `continuity-exhausted:${subject}:${opened}`, "continuity-exhausted", subject, opened, null, null];
    });
  });
  const stub = await fetchStub(w.dir);
  const result = w.run({ bridge: BRIDGE, env: { NODE_OPTIONS: stub.options, BIFROST_DISCORD_BOT_TOKEN: "test-token-not-real" } });
  assert.deepEqual(JSON.parse(result.stdout), { skipped: 0, refused: bad.length, unknown: 0, failing: 0 });
  const contents = (await fetches(stub.log)).filter((entry) => entry.url.endsWith("/messages")).map((entry) => entry.body.content).sort();
  const at = (index) => new Date(1_700_000_200_000 + index).toISOString();
  assert.deepEqual(contents, good.map((subject, index) => OPENED_TEXT(subject, at(index))).sort());
  for (const content of contents) assert.match(content, /on `[A-Za-z0-9_.:/-]{1,256}` at /);
});

test("an out-of-range time refuses the record without aborting the run", async () => {
  const w = await world("wn-time-", { incidents: null });
  await variantStore(w.incidents, (tuples) => {
    const [closed, open] = tuples;
    const hugeClose = [...closed];
    hugeClose[5] = 8.7e15;
    hugeClose[1] = closed[1];
    const hugeOpen = ["idunn.operator_incident.v1", "continuity-exhausted:huge-open:8700000000000000", "continuity-exhausted", "huge-open", 8.7e15, null, null];
    const edge = ["idunn.operator_incident.v1", "continuity-exhausted:edge:8640000000000000", "continuity-exhausted", "edge", 8.64e15, null, null];
    return [hugeClose, hugeOpen, edge, open];
  });
  const result = w.run();
  assert.equal(result.stderr, "", "no unexpected-error");
  assert.deepEqual(JSON.parse(result.stdout), { skipped: 0, refused: 2, unknown: 0, failing: 0 });
  assert.notEqual(result.status, 0, "refused records fail the run");
  assert.deepEqual((await w.posts()).map((post) => opt(post, "source-id")).sort(), ["continuity-exhausted:edge:8640000000000000", OPEN_KEY]);
  assert.ok((await w.status()).rows.every((row) => row.status === "completed"));
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
  assert.equal((await w.status()).status, 0, "status exits zero when nothing is unknown or failing");
});


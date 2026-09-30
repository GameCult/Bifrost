#!/usr/bin/env node
// One-shot reader: turns Idunn's operator incidents into owner DMs.
//
//   process --incident-store P --journal-store J --receipt-store R [--bridge-cli B]
//   status  --journal-store J
//
// Owner of the decision to send, the journal (J), the recipient
// (DISCORD_OWNER_ID) and the token (BIFROST_DISCORD_BOT_TOKEN, read only by the
// bridge it spawns). Idunn's store P is read unlocked and never written: no
// CultMesh node, no lock, only SingleFileMessagePackBackingStore.pullAll().
//
// Requires CultLib 8cb3b728 or later (package @gamecult/cultcache-ts); see
// docs/watchdog-notice.md.
//
// Nothing here echoes an input value. Errors are fixed codes, and the
// journal's lastError is a fixed code, never bridge output.
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { mkdtemp, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const cult = resolve(process.env.VOIDBOT_CULTLIB_ROOT || resolve(root, "..", "CultLib"));

const INCIDENT_SCHEMA = "idunn.operator_incident.v1";
const JOURNAL_TYPE = "bifrost.watchdog_notice_execution";
const JOURNAL_SCHEMA = "bifrost.watchdog_notice_execution.v1";
const MAX_ATTEMPTS = 5;
const BRIDGE_TIMEOUT_MS = 60_000;
const STATUSES = ["running", "completed", "failed", "unknown"];
const NAME = /^[a-z][a-z-]{0,63}$/;
// Idunn's require_id (GameCult/Idunn src/control_plane.rs:9122-9131 at 7f7528b):
// 1-256 bytes of [A-Za-z0-9-_.:/]. The notice puts the subject in inline code.
const SUBJECT = /^[A-Za-z0-9_.:/-]{1,256}$/;
const MAX_DATE_MS = 8.64e15;
// bifrost-bridge.mjs discord-dm exits 75 (EX_TEMPFAIL) only when it knows the
// message was not sent. Every other non-success leaves the outcome unknown.
const BRIDGE_NOT_SENT = 75;

class NoticeError extends Error {}
const fail = (code) => new NoticeError(code);

async function main(args) {
  const [verb, ...rest] = args;
  const options = parseArgs(rest);
  if (verb === "status") return status(options);
  if (verb === "process") return processIncidents(options);
  throw fail("unknown-verb");
}

async function processIncidents(options) {
  const incidentStore = requireOption(options, "incident-store");
  const journalStore = requireOption(options, "journal-store");
  const receiptStore = requireOption(options, "receipt-store");
  const bridgeCli = resolve(options["bridge-cli"] || resolve(root, "tools", "bifrost-bridge.mjs"));
  const recipientId = (process.env.DISCORD_OWNER_ID ?? "").trim();
  if (!recipientId) throw fail("recipient-missing");

  const runtime = loadRuntime();
  const { records, skipped, refused } = await readIncidents(runtime, incidentStore);
  if (records === null) return 0;

  const journal = await openJournal(runtime, journalStore);
  for (const entry of journal.cache.getAll(journal.definition)) {
    if (entry.status === "running") {
      await journal.put({ ...entry, status: "unknown", lastError: "interrupted-before-outcome", updatedAt: now() });
    }
  }

  const deliver = async (record, notice) => {
    const key = `${record.incidentKey}#${notice}`;
    const previous = journal.cache.get(journal.definition, key);
    const attempts = (previous?.attempts ?? 0) + 1;
    const nonce = createHash("sha256").update(key).digest("hex").slice(0, 25);
    const content = noticeText(record, notice);
    const base = { schemaVersion: JOURNAL_SCHEMA, incidentKey: record.incidentKey, notice, attempts, nonce };
    await journal.put({ ...base, status: "running", messageId: "", lastError: "", updatedAt: now() });
    const outcome = await sendNotice({ bridgeCli, recipientId, receiptStore, key, nonce, record, content });
    await journal.put({ ...base, messageId: outcome.messageId ?? "", status: outcome.status, lastError: outcome.error ?? "", updatedAt: now() });
  };
  const retryable = (entry) => !entry || (entry.status === "failed" && entry.attempts < MAX_ATTEMPTS);
  const entryFor = (record, notice) => journal.cache.get(journal.definition, `${record.incidentKey}#${notice}`);

  for (const record of records) {
    if (retryable(entryFor(record, "opened"))) await deliver(record, "opened");
    if (record.closedAt !== null && entryFor(record, "opened")?.status === "completed" && retryable(entryFor(record, "closed"))) {
      await deliver(record, "closed");
    }
  }

  const entries = journal.cache.getAll(journal.definition);
  const unknown = entries.filter((entry) => entry.status === "unknown").length;
  const exhausted = entries.filter(isExhausted).length;
  process.stdout.write(`${JSON.stringify({ skipped, refused, unknown, exhausted })}\n`);
  return unknown + exhausted + refused > 0 ? 1 : 0;
}

async function status(options) {
  const runtime = loadRuntime();
  const journal = await openJournal(runtime, requireOption(options, "journal-store"));
  const entries = journal.cache.getAll(journal.definition);
  const rows = entries
    .map(({ incidentKey, notice, status, attempts, updatedAt, lastError }) => ({ incidentKey, notice, status, attempts, updatedAt, lastError }))
    .sort((a, b) => (a.incidentKey + a.notice).localeCompare(b.incidentKey + b.notice));
  process.stdout.write(`${JSON.stringify(rows, null, 2)}\n`);
  return entries.some((entry) => entry.status === "unknown" || isExhausted(entry)) ? 1 : 0;
}

const isExhausted = (entry) => entry.status === "failed" && entry.attempts >= MAX_ATTEMPTS;
const now = () => new Date().toISOString();

// Reads P unlocked. An absent file is "nothing to do" (records: null). A
// record of another schema is counted as skipped; a record of this schema
// that breaks the contract is counted as refused. Neither is echoed.
async function readIncidents(runtime, path) {
  try {
    await stat(path);
  } catch (error) {
    if (error?.code === "ENOENT") return { records: null, skipped: 0, refused: 0 };
    throw fail("incident-store-unreadable");
  }
  let envelopes;
  try {
    envelopes = await new runtime.SingleFileMessagePackBackingStore(path).pullAll();
  } catch {
    throw fail("incident-store-unreadable");
  }
  const records = [];
  let skipped = 0;
  let refused = 0;
  for (const envelope of envelopes) {
    let tuple;
    try {
      tuple = runtime.decode(envelope.payload);
    } catch {
      skipped += 1;
      continue;
    }
    if (!Array.isArray(tuple) || tuple[0] !== INCIDENT_SCHEMA) {
      skipped += 1;
      continue;
    }
    const record = parseIncident(tuple);
    if (record) records.push(record);
    else refused += 1;
  }
  records.sort((a, b) => a.openedAt - b.openedAt || a.incidentKey.localeCompare(b.incidentKey));
  return { records, skipped, refused };
}

// [schema_version, incident_key, condition, subject, opened_at, closed_at, close_reason]
function parseIncident(tuple) {
  if (tuple.length !== 7) return null;
  const [, incidentKey, condition, subject, openedAt, closedAt, closeReason] = tuple;
  if (typeof condition !== "string" || !NAME.test(condition)) return null;
  if (typeof subject !== "string" || !SUBJECT.test(subject)) return null;
  if (!Number.isSafeInteger(openedAt) || openedAt <= 0 || openedAt > MAX_DATE_MS) return null;
  if (incidentKey !== `${condition}:${subject}:${openedAt}`) return null;
  if (closedAt === null) {
    if (closeReason !== null) return null;
  } else {
    if (!Number.isSafeInteger(closedAt) || closedAt < openedAt || closedAt > MAX_DATE_MS) return null;
    if (typeof closeReason !== "string" || !NAME.test(closeReason)) return null;
  }
  return { incidentKey, condition, subject, openedAt, closedAt, closeReason };
}

function noticeText(record, notice) {
  const opened = new Date(record.openedAt).toISOString();
  if (notice === "opened") return `Idunn incident opened: ${record.condition} on \`${record.subject}\` at ${opened}`;
  const closed = new Date(record.closedAt).toISOString();
  return `Idunn incident closed (${record.closeReason}): ${record.condition} on \`${record.subject}\`, opened ${opened}, closed ${closed}`;
}

// The one send primitive. The outcome is completed with a message id; failed
// only when the bridge reports exit 75 (known not sent); otherwise unknown. The
// error is a fixed code: bridge stdout and stderr never reach the journal.
async function sendNotice({ bridgeCli, recipientId, receiptStore, key, nonce, record, content }) {
  const dir = await mkdtemp(resolve(tmpdir(), "watchdog-notice-"));
  try {
    const contentFile = resolve(dir, "content.txt");
    await writeFile(contentFile, content, "utf8");
    const result = spawnSync(process.execPath, [
      bridgeCli, "discord-dm",
      "--recipient-id", recipientId,
      "--content-file", contentFile,
      "--nonce", nonce,
      "--cultmesh-command-id", key,
      "--receipt-store", receiptStore,
      "--source-kind", "idunn-operator-incident",
      "--source-id", record.incidentKey,
    ], { encoding: "utf8", env: process.env, windowsHide: true, timeout: BRIDGE_TIMEOUT_MS, stdio: ["ignore", "pipe", "ignore"] });
    if (result.status === BRIDGE_NOT_SENT) return { status: "failed", error: "bridge-not-sent" };
    if (result.error || result.status !== 0) return { status: "unknown", error: "bridge-exit-unknown" };
    try {
      const value = JSON.parse(result.stdout);
      if (value.action === "discord-dm" && value.ok === true && typeof value.messageId === "string" && value.messageId) {
        return { status: "completed", messageId: value.messageId };
      }
    } catch {
      // falls through to the fixed code
    }
    return { status: "unknown", error: "bridge-output-invalid" };
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}

async function openJournal(runtime, path) {
  const definition = runtime.defineDocumentType({
    type: JOURNAL_TYPE,
    schemaName: JOURNAL_TYPE,
    schemaId: JOURNAL_SCHEMA,
    schemaVersion: JOURNAL_SCHEMA,
    contentHash: JOURNAL_SCHEMA,
    global: false,
    schema: { parse: parseEntry },
    members: ["schemaVersion", "incidentKey", "notice", "status", "attempts", "nonce", "messageId", "lastError", "updatedAt"]
      .map((memberName, slot) => ({ slot, memberName, typeName: memberName === "attempts" ? "uint32" : "string" })),
  });
  const cache = runtime.CultCache.builder()
    .withDocumentType(definition)
    .withGenericStore(new runtime.SingleFileMessagePackBackingStore(path))
    .build();
  try {
    await cache.pullAllBackingStores();
  } catch {
    throw fail("journal-unreadable");
  }
  return { cache, definition, put: (entry) => cache.put(definition, `${entry.incidentKey}#${entry.notice}`, entry) };
}

function parseEntry(value) {
  const ok = value && value.schemaVersion === JOURNAL_SCHEMA
    && typeof value.incidentKey === "string" && value.incidentKey
    && (value.notice === "opened" || value.notice === "closed")
    && STATUSES.includes(value.status)
    && Number.isSafeInteger(value.attempts) && value.attempts >= 0
    && ["nonce", "messageId", "lastError", "updatedAt"].every((field) => typeof value[field] === "string");
  if (!ok) throw fail("journal-entry-invalid");
  return value;
}

function loadRuntime() {
  try {
    const cache = createRequire(resolve(cult, "packages", "cultcache-ts", "package.json"));
    const net = createRequire(resolve(cult, "packages", "cultnet-ts", "package.json"));
    const cc = cache("@gamecult/cultcache-ts");
    return {
      CultCache: cc.CultCache,
      SingleFileMessagePackBackingStore: cc.SingleFileMessagePackBackingStore,
      defineDocumentType: cc.defineDocumentType,
      decode: net("@msgpack/msgpack").decode,
    };
  } catch {
    throw fail("cultlib-unavailable");
  }
}

function parseArgs(args) {
  const options = {};
  for (let i = 0; i < args.length; i += 2) {
    if (!args[i].startsWith("--") || args[i + 1] === undefined) throw fail("bad-arguments");
    options[args[i].slice(2)] = args[i + 1];
  }
  return options;
}

function requireOption(options, name) {
  if (!options[name]) throw fail(`missing-option-${name}`);
  return options[name];
}

try {
  process.exitCode = await main(process.argv.slice(2));
} catch (error) {
  process.stderr.write(`watchdog-notice: ${error instanceof NoticeError ? error.message : "unexpected-error"}\n`);
  process.exitCode = 1;
}

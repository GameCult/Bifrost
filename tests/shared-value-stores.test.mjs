// Six document types once declared `indexes:` on grouping fields (status, repo, agent, topic...) that many rows share.
// CultLib's `indexes:` means unique, so each store must still write, close and reopen with rows that share those values.
// Real CLIs and store openers against temp files; CultLib comes from tests/cultlib-root.mjs.
import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { cultLibRoot } from "./cultlib-root.mjs";
import { openCrossingReceiptStore } from "../tools/bifrost-crossing-documents.mjs";
import { buildAuthorizedRelease, openRepositoryReleaseAuthorityStore } from "../tools/bifrost-repository-release-authority.mjs";

const root = resolve(import.meta.dirname, "..");
const dir = mkdtempSync(resolve(tmpdir(), "bifrost-shared-"));
test.after(() => rmSync(dir, { recursive: true, force: true }));

const env = {
  ...process.env,
  VOIDBOT_CULTLIB_ROOT: cultLibRoot,
  BIFROST_SKIP_LOCAL_ENV: "true",
  BIFROST_ROOT_VERSE: "test-verse",
  BIFROST_MACHINE_ID: "test-machine",
  BIFROST_CROSSING_RECEIPT_STORE: resolve(dir, "receipts.cc"),
};
for (const name of ["BIFROST_BRIDGE_BASE_URL", "BIFROST_BRIDGE_TOKEN", "BIFROST_ALLOW_UNRECEIPTED_ACTIVITY"]) delete env[name];

function cli(tool, args) {
  const run = spawnSync(process.execPath, [resolve(root, "tools", tool), ...args], { env, encoding: "utf8", cwd: root });
  assert.equal(run.status, 0, `${tool} ${args[0]}\n${run.stderr}`);
  return run.stdout;
}

test("agent-transport store holds requests that share repo, agent and status", () => {
  const store = resolve(dir, "agent-transport.cc");
  for (const id of ["req-1", "req-2", "req-3"]) {
    cli("agent-transport.mjs", ["enqueue", "--store", store, "--id", id, "--repo", "Bifrost", "--agent", "heimdall", "--title", id, "--request", "body", "--allow-unmirrored", "true"]);
  }
  const listed = JSON.parse(cli("agent-transport.mjs", ["list", "--store", store]));
  assert.deepEqual(listed.map((request) => request.id).sort(), ["req-1", "req-2", "req-3"]);
});

test("governance-threads store holds topics and comments that share their grouping fields", () => {
  const store = resolve(dir, "governance.cc");
  for (const id of ["topic-1", "topic-2"]) {
    cli("governance-threads.mjs", ["open", "--store", store, "--id", id, "--title", id, "--repo", "Bifrost", "--agent", "heimdall", "--summary", "s", "--allow-unmirrored", "true"]);
  }
  for (const id of ["c-1", "c-2"]) {
    cli("governance-threads.mjs", ["comment", "--store", store, "--topic", "topic-1", "--id", id, "--author", "heimdall", "--stance", "support", "--body", "same", "--allow-unmirrored", "true"]);
  }
  const topics = JSON.parse(cli("governance-threads.mjs", ["list", "--store", store]));
  assert.deepEqual(topics.map((topic) => topic.id).sort(), ["topic-1", "topic-2"]);
  const shown = JSON.parse(cli("governance-threads.mjs", ["show", "--store", store, "--topic", "topic-1"]));
  assert.equal(JSON.stringify(shown).match(/"id": ?"c-\d"/g)?.length ?? 0, 2);
});

test("crossing receipt store reopens with receipts that share status, kind and command", async () => {
  const receipts = await openCrossingReceiptStore(env.BIFROST_CROSSING_RECEIPT_STORE);
  const before = receipts.getAll().length;
  assert.ok(before >= 5, "the CLI runs above leave completed agent_transport.request receipts");
  const reopened = await openCrossingReceiptStore(env.BIFROST_CROSSING_RECEIPT_STORE);
  assert.equal(reopened.getAll().length, before);
  assert.ok(new Set(reopened.getAll().map((receipt) => `${receipt.status}/${receipt.crossingKind}`)).size < before);
});

test("release authority store reopens with authorities that share repository, ref and status", async () => {
  const path = resolve(dir, "release-authority.cc");
  const authority = (commitSha) => buildAuthorizedRelease({
    repositoryFullName: "GameCult/Bifrost", upstreamRef: "refs/heads/main", commitSha,
    commandId: `cmd-${commitSha[0]}`, crossingReceiptId: `crossing-${commitSha[0]}`, policyDecisionId: "policy", authorityReference: "ref",
    actorIdentity: "heimdall", sourceKind: "test", sourceId: "src", epiphanyRunId: "run", epiphanyLaneId: "lane", epiphanyAgentIdentity: "heimdall",
    externalReceiptUrl: "https://example.test/r", externalReceiptId: "r", authorizedAt: "2026-11-11T00:00:00.000Z",
  });
  const store = await openRepositoryReleaseAuthorityStore(path);
  await store.put(authority("a".repeat(40)));
  await store.put(authority("b".repeat(40)));
  const reopened = await openRepositoryReleaseAuthorityStore(path);
  assert.equal(reopened.getAll().length, 2);
});

test("provider advertisement store exports and re-exports beside other documents", () => {
  const out = resolve(dir, "provider.cc");
  cli("provider-advertisement.mjs", ["export", "--out", out]);
  const second = JSON.parse(cli("provider-advertisement.mjs", ["export", "--out", out]));
  assert.equal(second.ok, true);
});

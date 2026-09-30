import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import {
  discordPostCommandDefinition,
  discordPostCommandDocumentType,
  discordPostCommandSchemaId,
} from "../tools/bifrost-discord-command-documents.mjs";

const root = resolve(import.meta.dirname, "..");
const cultLib = resolve(process.env.VOIDBOT_CULTLIB_ROOT || resolve(root, "..", "CultLib"));
const tool = resolve(root, "tools", "cultmesh-bridge-commands.mjs");
const canary = "CANARY-9f3a1c77";

function loadRuntime() {
  const mesh = resolve(cultLib, "packages", "cultmesh-ts", "dist", "index.js");
  const cache = resolve(cultLib, "packages", "cultcache-ts", "dist", "index.js");
  const requireCult = createRequire(mesh);
  return { CultMesh: requireCult(mesh).CultMesh, defineDocumentType: requireCult(cache).defineDocumentType };
}

async function putCommand(store, commandId, command) {
  const { CultMesh, defineDocumentType } = loadRuntime();
  const definition = discordPostCommandDefinition(defineDocumentType);
  const node = await CultMesh.createNode(store, { documents: [definition] });
  await node.put(definition, commandId, {
    schemaName: discordPostCommandDocumentType,
    schemaVersion: discordPostCommandSchemaId,
    commandId,
    command,
    status: "pending",
    requestedBy: "test",
    requestedAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    source: { kind: "test", id: commandId },
    payload: { channelId: "123", content: canary },
  });
  await node.flush?.();
}

function run(...args) {
  const env = { ...process.env, BIFROST_SKIP_LOCAL_ENV: "true", VOIDBOT_CULTLIB_ROOT: cultLib };
  delete env.BIFROST_DISCORD_BOT_TOKEN;
  delete env.DISCORD_BOT_TOKEN;
  const result = spawnSync(process.execPath, [tool, ...args], { cwd: root, encoding: "utf8", env });
  return { ...result, json: JSON.parse(result.stdout) };
}

test("a non-discord-post command is refused with a failed receipt, never left pending", async () => {
  const store = resolve(await mkdtemp(resolve(tmpdir(), "bifrost-pump-")), "store.cc");
  await putCommand(store, "dm1", "discord-dm");

  const processed = run("process", "--store", store);
  assert.equal(processed.json.commandCount, 1);
  assert.equal(processed.json.ok, false);
  assert.equal(processed.json.processed[0].status, "failed");

  const receipt = run("receipt", "--store", store, "--command-id", "dm1").json;
  assert.equal(receipt.ok, false);
  assert.equal(receipt.receipt.status, "failed");
  assert.equal(receipt.receipt.ok, false);
  assert.match(receipt.receipt.error, /only discord-post is supported/);

  const again = run("process", "--store", store);
  assert.equal(again.json.commandCount, 0, "a refused command is not pending or running afterwards");
});

test("a refusal echoes no input value", async () => {
  const store = resolve(await mkdtemp(resolve(tmpdir(), "bifrost-pump-")), "store.cc");
  await putCommand(store, "dm2", `${canary}-verb`);
  const processed = run("process", "--store", store, "--command-id", "dm2");
  assert.equal(processed.json.processed[0].status, "failed");
  assert.equal(processed.json.processed[0].error.includes(canary), false);
});

test("a discord-post command still reaches the actuator", async () => {
  const store = resolve(await mkdtemp(resolve(tmpdir(), "bifrost-pump-")), "store.cc");
  await putCommand(store, "post1", "discord-post");
  const processed = run("process", "--store", store);
  assert.equal(processed.json.commandCount, 1);
  const receipt = processed.json.processed[0];
  assert.equal(receipt.action, "discord-post");
  assert.match(receipt.error, /Bifrost Discord actuator failed/);
  assert.doesNotMatch(receipt.error, /only discord-post is supported/);
});

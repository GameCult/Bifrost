#!/usr/bin/env node
import { createRequire } from "node:module";
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdir, writeFile, rm } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { crossingReceiptDefinition } from "./bifrost-crossing-documents.mjs";
import {
  discordPostCommandDefinition,
  discordPostCommandDocumentType as commandDocumentType,
  discordPostCommandSchemaId as commandSchemaId,
  discordPostReceiptDefinition,
  discordPostReceiptDocumentType as receiptDocumentType,
  discordPostReceiptSchemaId as receiptSchemaId,
} from "./bifrost-discord-command-documents.mjs";

const repoRoot = resolve(import.meta.dirname, "..");
const projectsRoot = resolve(repoRoot, "..");
const cultLibRoot = resolve(process.env.VOIDBOT_CULTLIB_ROOT || resolve(projectsRoot, "CultLib"));
const defaultStorePath = resolve(repoRoot, ".bifrost", "provider-store.cc");

async function main() {
  loadBifrostLocalEnv();
  const [command, ...rawArgs] = process.argv.slice(2);
  const options = parseArgs(rawArgs);

  if (!command || command === "help" || command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  switch (command) {
    case "process":
      await processCommands(options);
      return;
    case "write-smoke":
      await writeSmokeCommand(options);
      return;
    case "receipt":
      await printReceipt(options);
      return;
    case "delete":
      await deleteCommand(options);
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/cultmesh-bridge-commands.mjs help".`);
  }
}

async function processCommands(options) {
  const storePath = resolve(options.store ?? defaultStorePath);
  const commandIdFilter = optionalString(options["command-id"]);
  const node = await openCommandNode(storePath);
  await node.cache?.pullAllBackingStores?.();
  const commands = listCommandRecords(node)
    .filter((command) => command.status === "pending" || command.status === "running")
    .filter((command) => !commandIdFilter || command.commandId === commandIdFilter)
    .sort((left, right) => String(left.createdAt).localeCompare(String(right.createdAt)));

  const processed = [];
  for (const command of commands) {
    processed.push(await processOneCommand(node, command, options));
  }
  await node.flush?.();

  printJson({
    ok: processed.every((receipt) => receipt.status === "completed"),
    action: "process",
    storePath,
    commandCount: commands.length,
    processed,
  });
}

async function writeSmokeCommand(options) {
  const storePath = resolve(options.store ?? defaultStorePath);
  const commandId = optionalString(options["command-id"]) ?? `bifrost-smoke-${Date.now()}`;
  const command = {
    schemaName: commandDocumentType,
    schemaVersion: commandSchemaId,
    commandId,
    command: "discord-post",
    status: "pending",
    requestedBy: "bifrost-smoke",
    requestedAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    source: {
      kind: "smoke",
      id: commandId,
    },
    payload: {
      channelId: requireOption(options, "channel-id"),
      content: optionalString(options.content) ?? "Bifrost CultMesh Discord command smoke.",
      personaName: optionalString(options["persona-name"]),
      personaAvatarUrl: optionalString(options["persona-avatar-url"]),
      replyToMessageId: optionalString(options["reply-to-message-id"]),
    },
  };
  const node = await openCommandNode(storePath);
  await node.put(commandDefinition(), command.commandId, command);
  await node.flush?.();
  printJson({ ok: true, action: "write-smoke", storePath, commandId });
}

async function printReceipt(options) {
  const commandId = requireOption(options, "command-id");
  const storePath = resolve(options.store ?? defaultStorePath);
  const node = await openCommandNode(storePath);
  await node.cache?.pullAllBackingStores?.();
  const receipt = unwrapRecord(await node.get(receiptDefinition(), commandId));
  if (!receipt) {
    printJson({ ok: false, commandId, receipt: null });
    return;
  }
  printJson({ ok: receipt.status === "completed", commandId, receipt });
}

async function deleteCommand(options) {
  const commandId = requireOption(options, "command-id");
  const storePath = resolve(options.store ?? defaultStorePath);
  const node = await openCommandNode(storePath);
  await node.cache?.pullAllBackingStores?.();
  const deletedCommand = typeof node.cache?.delete === "function"
    ? await node.cache.delete(commandDefinition(), commandId)
    : false;
  const deletedReceipt = typeof node.cache?.delete === "function"
    ? await node.cache.delete(receiptDefinition(), commandId)
    : false;
  await node.flush?.();
  printJson({ ok: Boolean(deletedCommand || deletedReceipt), action: "delete", storePath, commandId, deletedCommand, deletedReceipt });
}

async function processOneCommand(node, command, options) {
  const running = {
    ...command,
    status: "running",
    updatedAt: new Date().toISOString(),
  };
  await node.put(commandDefinition(), command.commandId, running);

  const payload = command.payload && typeof command.payload === "object" ? command.payload : {};
  const tempDir = resolve(repoRoot, ".bifrost", "cultmesh-command-payloads", command.commandId);
  const contentPath = resolve(tempDir, "content.md");
  await mkdir(tempDir, { recursive: true });
  await writeFile(contentPath, requireString(payload.content, "payload.content"), "utf8");

  try {
    const args = [
      "tools/bifrost-bridge.mjs",
      "discord-post",
      "--channel-id",
      requireString(payload.channelId, "payload.channelId"),
      "--content-file",
      contentPath,
      "--cultmesh-command-id",
      command.commandId,
      "--source-kind",
      optionalString(command.source?.kind) ?? "cultmesh-command",
      "--source-id",
      optionalString(command.source?.id) ?? command.commandId,
      "--identity",
      optionalString(command.actor?.id) ?? optionalString(payload.identityId) ?? optionalString(payload.personaName) ?? "bifrost",
    ];
    pushOption(args, "--persona-name", payload.personaName);
    pushOption(args, "--persona-avatar-url", payload.personaAvatarUrl);
    pushOption(args, "--reply-to-message-id", payload.replyToMessageId);
    pushOption(args, "--receipt-store", options["receipt-store"]);

    const result = spawnSync(process.execPath, args, {
      cwd: repoRoot,
      encoding: "utf8",
      windowsHide: true,
      env: {
        ...process.env,
        BIFROST_CULTMESH_COMMAND_ID: command.commandId,
      },
    });
    if (result.status !== 0 || result.error) {
      throw new Error(renderSpawnFailure(result));
    }
    const posted = parseJson(result.stdout, "bifrost bridge discord-post receipt");
    const receipt = buildReceipt(command, {
      status: "completed",
      ok: true,
      action: "discord-post",
      channelId: posted.channelId,
      messageId: posted.messageId,
      transport: posted.transport,
      url: posted.url,
      canonicalReceiptId: posted.crossingReceiptId,
      payload: posted,
    });
    await node.put(receiptDefinition(), command.commandId, receipt);
    await node.put(commandDefinition(), command.commandId, {
      ...running,
      status: "completed",
      updatedAt: receipt.completedAt,
      receiptId: receipt.receiptId,
    });
    return receipt;
  } catch (error) {
    const receipt = buildReceipt(command, {
      status: "failed",
      ok: false,
      action: "discord-post",
      canonicalReceiptId: `crossing_${command.commandId}`,
      error: error instanceof Error ? error.message : String(error),
    });
    await node.put(receiptDefinition(), command.commandId, receipt);
    await node.put(commandDefinition(), command.commandId, {
      ...running,
      status: "failed",
      updatedAt: receipt.completedAt,
      receiptId: receipt.receiptId,
      error: receipt.error,
    });
    return receipt;
  } finally {
    await rm(tempDir, { recursive: true, force: true }).catch(() => {});
  }
}

function buildReceipt(command, result) {
  const completedAt = new Date().toISOString();
  return {
    schemaName: receiptDocumentType,
    schemaVersion: receiptSchemaId,
    receiptId: command.commandId,
    commandId: command.commandId,
    command: command.command,
    status: result.status,
    ok: result.ok,
    action: result.action,
    channelId: result.channelId ?? command.payload?.channelId ?? "",
    messageId: result.messageId ?? "",
    transport: result.transport ?? "",
    url: result.url ?? "",
    canonicalReceiptId: result.canonicalReceiptId ?? "",
    error: result.error ?? "",
    requestedAt: command.requestedAt ?? command.createdAt ?? "",
    completedAt,
    source: command.source ?? {},
    actor: command.actor ?? {},
    payload: result.payload ?? {},
  };
}

function listCommandRecords(node) {
  if (typeof node.cache?.getAll !== "function") {
    return [];
  }
  return node.cache.getAll(commandDefinition())
    .map(unwrapRecord)
    .filter((entry) => entry && entry.command === "discord-post" && entry.commandId);
}

async function openCommandNode(storePath) {
  const { CultMesh, defineDocumentType } = loadCultMeshRuntime();
  return CultMesh.createNode(storePath, {
    documents: [
      commandDefinition(defineDocumentType),
      receiptDefinition(defineDocumentType),
      crossingReceiptDefinition(defineDocumentType),
      genericDocument(defineDocumentType, "gamecult.eve.provider_advertisement", "gamecult.eve.provider_advertisement.v1", "providerId"),
      genericDocument(defineDocumentType, "gamecult.eve.surface_state", "gamecult.eve.surface_state.v1", "providerId"),
      genericDocument(defineDocumentType, "gamecult.eve.interface_binding", "gamecult.eve.interface_binding.v1", "bindingId"),
    ],
  });
}

function commandDefinition(defineDocumentTypeInput) {
  const defineDocumentType = defineDocumentTypeInput ?? loadCultMeshRuntime().defineDocumentType;
  return discordPostCommandDefinition(defineDocumentType);
}

function receiptDefinition(defineDocumentTypeInput) {
  const defineDocumentType = defineDocumentTypeInput ?? loadCultMeshRuntime().defineDocumentType;
  return discordPostReceiptDefinition(defineDocumentType);
}

function genericDocument(defineDocumentType, type, schemaId, name) {
  return defineDocumentType({
    type,
    schemaName: type,
    schemaId,
    schemaVersion: schemaId,
    contentHash: schemaId,
    global: false,
    name,
    schema: parseObjectDocument(type),
  });
}

function loadCultMeshRuntime() {
  const candidates = [
    resolve(cultLibRoot, "packages", "cultmesh-ts", "package.json"),
    resolve(projectsRoot, "CultMeshTS", "package.json"),
  ];

  for (const packageJson of candidates) {
    if (!existsSync(packageJson)) {
      continue;
    }
    try {
      const requireCult = createRequire(packageJson);
      const { CultMesh } = requireCult("cultmesh-ts");
      const { defineDocumentType } = requireCult("cultcache-ts");
      if (CultMesh && defineDocumentType) {
        return { CultMesh, defineDocumentType };
      }
    } catch {
    }
  }

  throw new Error("CultMesh/CultCache TypeScript runtime is unavailable.");
}

function parseObjectDocument(label) {
  return {
    parse(input) {
      if (!input || typeof input !== "object") {
        throw new Error(`${label} must be an object.`);
      }
      return input;
    },
  };
}

function loadBifrostLocalEnv() {
  if (process.env.BIFROST_SKIP_LOCAL_ENV === "true") {
    return;
  }
  loadLocalEnv(resolve(repoRoot, ".env"));
  loadLocalEnv(resolve(projectsRoot, "VoidBot", ".env"));
}

function loadLocalEnv(path) {
  if (!existsSync(path)) {
    return;
  }
  const content = readFileSync(path, "utf8");
  for (const rawLine of content.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#")) {
      continue;
    }
    const match = line.match(/^([A-Za-z_][A-Za-z0-9_]*)=(.*)$/);
    if (!match || process.env[match[1]] !== undefined) {
      continue;
    }
    process.env[match[1]] = match[2].replace(/^"(.*)"$/, "$1");
  }
}

function parseArgs(args) {
  const options = {};
  for (let index = 0; index < args.length; index += 1) {
    const token = args[index];
    if (!token.startsWith("--")) {
      throw new Error(`Unexpected argument "${token}".`);
    }
    const key = token.slice(2);
    const next = args[index + 1];
    if (!next || next.startsWith("--")) {
      options[key] = "true";
      continue;
    }
    options[key] = next;
    index += 1;
  }
  return options;
}

function requireOption(options, name) {
  const value = optionalString(options[name]);
  if (!value) {
    throw new Error(`Missing required option --${name}.`);
  }
  return value;
}

function requireString(value, name) {
  const text = optionalString(value);
  if (!text) {
    throw new Error(`Missing required ${name}.`);
  }
  return text;
}

function optionalString(value) {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function pushOption(args, name, value) {
  const text = optionalString(value);
  if (text) {
    args.push(name, text);
  }
}

function unwrapRecord(record) {
  const candidate = Array.isArray(record) && record.length === 1 ? record[0] : record?.value ?? record;
  return candidate && typeof candidate === "object" ? candidate : null;
}

function parseJson(value, label) {
  try {
    return JSON.parse(value);
  } catch {
    throw new Error(`${label} returned non-JSON output: ${value}`);
  }
}

function renderSpawnFailure(result) {
  const stderr = typeof result.stderr === "string" ? result.stderr.trim() : "";
  const stdout = typeof result.stdout === "string" ? result.stdout.trim() : "";
  const error = result.error instanceof Error ? result.error.message : "";
  return [
    `Bifrost Discord actuator failed with status ${result.status ?? "unknown"}.`,
    error,
    stderr,
    stdout,
  ].filter(Boolean).join(" ");
}

function printJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

function printHelp() {
  process.stdout.write(`Bifrost CultMesh bridge commands

Usage:
  node tools/cultmesh-bridge-commands.mjs process [--store <path>] [--command-id <id>]
  node tools/cultmesh-bridge-commands.mjs receipt --command-id <id> [--store <path>]
  node tools/cultmesh-bridge-commands.mjs delete --command-id <id> [--store <path>]
  node tools/cultmesh-bridge-commands.mjs write-smoke --channel-id <id> [--store <path>]

The command request document is ${commandDocumentType} / ${commandSchemaId}.
The receipt document is ${receiptDocumentType} / ${receiptSchemaId}.
`);
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});

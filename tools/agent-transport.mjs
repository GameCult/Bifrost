#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { randomUUID } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, "..");
const projectsRoot = resolve(repoRoot, "..");
const defaultStorePath = resolve(repoRoot, ".bifrost", "agent-transport.cc");
const bridgeCli = resolve(repoRoot, "tools", "bifrost-bridge.mjs");
const defaultPersonaName = "Bifrost";
const defaultPersonaAvatarUrl =
  "https://raw.githubusercontent.com/GameCult/Bifrost/main/src/Bifrost.Web/wwwroot/img/bifrost-profile.png";

const cultCacheRequire = createRequire(resolve(projectsRoot, "CultCacheTS", "package.json"));
const cultNetRequire = createRequire(resolve(projectsRoot, "CultNetTS", "package.json"));

const {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentType,
} = cultCacheRequire(resolve(projectsRoot, "CultCacheTS", "dist", "index.js"));
const {
  CultNetDocumentRegistry,
  defineCultNetDocumentBinding,
} = cultNetRequire(resolve(projectsRoot, "CultNetTS", "dist", "index.js"));
const { encode, decode } = cultNetRequire("@msgpack/msgpack");

const documentType = "bifrost.agent-transport.update-request";
const schemaId = "bifrost.agent-transport.update-request.v0";
const validStatuses = new Set(["queued", "claimed", "completed", "cancelled"]);
const validCloseStatuses = new Set(["completed", "cancelled"]);

const updateRequestDefinition = defineDocumentType({
  type: documentType,
  schemaId,
  schemaName: documentType,
  schemaVersion: "bifrost.agent_transport.update_request.v0",
  schema: {
    parse: parseUpdateRequest,
  },
  name: "id",
  indexes: {
    targetRepoName: "targetRepoName",
    targetAgentIdentity: "targetAgentIdentity",
    status: "status",
  },
  members: [
    { slot: 1, memberName: "id", typeName: "string", isName: true },
    { slot: 2, memberName: "targetRepoName", typeName: "string", indexAlias: "targetRepoName" },
    { slot: 3, memberName: "targetRepositoryFullName", typeName: "string" },
    { slot: 4, memberName: "targetAgentIdentity", typeName: "string", indexAlias: "targetAgentIdentity" },
    { slot: 5, memberName: "title", typeName: "string" },
    { slot: 6, memberName: "requestMarkdown", typeName: "string" },
    { slot: 7, memberName: "priority", typeName: "number" },
    { slot: 8, memberName: "status", typeName: "string", indexAlias: "status" },
    { slot: 9, memberName: "sourceKind", typeName: "string" },
    { slot: 10, memberName: "sourceChannelId", typeName: "string" },
    { slot: 11, memberName: "sourceMessageIds", typeName: "string", isMany: true },
    { slot: 12, memberName: "sourcePacketPath", typeName: "string" },
    { slot: 13, memberName: "sourcePromptPath", typeName: "string" },
    { slot: 14, memberName: "createdByAgent", typeName: "string" },
    { slot: 15, memberName: "claimedByAgent", typeName: "string" },
    { slot: 16, memberName: "closeNote", typeName: "string" },
    { slot: 17, memberName: "createdAt", typeName: "string" },
    { slot: 18, memberName: "updatedAt", typeName: "string" },
    { slot: 19, memberName: "claimedAt", typeName: "string" },
    { slot: 20, memberName: "closedAt", typeName: "string" },
  ],
});

const cultNetRegistry = new CultNetDocumentRegistry([
  defineCultNetDocumentBinding({
    definition: updateRequestDefinition,
    payloadSchemaVersion: "bifrost.agent_transport.update_request.v0",
  }),
]);

async function main() {
  loadLocalEnv(resolve(repoRoot, ".env"));
  loadLocalEnv(resolve(projectsRoot, "VoidBot", ".env"));
  const [command, ...rawArgs] = process.argv.slice(2);
  const options = parseArgs(rawArgs);

  if (!command || command === "help" || command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  const storePath = resolveOptionPath(options.store ?? defaultStorePath);
  const cache = await openCache(storePath);

  switch (command) {
    case "enqueue":
      await enqueue(cache, options);
      return;
    case "list":
      listRequests(cache, options);
      return;
    case "claim":
      await claim(cache, options);
      return;
    case "release":
      await releaseRequest(cache, options);
      return;
    case "close":
      await closeRequest(cache, options);
      return;
    case "snapshot":
      await writeSnapshot(cache, options);
      return;
    case "apply-snapshot":
      await applySnapshot(cache, options);
      return;
    case "schema":
      printJson({ documentType, schemaId });
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/agent-transport.mjs help".`);
  }
}

async function openCache(storePath) {
  const cache = CultCache.builder()
    .withDocumentType(updateRequestDefinition)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.pullAllBackingStores();
  return cache;
}

async function enqueue(cache, options) {
  const now = new Date().toISOString();
  const requestMarkdown = await readRequestMarkdown(options);
  const request = parseUpdateRequest({
    id: options.id ?? `req_${randomUUID()}`,
    targetRepoName: requireOption(options, "repo"),
    targetRepositoryFullName: optionalString(options["repo-full-name"]),
    targetAgentIdentity: optionalString(options.agent),
    title: requireOption(options, "title"),
    requestMarkdown,
    priority: parseInteger(options.priority ?? "50", "priority"),
    status: "queued",
    sourceKind: optionalString(options["source-kind"]) ?? "manual",
    sourceChannelId: optionalString(options["source-channel-id"]),
    sourceMessageIds: parseCsv(options["source-message-ids"]),
    sourcePacketPath: optionalString(options["packet-path"]),
    sourcePromptPath: optionalString(options["prompt-path"]),
    createdByAgent: optionalString(options["created-by"]),
    claimedByAgent: undefined,
    closeNote: undefined,
    createdAt: now,
    updatedAt: now,
    claimedAt: undefined,
    closedAt: undefined,
  });

  await mirrorEnqueuedRequestOrThrow(request, options);
  await cache.put(updateRequestDefinition, request.id, request);
  printJson(request);
}

async function mirrorEnqueuedRequestOrThrow(request, options) {
  const channelId = resolveMirrorChannelId(options);
  if (!channelId) {
    if (allowsUnmirrored(options)) {
      return;
    }
    throw new Error(
      "Bifrost update requests require a Discord mirror. Set BIFROST_DISCORD_CHANNEL_ID, pass --mirror-channel-id, or use --allow-unmirrored true only for explicit fixtures.",
    );
  }

  const mirrorContent =
    await readOptionalTextOption(options, "mirror-content") ??
    renderRequestMirrorFallback(request);
  const personaName = optionalString(options["mirror-persona-name"])
    ?? optionalString(process.env.BIFROST_DISCORD_PERSONA_NAME)
    ?? defaultPersonaName;
  const personaAvatarUrl =
    optionalString(options["mirror-persona-avatar-url"]) ??
    optionalString(process.env.BIFROST_DISCORD_PERSONA_AVATAR_URL) ??
    optionalString(process.env.DISCORD_PERSONA_AVATAR_URL_BIFROST) ??
    defaultPersonaAvatarUrl;

  runNodeJson([
    bridgeCli,
    "discord-post",
    "--channel-id", channelId,
    "--content", mirrorContent,
    "--persona-name", personaName,
    ...optionalArg("--persona-avatar-url", personaAvatarUrl),
    ...optionalArg("--reply-to-message-id", options["mirror-reply-to-message-id"]),
    ...(options["mirror-dry-run"] === "true" ? ["--dry-run", "true"] : []),
  ], repoRoot);
}

function resolveMirrorChannelId(options) {
  return (
    optionalString(options["mirror-channel-id"]) ??
    optionalString(process.env.BIFROST_DISCORD_CHANNEL_ID) ??
    optionalString(process.env.DISCORD_BIFROST_CHANNEL_ID)
  );
}

function allowsUnmirrored(options) {
  return options["allow-unmirrored"] === "true" || process.env.BIFROST_ALLOW_UNMIRRORED_GOVERNANCE === "true";
}

function renderRequestMirrorFallback(request) {
  const actor = request.createdByAgent ? `${request.createdByAgent} queued` : "Queued";
  return [
    `Bifrost intake: ${actor} a work request.`,
    "",
    `**${request.targetRepoName}: ${request.title}**`,
    "",
    summarizeRequestMarkdown(request.requestMarkdown),
    "",
    `Request: \`${request.id}\``,
  ].join("\n");
}

function summarizeRequestMarkdown(markdown) {
  const text = String(markdown);
  const requestSection = text.match(/## Request\s+([\s\S]*?)(?:\n## |\n```|$)/i)?.[1]?.trim();
  const firstUsefulBlock = (requestSection ?? text)
    .split(/\n\s*\n/)
    .map((block) => block.replace(/\s+/g, " ").trim())
    .find((block) => block && !block.startsWith("#"));
  return truncate(firstUsefulBlock ?? "A Bifrost update request was queued for Codex dispatch.", 360);
}

function truncate(value, maxLength) {
  return value.length > maxLength ? `${value.slice(0, maxLength - 3)}...` : value;
}

function listRequests(cache, options) {
  const requests = filteredRequests(cache, options)
    .sort(compareRequests);

  printJson(requests);
}

async function claim(cache, options) {
  const repo = optionalString(options.repo);
  if (!repo) {
    throw new Error("claim requires --repo so an agent cannot grab work outside its jurisdiction.");
  }

  const agent = optionalString(options.agent);
  const claimedBy = optionalString(options["claimed-by"]) ?? agent ?? "codex";
  const request = filteredRequests(cache, {
    ...options,
    status: "queued",
  })
    .filter((candidate) => equalsIgnoreCase(candidate.targetRepoName, repo))
    .filter((candidate) => !agent || equalsIgnoreCase(candidate.targetAgentIdentity, agent) || !candidate.targetAgentIdentity)
    .sort(compareRequests)[0];

  if (!request) {
    printJson(null);
    return;
  }

  const now = new Date().toISOString();
  const claimed = parseUpdateRequest({
    ...request,
    status: "claimed",
    claimedByAgent: claimedBy,
    claimedAt: now,
    updatedAt: now,
  });
  await cache.put(updateRequestDefinition, claimed.id, claimed);
  printJson(claimed);
}

async function closeRequest(cache, options) {
  const id = requireOption(options, "id");
  const status = requireOption(options, "status");
  if (!validCloseStatuses.has(status)) {
    throw new Error("--status for close must be completed or cancelled.");
  }

  const current = cache.get(updateRequestDefinition, id);
  if (!current) {
    throw new Error(`No update request found for id "${id}".`);
  }

  const now = new Date().toISOString();
  const closed = parseUpdateRequest({
    ...current,
    status,
    closeNote: optionalString(options.note),
    closedAt: now,
    updatedAt: now,
  });
  await cache.put(updateRequestDefinition, closed.id, closed);
  printJson(closed);
}

async function releaseRequest(cache, options) {
  const id = requireOption(options, "id");
  const current = cache.get(updateRequestDefinition, id);
  if (!current) {
    throw new Error(`No update request found for id "${id}".`);
  }

  if (current.status !== "claimed") {
    throw new Error(`Only claimed requests can be released; "${id}" is ${current.status}.`);
  }

  const now = new Date().toISOString();
  const released = parseUpdateRequest({
    ...current,
    status: "queued",
    claimedByAgent: undefined,
    claimedAt: undefined,
    closeNote: optionalString(options.note),
    updatedAt: now,
  });
  await cache.put(updateRequestDefinition, released.id, released);
  printJson(released);
}

async function writeSnapshot(cache, options) {
  const message = cultNetRegistry.createRawSnapshotResponse(
    cache,
    options["message-id"] ?? `snapshot_${randomUUID()}`,
  );

  if (options.out) {
    const outPath = resolveOptionPath(options.out);
    await mkdir(dirname(outPath), { recursive: true });
    await writeFile(outPath, encode(message));
    printJson({
      schemaVersion: message.schemaVersion,
      messageId: message.messageId,
      documentCount: message.documents.length,
      out: outPath,
    });
    return;
  }

  printJson({
    ...message,
    documents: message.documents.map((document) => ({
      ...document,
      payload: Buffer.from(document.payload).toString("base64url"),
      payloadEncoding: "messagepack+base64url",
    })),
  });
}

async function applySnapshot(cache, options) {
  const inPath = resolveOptionPath(requireOption(options, "in"));
  const message = decode(await readFile(inPath));
  await cultNetRegistry.applyRawSnapshotResponse(cache, message);
  printJson({
    applied: true,
    schemaVersion: message.schemaVersion,
    messageId: message.messageId,
    documentCount: message.documents?.length ?? 0,
    in: inPath,
  });
}

function filteredRequests(cache, options) {
  return cache.getAll(updateRequestDefinition)
    .filter((request) => !options.status || request.status === options.status)
    .filter((request) => !options.repo || equalsIgnoreCase(request.targetRepoName, options.repo))
    .filter((request) => !options.agent || equalsIgnoreCase(request.targetAgentIdentity, options.agent));
}

function compareRequests(left, right) {
  if (right.priority !== left.priority) {
    return right.priority - left.priority;
  }

  return left.createdAt.localeCompare(right.createdAt);
}

async function readRequestMarkdown(options) {
  if (options["request-file"]) {
    return readFile(resolveOptionPath(options["request-file"]), "utf8");
  }

  return requireOption(options, "request");
}

async function readOptionalTextOption(options, name) {
  const file = optionalString(options[`${name}-file`]);
  if (file) {
    return readFile(resolveOptionPath(file), "utf8");
  }
  return optionalString(options[name]);
}

function parseUpdateRequest(input) {
  if (!input || typeof input !== "object") {
    throw new Error("Update request must be an object.");
  }

  const request = {
    id: requireString(input.id, "id"),
    targetRepoName: requireString(input.targetRepoName, "targetRepoName"),
    targetRepositoryFullName: optionalString(input.targetRepositoryFullName),
    targetAgentIdentity: optionalString(input.targetAgentIdentity),
    title: requireString(input.title, "title"),
    requestMarkdown: requireString(input.requestMarkdown, "requestMarkdown"),
    priority: requireNumber(input.priority, "priority"),
    status: requireString(input.status, "status"),
    sourceKind: optionalString(input.sourceKind) ?? "manual",
    sourceChannelId: optionalString(input.sourceChannelId),
    sourceMessageIds: normalizeStringArray(input.sourceMessageIds, "sourceMessageIds"),
    sourcePacketPath: optionalString(input.sourcePacketPath),
    sourcePromptPath: optionalString(input.sourcePromptPath),
    createdByAgent: optionalString(input.createdByAgent),
    claimedByAgent: optionalString(input.claimedByAgent),
    closeNote: optionalString(input.closeNote),
    createdAt: requireString(input.createdAt, "createdAt"),
    updatedAt: requireString(input.updatedAt, "updatedAt"),
    claimedAt: optionalString(input.claimedAt),
    closedAt: optionalString(input.closedAt),
  };

  if (!validStatuses.has(request.status)) {
    throw new Error(`Update request status "${request.status}" is not valid.`);
  }

  return request;
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
  const value = options[name];
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`Missing required option --${name}.`);
  }

  return value.trim();
}

function optionalString(value) {
  if (typeof value !== "string") {
    return undefined;
  }

  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function optionalArg(flag, value) {
  const normalized = optionalString(value);
  return normalized ? [flag, normalized] : [];
}

function requireString(value, field) {
  const normalized = optionalString(value);
  if (!normalized) {
    throw new Error(`Update request field "${field}" must be a non-empty string.`);
  }

  return normalized;
}

function requireNumber(value, field) {
  const number = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(number)) {
    throw new Error(`Update request field "${field}" must be a finite number.`);
  }

  return number;
}

function parseInteger(value, field) {
  const number = Number(value);
  if (!Number.isInteger(number)) {
    throw new Error(`--${field} must be an integer.`);
  }

  return number;
}

function parseCsv(value) {
  if (typeof value !== "string" || value.trim().length === 0) {
    return [];
  }

  return value
    .split(",")
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

function normalizeStringArray(value, field) {
  if (value === undefined || value === null) {
    return [];
  }

  if (!Array.isArray(value) || value.some((item) => typeof item !== "string")) {
    throw new Error(`Update request field "${field}" must be an array of strings.`);
  }

  return value;
}

function equalsIgnoreCase(left, right) {
  return typeof left === "string"
    && typeof right === "string"
    && left.toLowerCase() === right.toLowerCase();
}

function resolveOptionPath(path) {
  return resolve(process.cwd(), path);
}

function runNodeJson(args, cwd) {
  const result = spawnSync(process.execPath, args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
    timeout: 30000,
  });
  if (result.status !== 0) {
    const reason = result.error?.message ?? result.stderr ?? result.stdout;
    throw new Error(`node ${args.join(" ")} failed with ${result.status ?? "unknown"}:\n${reason}`);
  }
  return JSON.parse(result.stdout);
}

function loadLocalEnv(path) {
  if (!existsSync(path)) {
    return;
  }

  for (const line of readFileSync(path, "utf8").split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) {
      continue;
    }
    const separator = trimmed.indexOf("=");
    if (separator === -1) {
      continue;
    }
    const key = trimmed.slice(0, separator).trim();
    if (process.env[key]) {
      continue;
    }
    let value = trimmed.slice(separator + 1).trim();
    if (
      value.length >= 2 &&
      ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'")))
    ) {
      value = value.slice(1, -1);
    }
    process.env[key] = value;
  }
}

function printJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

function printHelp() {
  process.stdout.write(`Bifrost agent transport

Commands:
  enqueue        Store a queued update request in CultCache
  list           List requests, optionally filtered by --repo, --agent, --status
  claim          Claim the highest-priority queued request for --repo
  close          Mark a request completed or cancelled
  release        Return a claimed request to the queue after dispatch setup fails
  snapshot       Write or print a CultNet raw snapshot response
  apply-snapshot Apply a CultNet raw snapshot response from --in
  schema         Print the document type and schema id

Common options:
  --store <path> Override the .cc store path

Mirror options for enqueue:
  --mirror-channel-id <id>            Mirror queued requests to Discord; defaults to BIFROST_DISCORD_CHANNEL_ID
  --mirror-persona-name <name>        Render the mirror through this persona; defaults to Bifrost
  --mirror-persona-avatar-url <url>   Optional persona avatar for the mirror
  --mirror-content <text>             Optional custom mirror text
  --mirror-content-file <path>        Read custom mirror text from a file
  --mirror-dry-run true               Exercise mirror plumbing without posting to Discord
  --allow-unmirrored true             Fixture/debug escape hatch; production writes should not use this

Examples:
  node tools/agent-transport.mjs enqueue --repo AetheriaLore --agent nibu --title "Wavecrafters" --request-file packet.md --priority 80
  node tools/agent-transport.mjs claim --repo AetheriaLore --agent nibu --claimed-by nibu
  node tools/agent-transport.mjs snapshot --out transport.msgpack
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

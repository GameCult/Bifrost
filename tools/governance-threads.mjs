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
const defaultStorePath = resolve(repoRoot, ".bifrost", "governance-threads.cc");
const defaultPacketDir = resolve(repoRoot, ".bifrost", "governance-dispatch-packets");
const agentTransportCli = resolve(repoRoot, "tools", "agent-transport.mjs");
const bridgeCli = resolve(repoRoot, "tools", "bifrost-bridge.mjs");
const defaultPersonaName = "Bifrost";
const defaultPersonaAvatarUrl =
  "https://raw.githubusercontent.com/GameCult/Bifrost/main/src/Bifrost.Web/wwwroot/img/bifrost-profile.png";

const cultCacheRequire = createRequire(resolve(projectsRoot, "CultCacheTS", "package.json"));
const {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentType,
} = cultCacheRequire(resolve(projectsRoot, "CultCacheTS", "dist", "index.js"));

const topicType = "bifrost.governance.topic";
const topicSchemaId = "bifrost.governance.topic.v0";
const commentType = "bifrost.governance.topic-comment";
const commentSchemaId = "bifrost.governance.topic_comment.v0";
const validTopicStatuses = new Set(["open", "consensus_ready", "approved", "dispatched", "closed", "cancelled"]);
const validStances = new Set(["comment", "proposal", "support", "objection", "question", "approval", "summary", "receipt"]);

const topicDefinition = defineDocumentType({
  type: topicType,
  schemaId: topicSchemaId,
  schemaName: topicType,
  schemaVersion: "bifrost.governance.topic.v0",
  schema: { parse: parseTopic },
  name: "id",
  indexes: {
    jurisdictionRepoName: "jurisdictionRepoName",
    jurisdictionAgentIdentity: "jurisdictionAgentIdentity",
    status: "status",
  },
  members: [
    { slot: 1, memberName: "id", typeName: "string", isName: true },
    { slot: 2, memberName: "title", typeName: "string" },
    { slot: 3, memberName: "jurisdictionRepoName", typeName: "string", indexAlias: "jurisdictionRepoName" },
    { slot: 4, memberName: "jurisdictionAgentIdentity", typeName: "string", indexAlias: "jurisdictionAgentIdentity" },
    { slot: 5, memberName: "status", typeName: "string", indexAlias: "status" },
    { slot: 6, memberName: "summaryMarkdown", typeName: "string" },
    { slot: 7, memberName: "priority", typeName: "number" },
    { slot: 8, memberName: "sourceKind", typeName: "string" },
    { slot: 9, memberName: "sourceChannelId", typeName: "string" },
    { slot: 10, memberName: "sourceMessageIds", typeName: "string", isMany: true },
    { slot: 11, memberName: "createdByActor", typeName: "string" },
    { slot: 12, memberName: "approvedByAgent", typeName: "string" },
    { slot: 13, memberName: "dispatchRequestId", typeName: "string" },
    { slot: 14, memberName: "createdAt", typeName: "string" },
    { slot: 15, memberName: "updatedAt", typeName: "string" },
    { slot: 16, memberName: "approvedAt", typeName: "string" },
    { slot: 17, memberName: "dispatchedAt", typeName: "string" },
    { slot: 18, memberName: "closedAt", typeName: "string" },
  ],
});

const commentDefinition = defineDocumentType({
  type: commentType,
  schemaId: commentSchemaId,
  schemaName: commentType,
  schemaVersion: "bifrost.governance.topic_comment.v0",
  schema: { parse: parseComment },
  name: "id",
  indexes: {
    topicId: "topicId",
    authorId: "authorId",
    stance: "stance",
  },
  members: [
    { slot: 1, memberName: "id", typeName: "string", isName: true },
    { slot: 2, memberName: "topicId", typeName: "string", indexAlias: "topicId" },
    { slot: 3, memberName: "authorKind", typeName: "string" },
    { slot: 4, memberName: "authorId", typeName: "string", indexAlias: "authorId" },
    { slot: 5, memberName: "stance", typeName: "string", indexAlias: "stance" },
    { slot: 6, memberName: "bodyMarkdown", typeName: "string" },
    { slot: 7, memberName: "sourceMessageId", typeName: "string" },
    { slot: 8, memberName: "createdAt", typeName: "string" },
  ],
});

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
    case "open":
      await openTopic(cache, options);
      return;
    case "comment":
      await addComment(cache, options);
      return;
    case "approve":
      await approveTopic(cache, options);
      return;
    case "promote":
      await promoteTopic(cache, options);
      return;
    case "list":
      listTopics(cache, options);
      return;
    case "show":
      showTopic(cache, options);
      return;
    case "digest":
      digestTopics(cache, options);
      return;
    case "schema":
      printJson({ topicType, topicSchemaId, commentType, commentSchemaId });
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/governance-threads.mjs help".`);
  }
}

async function openCache(storePath) {
  const cache = CultCache.builder()
    .withDocumentType(topicDefinition)
    .withDocumentType(commentDefinition)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.pullAllBackingStores();
  return cache;
}

async function openTopic(cache, options) {
  const now = new Date().toISOString();
  const topic = parseTopic({
    id: options.id ?? `topic_${randomUUID()}`,
    title: requireOption(options, "title"),
    jurisdictionRepoName: requireOption(options, "repo"),
    jurisdictionAgentIdentity: optionalString(options.agent),
    status: optionalString(options.status) ?? "open",
    summaryMarkdown: await readMarkdownOption(options, "summary"),
    priority: parseInteger(options.priority ?? "50", "priority"),
    sourceKind: optionalString(options["source-kind"]) ?? "manual",
    sourceChannelId: optionalString(options["source-channel-id"]),
    sourceMessageIds: parseCsv(options["source-message-ids"]),
    createdByActor: optionalString(options["created-by"]),
    approvedByAgent: undefined,
    dispatchRequestId: undefined,
    createdAt: now,
    updatedAt: now,
    approvedAt: undefined,
    dispatchedAt: undefined,
    closedAt: undefined,
  });

  await cache.put(topicDefinition, topic.id, topic);
  try {
    await mirrorTopicActivityOrThrow(cache, topic, {
      options,
      fallbackContent: topic.summaryMarkdown,
      eventLabel: "opened",
    });
  } catch (error) {
    await cache.delete(topicDefinition, topic.id);
    throw error;
  }
  printJson(topic);
}

async function addComment(cache, options) {
  const topicId = requireOption(options, "topic");
  const topic = cache.get(topicDefinition, topicId);
  if (!topic) {
    throw new Error(`No governance topic found for id "${topicId}".`);
  }

  const comment = parseComment({
    id: options.id ?? `comment_${randomUUID()}`,
    topicId,
    authorKind: optionalString(options["author-kind"]) ?? "agent",
    authorId: requireOption(options, "author"),
    stance: optionalString(options.stance) ?? "comment",
    bodyMarkdown: await readMarkdownOption(options, "body"),
    sourceMessageId: optionalString(options["source-message-id"]),
    createdAt: new Date().toISOString(),
  });

  await cache.put(commentDefinition, comment.id, comment);
  try {
    await mirrorTopicActivityOrThrow(cache, topic, {
      options,
      fallbackContent: comment.bodyMarkdown,
      eventLabel: comment.stance,
    });
  } catch (error) {
    await cache.delete(commentDefinition, comment.id);
    throw error;
  }
  printJson(comment);
}

async function approveTopic(cache, options) {
  const topic = requireTopic(cache, options);
  const approvedBy = requireOption(options, "approved-by");
  const approvalBody = await readOptionalMarkdownOption(options, "body");

  if (!topic.jurisdictionAgentIdentity || !equalsIgnoreCase(approvedBy, topic.jurisdictionAgentIdentity)) {
    throw new Error(`Topic "${topic.id}" can only be approved by its jurisdiction Face (${topic.jurisdictionAgentIdentity ?? "none"}).`);
  }

  const now = new Date().toISOString();
  const approved = parseTopic({
    ...topic,
    status: "approved",
    approvedByAgent: approvedBy,
    approvedAt: now,
    updatedAt: now,
  });
  await cache.put(topicDefinition, approved.id, approved);

  let comment;
  if (approvalBody) {
    comment = parseComment({
      id: `comment_${randomUUID()}`,
      topicId: topic.id,
      authorKind: "face",
      authorId: approvedBy,
      stance: "approval",
      bodyMarkdown: approvalBody,
      sourceMessageId: optionalString(options["source-message-id"]),
      createdAt: now,
    });
    await cache.put(commentDefinition, comment.id, comment);
  }

  try {
    await mirrorTopicActivityOrThrow(cache, approved, {
      options,
      fallbackContent: approvalBody ?? `Approved by ${approvedBy}.`,
      eventLabel: "approved",
    });
  } catch (error) {
    await cache.put(topicDefinition, topic.id, topic);
    if (comment) {
      await cache.delete(commentDefinition, comment.id);
    }
    throw error;
  }

  printJson(approved);
}

async function promoteTopic(cache, options) {
  const topic = requireTopic(cache, options);
  if (topic.status !== "approved") {
    throw new Error(`Topic "${topic.id}" must be approved before dispatch promotion; current status is ${topic.status}.`);
  }

  const comments = commentsForTopic(cache, topic.id);
  const requestMarkdown = renderUpdateRequest(topic, comments);
  const packetDir = resolveOptionPath(options["packet-dir"] ?? defaultPacketDir);
  await mkdir(packetDir, { recursive: true });
  const packetPath = resolve(packetDir, `${topic.id}.md`);
  await writeFile(packetPath, requestMarkdown, "utf8");

  const request = runNodeJson([
    agentTransportCli,
    "enqueue",
    "--repo", topic.jurisdictionRepoName,
    ...optionalArg("--agent", topic.jurisdictionAgentIdentity),
    "--title", topic.title,
    "--request-file", packetPath,
    "--priority", String(topic.priority),
    "--source-kind", "bifrost_governance_topic",
    ...optionalArg("--source-channel-id", topic.sourceChannelId),
    ...optionalArg("--source-message-ids", topic.sourceMessageIds.join(",")),
    "--packet-path", packetPath,
    ...optionalArg("--created-by", topic.approvedByAgent ?? topic.createdByActor),
    ...optionalArg("--mirror-channel-id", resolveMirrorChannelId(options)),
    ...optionalArg("--mirror-persona-name", optionalString(process.env.BIFROST_DISCORD_PERSONA_NAME) ?? defaultPersonaName),
    ...optionalArg(
      "--mirror-persona-avatar-url",
      optionalString(process.env.BIFROST_DISCORD_PERSONA_AVATAR_URL) ??
        optionalString(process.env.DISCORD_PERSONA_AVATAR_URL_BIFROST) ??
        defaultPersonaAvatarUrl,
    ),
    ...(options["mirror-dry-run"] === "true" ? ["--mirror-dry-run", "true"] : []),
    ...(allowsUnmirrored(options) ? ["--allow-unmirrored", "true"] : []),
    ...optionalArg("--store", options["transport-store"]),
  ], repoRoot);

  const now = new Date().toISOString();
  const dispatched = parseTopic({
    ...topic,
    status: "dispatched",
    dispatchRequestId: request.id,
    dispatchedAt: now,
    updatedAt: now,
  });
  await cache.put(topicDefinition, dispatched.id, dispatched);

  const receipt = parseComment({
    id: `comment_${randomUUID()}`,
    topicId: topic.id,
    authorKind: "system",
    authorId: "bifrost",
    stance: "receipt",
    bodyMarkdown: `Promoted to Bifrost update request \`${request.id}\`.`,
    sourceMessageId: undefined,
    createdAt: now,
  });
  await cache.put(commentDefinition, receipt.id, receipt);
  try {
    await mirrorTopicActivityOrThrow(cache, dispatched, {
      options,
      fallbackContent: receipt.bodyMarkdown,
      eventLabel: "dispatched",
    });
  } catch (error) {
    await cache.put(topicDefinition, topic.id, topic);
    await cache.delete(commentDefinition, receipt.id);
    throw error;
  }

  printJson({ topic: dispatched, request });
}

async function mirrorTopicActivityOrThrow(cache, topic, input) {
  const channelId = resolveMirrorChannelId(input.options);
  if (!channelId) {
    if (allowsUnmirrored(input.options)) {
      return;
    }
    throw new Error(
      "Bifrost governance writes require a Discord mirror. Set BIFROST_DISCORD_CHANNEL_ID, pass --mirror-channel-id, or use --allow-unmirrored true only for explicit fixtures.",
    );
  }

  const personaName = optionalString(input.options["mirror-persona-name"])
    ?? optionalString(process.env.BIFROST_DISCORD_PERSONA_NAME)
    ?? defaultPersonaName;
  const personaAvatarUrl =
    optionalString(input.options["mirror-persona-avatar-url"]) ??
    optionalString(process.env.BIFROST_DISCORD_PERSONA_AVATAR_URL) ??
    optionalString(process.env.DISCORD_PERSONA_AVATAR_URL_BIFROST) ??
    defaultPersonaAvatarUrl;
  const mirrorContent =
    await readOptionalMarkdownOption(input.options, "mirror-content") ??
    renderMirrorFallback(topic, input.eventLabel, input.fallbackContent);

  const now = new Date().toISOString();
  const receipt = runNodeJson([
    bridgeCli,
    "discord-post",
    "--channel-id", channelId,
    "--content", mirrorContent,
    "--persona-name", personaName,
    ...optionalArg("--persona-avatar-url", personaAvatarUrl),
    ...optionalArg("--reply-to-message-id", input.options["mirror-reply-to-message-id"]),
    ...(input.options["mirror-dry-run"] === "true" ? ["--dry-run", "true"] : []),
  ], repoRoot);
  const bodyMarkdown = receipt.dryRun
    ? `Dry-run mirror prepared for Discord channel ${channelId}.`
    : `Mirrored ${input.eventLabel} to Discord channel ${channelId}${receipt.url ? `: ${receipt.url}` : ""}.`;

  const receiptComment = parseComment({
    id: `comment_${randomUUID()}`,
    topicId: topic.id,
    authorKind: "system",
    authorId: "bifrost",
    stance: "receipt",
    bodyMarkdown,
    sourceMessageId: receipt.messageId,
    createdAt: now,
  });
  await cache.put(commentDefinition, receiptComment.id, receiptComment);
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

function renderMirrorFallback(topic, eventLabel, content) {
  return [
    `Bifrost ${eventLabel}: ${topic.title}`,
    "",
    content.trim(),
    "",
    `Topic: ${topic.id}`,
  ].join("\n");
}

function listTopics(cache, options) {
  const topics = cache.getAll(topicDefinition)
    .filter((topic) => !options.status || topic.status === options.status)
    .filter((topic) => !options.repo || equalsIgnoreCase(topic.jurisdictionRepoName, options.repo))
    .filter((topic) => !options.agent || equalsIgnoreCase(topic.jurisdictionAgentIdentity, options.agent))
    .sort(compareTopics);

  printJson(topics);
}

function showTopic(cache, options) {
  const topic = requireTopic(cache, options);
  printJson({
    topic,
    comments: commentsForTopic(cache, topic.id),
  });
}

function digestTopics(cache, options) {
  const limit = parseInteger(options.limit ?? "6", "limit");
  const topics = cache.getAll(topicDefinition)
    .filter((topic) => !options.repo || equalsIgnoreCase(topic.jurisdictionRepoName, options.repo))
    .filter((topic) => !options.agent || equalsIgnoreCase(topic.jurisdictionAgentIdentity, options.agent))
    .filter((topic) => !options.status || topic.status === options.status)
    .sort(compareRecentTopics)
    .slice(0, limit);

  printJson({
    generatedAt: new Date().toISOString(),
    topics: topics.map((topic) => ({
      ...topic,
      comments: commentsForTopic(cache, topic.id).slice(-5),
    })),
  });
}

function requireTopic(cache, options) {
  const id = requireOption(options, "topic");
  const topic = cache.get(topicDefinition, id);
  if (!topic) {
    throw new Error(`No governance topic found for id "${id}".`);
  }
  return topic;
}

function commentsForTopic(cache, topicId) {
  return cache.getAll(commentDefinition)
    .filter((comment) => comment.topicId === topicId)
    .sort((left, right) => left.createdAt.localeCompare(right.createdAt));
}

function renderUpdateRequest(topic, comments) {
  const commentLines = comments.map((comment) => [
    `### ${comment.stance}: ${comment.authorId}`,
    "",
    comment.bodyMarkdown,
  ].join("\n"));

  return [
    `# ${topic.title}`,
    "",
    `Bifrost governance topic: ${topic.id}`,
    `Jurisdiction: ${topic.jurisdictionRepoName}${topic.jurisdictionAgentIdentity ? ` / ${topic.jurisdictionAgentIdentity}` : ""}`,
    `Approved by: ${topic.approvedByAgent}`,
    `Priority: ${topic.priority}`,
    "",
    "## Request",
    "",
    topic.summaryMarkdown,
    "",
    "## Discussion Record",
    "",
    commentLines.length > 0 ? commentLines.join("\n\n") : "- No comments recorded.",
    "",
    "## Dispatch Instruction",
    "",
    "Work this as the approved repo-local request produced from the canonical Bifrost topic. Preserve the topic id in any report, commit message, PR body, or follow-up note.",
    "",
  ].join("\n");
}

async function readMarkdownOption(options, name) {
  const value = await readOptionalMarkdownOption(options, name);
  if (!value) {
    throw new Error(`Missing required option --${name} or --${name}-file.`);
  }
  return value;
}

async function readOptionalMarkdownOption(options, name) {
  const fileValue = optionalString(options[`${name}-file`]);
  if (fileValue) {
    return readFile(resolveOptionPath(fileValue), "utf8");
  }
  return optionalString(options[name]);
}

function parseTopic(input) {
  if (!input || typeof input !== "object") {
    throw new Error("Governance topic must be an object.");
  }

  const topic = {
    id: requireString(input.id, "id"),
    title: requireString(input.title, "title"),
    jurisdictionRepoName: requireString(input.jurisdictionRepoName, "jurisdictionRepoName"),
    jurisdictionAgentIdentity: optionalString(input.jurisdictionAgentIdentity),
    status: requireString(input.status, "status"),
    summaryMarkdown: requireString(input.summaryMarkdown, "summaryMarkdown"),
    priority: requireNumber(input.priority, "priority"),
    sourceKind: optionalString(input.sourceKind) ?? "manual",
    sourceChannelId: optionalString(input.sourceChannelId),
    sourceMessageIds: normalizeStringArray(input.sourceMessageIds, "sourceMessageIds"),
    createdByActor: optionalString(input.createdByActor),
    approvedByAgent: optionalString(input.approvedByAgent),
    dispatchRequestId: optionalString(input.dispatchRequestId),
    createdAt: requireString(input.createdAt, "createdAt"),
    updatedAt: requireString(input.updatedAt, "updatedAt"),
    approvedAt: optionalString(input.approvedAt),
    dispatchedAt: optionalString(input.dispatchedAt),
    closedAt: optionalString(input.closedAt),
  };

  if (!validTopicStatuses.has(topic.status)) {
    throw new Error(`Governance topic status "${topic.status}" is not valid.`);
  }

  return topic;
}

function parseComment(input) {
  if (!input || typeof input !== "object") {
    throw new Error("Governance topic comment must be an object.");
  }

  const comment = {
    id: requireString(input.id, "id"),
    topicId: requireString(input.topicId, "topicId"),
    authorKind: requireString(input.authorKind, "authorKind"),
    authorId: requireString(input.authorId, "authorId"),
    stance: requireString(input.stance, "stance"),
    bodyMarkdown: requireString(input.bodyMarkdown, "bodyMarkdown"),
    sourceMessageId: optionalString(input.sourceMessageId),
    createdAt: requireString(input.createdAt, "createdAt"),
  };

  if (!validStances.has(comment.stance)) {
    throw new Error(`Governance comment stance "${comment.stance}" is not valid.`);
  }

  return comment;
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

function compareTopics(left, right) {
  if (right.priority !== left.priority) {
    return right.priority - left.priority;
  }
  return right.createdAt.localeCompare(left.createdAt);
}

function compareRecentTopics(left, right) {
  return right.updatedAt.localeCompare(left.updatedAt);
}

function requireOption(options, name) {
  const value = optionalString(options[name]);
  if (!value) {
    throw new Error(`Missing required option --${name}.`);
  }
  return value;
}

function optionalString(value) {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function requireString(value, field) {
  const normalized = optionalString(value);
  if (!normalized) {
    throw new Error(`Governance document field "${field}" must be a non-empty string.`);
  }
  return normalized;
}

function requireNumber(value, field) {
  const number = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(number)) {
    throw new Error(`Governance document field "${field}" must be a finite number.`);
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
  return value.split(",").map((item) => item.trim()).filter(Boolean);
}

function normalizeStringArray(value, field) {
  if (value === undefined || value === null) {
    return [];
  }
  if (!Array.isArray(value) || value.some((item) => typeof item !== "string")) {
    throw new Error(`Governance document field "${field}" must be an array of strings.`);
  }
  return value;
}

function optionalArg(flag, value) {
  const normalized = optionalString(value);
  return normalized ? [flag, normalized] : [];
}

function equalsIgnoreCase(left, right) {
  return typeof left === "string"
    && typeof right === "string"
    && left.toLowerCase() === right.toLowerCase();
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

function resolveOptionPath(path) {
  return resolve(process.cwd(), path);
}

function printJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

function printHelp() {
  process.stdout.write(`Bifrost governance topic threads

Commands:
  open      Create a canonical Bifrost topic for a feature request or discussion
  comment   Append a proposal, support, objection, question, approval, summary, or receipt
  approve   Mark a topic approved by its jurisdiction Face and optionally append approval text
  promote   Convert an approved topic into a Bifrost agent update request
  list      List topics, optionally filtered by --repo, --agent, --status
  show      Print one topic with its comments
  digest    Print recent matching topics with their latest comments for agent context
  schema    Print document type metadata

Examples:
  node tools/governance-threads.mjs open --repo AquaSynth --agent aqua --title "Universal utterance schema" --summary-file packet.md --priority 80
  node tools/governance-threads.mjs approve --topic topic_... --approved-by aqua --body "Aqua approves dispatch."
  node tools/governance-threads.mjs promote --topic topic_...

Mirror options:
  --mirror-channel-id <id>            Mirror this activity to Discord through Bifrost bridge; defaults to BIFROST_DISCORD_CHANNEL_ID
  --mirror-persona-name <name>        Render the mirror as the Face/persona
  --mirror-persona-avatar-url <url>   Optional persona avatar for the mirror
  --mirror-content <text>             Optional more verbal/personality-rich mirror text
  --mirror-content-file <path>        Read mirror text from a file
  --mirror-dry-run true               Exercise mirror plumbing without posting to Discord
  --allow-unmirrored true             Fixture/debug escape hatch; production writes should not use this
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

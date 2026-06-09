#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const PERSONA_WEBHOOK_NAME = "Bifrost Persona Pipe";
const PERSONA_WEBHOOK_CACHE_PATH = resolve(".bifrost/discord-webhook-cache.json");
const THREAD_CHANNEL_TYPES = new Set([10, 11, 12]);
const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, "..");
const projectsRoot = resolve(repoRoot, "..");

async function main() {
  loadLocalEnv(resolve(repoRoot, ".env"));
  loadLocalEnv(resolve(projectsRoot, "VoidBot", ".env"));
  const [command, ...rawArgs] = process.argv.slice(2);
  const options = parseArgs(rawArgs);

  if (!command || command === "help" || command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  switch (command) {
    case "github-draft-pr":
      await createGitHubDraftPr(options);
      return;
    case "github-pr-comment":
      await commentGitHubPr(options);
      return;
    case "discord-post":
      await postDiscordMessage(options);
      return;
    case "discord-dm":
      await sendDiscordDm(options);
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/bifrost-bridge.mjs help".`);
  }
}

async function createGitHubDraftPr(options) {
  const repoRoot = resolve(requireOption(options, "repo-root"));
  const identity = slugify(requireOption(options, "identity"));
  const title = requireOption(options, "title");
  const relativePath = normalizeRelativePath(requireOption(options, "path"));
  const body = await readOptionText(options, "body", "body-file", "Draft PR opened by Bifrost.");
  const content = await readOptionText(options, "content", "content-file");
  const base = options.base ?? "main";
  const branch = options.branch ?? `bifrost/${identity}/${slugify(title)}-${timestampSlug(new Date())}`;
  const commitMessage = options["commit-message"] ?? title;
  const allowDirty = options["allow-dirty"] === "true";
  const dryRun = options["dry-run"] === "true";

  const originalBranch = git(["branch", "--show-current"], repoRoot).stdout.trim();
  const status = git(["status", "--short"], repoRoot).stdout.trim();
  if (status.length > 0 && !allowDirty && !dryRun) {
    throw new Error(`Target repo has uncommitted changes. Refusing bridge write without --allow-dirty.\n${status}`);
  }

  const targetPath = resolve(repoRoot, relativePath);
  if (!targetPath.toLowerCase().startsWith(`${repoRoot.toLowerCase()}\\`) && targetPath.toLowerCase() !== repoRoot.toLowerCase()) {
    throw new Error(`Target path escapes repo root: ${relativePath}`);
  }

  if (dryRun) {
    printJson({
      dryRun: true,
      action: "github-draft-pr",
      repoRoot,
      identity,
      title,
      path: relativePath,
      base,
      branch,
      originalBranch,
      commitMessage,
      dirty: status.length > 0,
    });
    return;
  }

  let pushed = false;
  let prUrl = "";
  try {
    git(["switch", "-c", branch], repoRoot);
    await mkdir(dirname(targetPath), { recursive: true });
    await writeFile(targetPath, content, "utf8");
    git(["add", "--", relativePath], repoRoot);
    git(["commit", "-m", commitMessage], repoRoot);
    git(["push", "-u", "origin", branch], repoRoot);
    pushed = true;

    const pr = run("gh", [
      "pr",
      "create",
      "--draft",
      "--title",
      title,
      "--body",
      body,
      "--base",
      base,
      "--head",
      branch,
    ], repoRoot);
    prUrl = pr.stdout.trim();
  } finally {
    if (originalBranch) {
      git(["switch", originalBranch], repoRoot, { allowFailure: true });
    }
  }

  printJson({
    action: "github-draft-pr",
    ok: true,
    repoRoot,
    identity,
    title,
    path: relativePath,
    base,
    branch,
    pushed,
    prUrl,
  });
}

async function commentGitHubPr(options) {
  const repoRoot = resolve(requireOption(options, "repo-root"));
  const identity = slugify(requireOption(options, "identity"));
  const pr = requireOption(options, "pr");
  const content = await readOptionText(options, "content", "content-file");
  const dryRun = options["dry-run"] === "true";
  const body = `${identity} says:\n\n${content.trim()}`;

  if (dryRun) {
    printJson({
      dryRun: true,
      action: "github-pr-comment",
      repoRoot,
      identity,
      pr,
      body,
    });
    return;
  }

  run("gh", ["pr", "comment", pr, "--body", body], repoRoot);
  printJson({
    action: "github-pr-comment",
    ok: true,
    repoRoot,
    identity,
    pr,
  });
}

async function postDiscordMessage(options) {
  const token = process.env.BIFROST_DISCORD_BOT_TOKEN ?? process.env.DISCORD_BOT_TOKEN;
  const channelId = requireOption(options, "channel-id");
  const content = await readOptionText(options, "content", "content-file");
  const replyToMessageId = optionalString(options["reply-to-message-id"]);
  const personaName = optionalString(options["persona-name"]);
  const personaAvatarUrl = optionalString(options["persona-avatar-url"]);
  const dryRun = options["dry-run"] === "true";

  if (dryRun) {
    printJson({
      dryRun: true,
      action: "discord-post",
      channelId,
      content,
      replyToMessageId,
      personaName,
      personaAvatarUrl,
    });
    return;
  }

  if (!token) {
    throw new Error("Set BIFROST_DISCORD_BOT_TOKEN or DISCORD_BOT_TOKEN before posting to Discord.");
  }

  const result = personaName
    ? await postDiscordPersonaMessage(token, channelId, content, {
        personaName,
        personaAvatarUrl,
        replyToMessageId,
      })
    : await postDiscordBotMessage(token, channelId, content, replyToMessageId);

  printJson({
    action: "discord-post",
    ok: true,
    channelId,
    messageId: result.id,
    transport: result.transport,
    url: `https://discord.com/channels/${result.guildId ?? "@me"}/${channelId}/${result.id}`,
  });
}

async function sendDiscordDm(options) {
  const token = process.env.BIFROST_DISCORD_BOT_TOKEN ?? process.env.DISCORD_BOT_TOKEN;
  const recipientId = requireOption(options, "recipient-id");
  const content = await readOptionText(options, "content", "content-file");
  const dryRun = options["dry-run"] === "true";

  if (dryRun) {
    printJson({
      dryRun: true,
      action: "discord-dm",
      recipientId,
      content,
    });
    return;
  }

  if (!token) {
    throw new Error("Set BIFROST_DISCORD_BOT_TOKEN or DISCORD_BOT_TOKEN before sending a Discord DM.");
  }

  const channelId = await openDiscordDmChannel(token, recipientId);
  const result = await postDiscordBotMessage(token, channelId, content, undefined);
  printJson({
    action: "discord-dm",
    ok: true,
    recipientId,
    channelId,
    messageId: result.id,
    transport: result.transport,
    url: `https://discord.com/channels/@me/${channelId}/${result.id}`,
  });
}

async function openDiscordDmChannel(token, recipientId) {
  const response = await fetch("https://discord.com/api/v10/users/@me/channels", {
    method: "POST",
    headers: {
      Authorization: `Bot ${token}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      recipient_id: recipientId,
    }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Discord DM channel creation failed with ${response.status}: ${text}`);
  }

  const channel = JSON.parse(text);
  if (!channel.id) {
    throw new Error("Discord DM channel creation returned no channel id.");
  }
  return channel.id;
}

async function postDiscordBotMessage(token, channelId, content, replyToMessageId) {
  const response = await fetch(`https://discord.com/api/v10/channels/${channelId}/messages`, {
    method: "POST",
    headers: {
      Authorization: `Bot ${token}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      content,
      message_reference: replyToMessageId
        ? {
            message_id: replyToMessageId,
            fail_if_not_exists: false,
          }
        : undefined,
      allowed_mentions: {
        parse: [],
      },
    }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Discord post failed with ${response.status}: ${text}`);
  }

  const message = JSON.parse(text);
  return {
    id: message.id,
    guildId: message.guild_id,
    transport: "bot",
  };
}

async function postDiscordPersonaMessage(token, channelId, content, options) {
  const target = await resolveWebhookTarget(token, channelId);
  let webhook = await getConfiguredPersonaWebhook(target.webhookChannelId);
  if (!webhook) {
    webhook = getCachedPersonaWebhook(target.webhookChannelId);
  }
  if (!webhook) {
    webhook = await createPersonaWebhook(token, target.webhookChannelId);
    writeCachedPersonaWebhook(target.webhookChannelId, webhook);
  }

  try {
    return await executePersonaWebhook(webhook, {
      threadId: target.threadId,
      content,
      replyToMessageId: options.replyToMessageId,
      username: options.personaName.slice(0, 80),
      avatarUrl: options.personaAvatarUrl,
    });
  } catch (error) {
    if (!isStaleWebhookError(error) || webhook.configured) {
      throw error;
    }

    clearCachedPersonaWebhook(target.webhookChannelId);
    const refreshed = await createPersonaWebhook(token, target.webhookChannelId);
    writeCachedPersonaWebhook(target.webhookChannelId, refreshed);
    return executePersonaWebhook(refreshed, {
      threadId: target.threadId,
      content,
      replyToMessageId: options.replyToMessageId,
      username: options.personaName.slice(0, 80),
      avatarUrl: options.personaAvatarUrl,
    });
  }
}

async function resolveWebhookTarget(token, channelId) {
  const response = await fetch(`https://discord.com/api/v10/channels/${channelId}`, {
    method: "GET",
    headers: {
      Authorization: `Bot ${token}`,
      "Content-Type": "application/json",
    },
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Discord channel lookup failed with ${response.status}: ${text}`);
  }

  const channel = JSON.parse(text);
  if (THREAD_CHANNEL_TYPES.has(channel.type)) {
    if (!channel.parent_id) {
      throw new Error(`Discord thread ${channelId} has no parent channel for webhook routing.`);
    }

    return {
      webhookChannelId: channel.parent_id,
      threadId: channel.id,
    };
  }

  return {
    webhookChannelId: channel.id,
    threadId: undefined,
  };
}

async function createPersonaWebhook(token, channelId) {
  const response = await fetch(`https://discord.com/api/v10/channels/${channelId}/webhooks`, {
    method: "POST",
    headers: {
      Authorization: `Bot ${token}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      name: PERSONA_WEBHOOK_NAME,
    }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(
      `Discord webhook creation failed with ${response.status}: ${text}. ` +
        `Grant Manage Webhooks or set BIFROST_DISCORD_PERSONA_WEBHOOK_URL_${channelId}.`,
    );
  }

  const payload = JSON.parse(text);
  if (!payload.id || !payload.token) {
    throw new Error(`Discord webhook creation for channel ${channelId} returned no executable token.`);
  }

  return {
    id: payload.id,
    token: payload.token,
    channelId,
    name: PERSONA_WEBHOOK_NAME,
    createdAt: new Date().toISOString(),
  };
}

async function getConfiguredPersonaWebhook(channelId) {
  const rawUrl =
    optionalString(process.env[`BIFROST_DISCORD_PERSONA_WEBHOOK_URL_${channelId}`]) ??
    optionalString(process.env.DISCORD_PERSONA_WEBHOOK_URL);

  if (!rawUrl) {
    return undefined;
  }

  const webhook = parseDiscordWebhookUrl(rawUrl, channelId);
  const response = await fetch(`https://discord.com/api/v10/webhooks/${webhook.id}/${webhook.token}`);
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Configured Discord webhook lookup failed with ${response.status}: ${text}`);
  }

  const payload = JSON.parse(text);
  if (payload.channel_id !== channelId) {
    throw new Error(`Configured Discord webhook targets channel ${payload.channel_id}, not ${channelId}.`);
  }

  return webhook;
}

function parseDiscordWebhookUrl(rawUrl, expectedChannelId) {
  const url = new URL(rawUrl);
  const match =
    url.pathname.match(/\/api(?:\/v\d+)?\/webhooks\/([^/]+)\/([^/?#]+)/) ??
    url.pathname.match(/\/webhooks\/([^/]+)\/([^/?#]+)/);

  if (!match) {
    throw new Error(
      `Configured Discord webhook for channel ${expectedChannelId} must look like https://discord.com/api/webhooks/<id>/<token>.`,
    );
  }

  return {
    id: match[1],
    token: match[2],
    channelId: expectedChannelId,
    name: "configured",
    createdAt: "configured",
    configured: true,
  };
}

async function executePersonaWebhook(webhook, input) {
  const url = new URL(`https://discord.com/api/v10/webhooks/${webhook.id}/${webhook.token}`);
  url.searchParams.set("wait", "true");
  if (input.threadId) {
    url.searchParams.set("thread_id", input.threadId);
  }

  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      content: input.content,
      username: input.username,
      avatar_url: input.avatarUrl,
      message_reference: input.replyToMessageId
        ? {
            message_id: input.replyToMessageId,
            fail_if_not_exists: false,
          }
        : undefined,
      allowed_mentions: {
        parse: [],
      },
    }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Discord webhook execution failed with ${response.status}: ${text}`);
  }

  const payload = JSON.parse(text);
  if (!payload.id) {
    throw new Error("Discord webhook execution returned no message id.");
  }

  return {
    id: payload.id,
    guildId: payload.guild_id,
    transport: "webhook",
  };
}

function getCachedPersonaWebhook(channelId) {
  return readPersonaWebhookCache()[channelId];
}

function writeCachedPersonaWebhook(channelId, webhook) {
  const cache = readPersonaWebhookCache();
  cache[channelId] = webhook;
  writePersonaWebhookCache(cache);
}

function clearCachedPersonaWebhook(channelId) {
  const cache = readPersonaWebhookCache();
  delete cache[channelId];
  writePersonaWebhookCache(cache);
}

function readPersonaWebhookCache() {
  if (!existsSync(PERSONA_WEBHOOK_CACHE_PATH)) {
    return {};
  }

  try {
    return JSON.parse(readFileSync(PERSONA_WEBHOOK_CACHE_PATH, "utf8"));
  } catch {
    return {};
  }
}

function writePersonaWebhookCache(cache) {
  mkdirSync(dirname(PERSONA_WEBHOOK_CACHE_PATH), { recursive: true });
  writeFileSync(PERSONA_WEBHOOK_CACHE_PATH, `${JSON.stringify(cache, null, 2)}\n`, "utf8");
}

function isStaleWebhookError(error) {
  return error instanceof Error && (
    error.message.includes("Discord webhook execution failed with 401") ||
    error.message.includes("Discord webhook execution failed with 404")
  );
}

async function readOptionText(options, inlineName, fileName, fallback) {
  const inline = optionalString(options[inlineName]);
  if (inline) {
    return inline;
  }

  const file = optionalString(options[fileName]);
  if (file) {
    return readFile(resolve(file), "utf8");
  }

  if (fallback !== undefined) {
    return fallback;
  }

  throw new Error(`Missing required option --${inlineName} or --${fileName}.`);
}

function git(args, cwd, options = {}) {
  return run("git", args, cwd, options);
}

function run(command, args, cwd, options = {}) {
  const result = spawnSync(command, args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });

  if (result.status !== 0 && !options.allowFailure) {
    throw new Error(`${command} ${args.join(" ")} failed with ${result.status ?? "unknown"}:\n${result.stderr || result.stdout}`);
  }

  return {
    stdout: result.stdout ?? "",
    stderr: result.stderr ?? "",
    status: result.status,
  };
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

function optionalString(value) {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function normalizeRelativePath(value) {
  const normalized = value.replace(/\\/g, "/").replace(/^\/+/, "");
  if (normalized.includes("../") || normalized === "..") {
    throw new Error(`Relative path may not escape the repo: ${value}`);
  }
  return normalized;
}

function slugify(value) {
  return String(value)
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 80) || "bridge";
}

function timestampSlug(date) {
  return date.toISOString().replace(/[:.]/g, "-");
}

function printJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

function loadLocalEnv(path) {
  const normalizedPath = path.startsWith("/") && /^[A-Za-z]:/.test(path.slice(1))
    ? path.slice(1)
    : path;
  if (!existsSync(normalizedPath)) {
    return;
  }

  for (const line of readFileSync(normalizedPath, "utf8").split(/\r?\n/)) {
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

function printHelp() {
  process.stdout.write(`Bifrost bridge

Commands:
  github-draft-pr   Write one file in a target repo and open a draft PR through gh
  github-pr-comment Comment on a pull request through gh
  discord-post      Post a message to Discord through the bot token or persona webhook pipe
  discord-dm        Send a Discord DM through Bifrost's bridge-owned bot token

Examples:
  node tools/bifrost-bridge.mjs github-draft-pr --repo-root E:/Projects/AetheriaLore --identity nibu --title "Nibu: Glitchcraft" --path Aetheria/Articles/Nibu/glitchcraft.md --content-file article.md
  node tools/bifrost-bridge.mjs github-pr-comment --repo-root E:/Projects/AetheriaLore --identity nibu --pr 12 --content "This needs a sharper leash."
  node tools/bifrost-bridge.mjs discord-post --channel-id 1501196543150264332 --persona-name Nibu --content "Draft PR opened: https://github.com/..."
  node tools/bifrost-bridge.mjs discord-dm --recipient-id 123456789 --content "Moderation status update..."
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

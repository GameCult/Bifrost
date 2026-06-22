#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { mkdir, readFile, unlink, writeFile } from "node:fs/promises";
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
  loadBifrostLocalEnv();
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
    case "reddit-post":
      await postRedditThread(options);
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/bifrost-bridge.mjs help".`);
  }
}

function loadBifrostLocalEnv() {
  if (process.env.BIFROST_SKIP_LOCAL_ENV === "true") {
    return;
  }

  loadLocalEnv(resolve(repoRoot, ".env"));
  loadLocalEnv(resolve(projectsRoot, "VoidBot", ".env"));
}

async function createGitHubDraftPr(options) {
  ensureGitHubBridgeGate(options);
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
  const targetRepositoryFullName =
    optionalString(options["target-repository-full-name"]) ??
    detectGitHubRepositoryFullName(options, repoRoot);

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

  const bridgeAction = await beginBridgeAction(options, {
    actorKind: "Agent",
    actorName: identity,
    targetSurface: "GitHub",
    actionKind: "GitHubDraftPullRequest",
    targetRepositoryFullName,
    targetLocator: relativePath,
    title,
    summary: body,
  });

  let pushed = false;
  let prUrl = "";
  try {
    try {
      await bridgeAction?.start();
      git(["switch", "-c", branch], repoRoot);
      await mkdir(dirname(targetPath), { recursive: true });
      await writeFile(targetPath, content, "utf8");
      git(["add", "--", relativePath], repoRoot);
      git(["commit", "-m", commitMessage], repoRoot);
      git(["push", "-u", "origin", branch], repoRoot, {
        env: {
          BIFROST_GITHUB_PUSH_AUTHORIZED: "true",
        },
      });
      pushed = true;

      const pr = runGitHubCli(options, [
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
      ], repoRoot, {
        env: {
          BIFROST_GITHUB_MUTATION_AUTHORIZED: "true",
        },
      });
      prUrl = pr.stdout.trim();
      await bridgeAction?.complete({
        receiptUrl: prUrl,
        externalReceiptId: extractGitHubNumber(prUrl) ?? branch,
        receiptPayload: JSON.stringify({
          branch,
          base,
          path: relativePath,
          commitMessage,
          prUrl,
        }),
      });
    } catch (error) {
      await bridgeAction?.fail(error);
      throw error;
    }
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
  ensureGitHubBridgeGate(options);
  const repoRoot = resolve(requireOption(options, "repo-root"));
  const identity = slugify(requireOption(options, "identity"));
  const pr = requireOption(options, "pr");
  const content = await readOptionText(options, "content", "content-file");
  const dryRun = options["dry-run"] === "true";
  const body = `${identity} says:\n\n${content.trim()}`;
  const targetRepositoryFullName =
    optionalString(options["target-repository-full-name"]) ??
    detectGitHubRepositoryFullName(options, repoRoot);

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

  const bridgeAction = await beginBridgeAction(options, {
    actorKind: "Agent",
    actorName: identity,
    targetSurface: "GitHub",
    actionKind: "GitHubPullRequestComment",
    targetRepositoryFullName,
    targetLocator: `pull/${pr}`,
    title: `PR comment on #${pr}`,
    summary: content,
  });

  try {
    await bridgeAction?.start();
    const payloadPath = resolve(repoRoot, ".bifrost", `github-pr-comment-${Date.now()}-${Math.random().toString(16).slice(2)}.json`);
    await mkdir(dirname(payloadPath), { recursive: true });
    await writeFile(payloadPath, JSON.stringify({ body }), "utf8");
    let comment;
    try {
      comment = parseRequiredJson(
        runGitHubCli(
          options,
          [
            "api",
            `repos/${targetRepositoryFullName}/issues/${pr}/comments`,
            "--method", "POST",
            "--input", payloadPath,
          ],
          repoRoot,
          {
            env: {
              BIFROST_GITHUB_MUTATION_AUTHORIZED: "true",
            },
          },
        ).stdout,
        "GitHub PR comment response",
      );
    } finally {
      await unlink(payloadPath).catch(() => {});
    }
    const receiptUrl = optionalString(comment.html_url);
    const externalReceiptId = comment.id === undefined || comment.id === null ? "" : String(comment.id);
    if (!receiptUrl || !externalReceiptId) {
      throw new Error("GitHub PR comment response returned no concrete receipt URL or comment id.");
    }
    await bridgeAction?.complete({
      receiptUrl,
      externalReceiptId,
      receiptPayload: JSON.stringify({
        pullRequestNumber: Number(pr),
        repositoryFullName: targetRepositoryFullName,
        issueCommentId: comment.id,
        issueCommentNodeId: optionalString(comment.node_id) ?? "",
        issueCommentUrl: receiptUrl,
        author: identity,
        body,
      }),
    });
    printJson({
      action: "github-pr-comment",
      ok: true,
      repoRoot,
      identity,
      pr,
      receiptUrl,
      externalReceiptId,
    });
  } catch (error) {
    await bridgeAction?.fail(error);
    throw error;
  }
}

async function postDiscordMessage(options) {
  ensureBridgeReceiptGate(options);
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

  const bridgeAction = await beginBridgeAction(options, {
    actorKind: personaName ? "Persona" : "Service",
    actorName: personaName ?? "bifrost",
    targetSurface: "Discord",
    actionKind: "DiscordPost",
    targetRepositoryFullName: options["target-repository-full-name"] ?? "",
    targetLocator: `channel/${channelId}`,
    title: personaName ? `Discord post from ${personaName}` : "Discord post",
    summary: content,
  });

  let result;
  try {
    await bridgeAction?.start();
    result = personaName
      ? await postDiscordPersonaMessage(token, channelId, content, {
          personaName,
          personaAvatarUrl,
          replyToMessageId,
        })
      : await postDiscordBotMessage(token, channelId, content, replyToMessageId);
    await bridgeAction?.complete({
      receiptUrl: `https://discord.com/channels/${result.guildId ?? "@me"}/${channelId}/${result.id}`,
      externalReceiptId: result.id,
      receiptPayload: JSON.stringify({
        channelId,
        messageId: result.id,
        guildId: result.guildId ?? "",
        transport: result.transport,
      }),
    });
  } catch (error) {
    await bridgeAction?.fail(error);
    throw error;
  }

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
  ensureBridgeReceiptGate(options);
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

  const bridgeAction = await beginBridgeAction(options, {
    actorKind: "Service",
    actorName: "bifrost",
    targetSurface: "Discord",
    actionKind: "DiscordDirectMessage",
    targetRepositoryFullName: options["target-repository-full-name"] ?? "",
    targetLocator: `recipient/${recipientId}`,
    title: "Discord direct message",
    summary: content,
  });

  let channelId;
  let result;
  try {
    await bridgeAction?.start();
    channelId = await openDiscordDmChannel(token, recipientId);
    result = await postDiscordBotMessage(token, channelId, content, undefined);
    await bridgeAction?.complete({
      receiptUrl: `https://discord.com/channels/@me/${channelId}/${result.id}`,
      externalReceiptId: result.id,
      receiptPayload: JSON.stringify({
        recipientId,
        channelId,
        messageId: result.id,
        transport: result.transport,
      }),
    });
  } catch (error) {
    await bridgeAction?.fail(error);
    throw error;
  }
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

async function postRedditThread(options) {
  ensureBridgeReceiptGate(options);
  const subreddit = normalizeSubreddit(options.subreddit ?? process.env.BIFROST_REDDIT_SUBREDDIT ?? "GameCultOrg");
  const title = requireOption(options, "title");
  const content = await readOptionText(options, "content", "content-file");
  const personaName = optionalString(options["persona-name"]);
  const personaFlairId = optionalString(options["persona-flair-id"]);
  const personaFlairText = optionalString(options["persona-flair-text"]) ?? personaName;
  const dryRun = options["dry-run"] === "true";

  if (dryRun) {
    printJson({
      dryRun: true,
      action: "reddit-post",
      subreddit,
      title,
      content,
      personaName,
      personaFlairId,
      personaFlairText,
    });
    return;
  }

  const bridgeAction = await beginBridgeAction(options, {
    actorKind: personaName ? "Persona" : "Service",
    actorName: personaName ?? "bifrost",
    targetSurface: "Reddit",
    actionKind: "RedditPost",
    targetRepositoryFullName: options["target-repository-full-name"] ?? "",
    targetLocator: `r/${subreddit}`,
    title,
    summary: content,
  });

  let result;
  try {
    await bridgeAction?.start();
    const accessToken = await getRedditAccessToken();
    result = await submitRedditSelfPost(accessToken, {
      subreddit,
      title,
      content,
      flairId: personaFlairId,
      flairText: personaFlairText,
    });
    await bridgeAction?.complete({
      receiptUrl: result.url,
      externalReceiptId: result.thingId,
      receiptPayload: JSON.stringify({
        subreddit,
        thingId: result.thingId,
        url: result.url,
        personaName,
        personaFlairId,
        personaFlairText,
      }),
    });
  } catch (error) {
    await bridgeAction?.fail(error);
    throw error;
  }

  printJson({
    action: "reddit-post",
    ok: true,
    subreddit,
    title,
    personaName,
    personaFlairId,
    personaFlairText,
    thingId: result.thingId,
    url: result.url,
  });
}

async function getRedditAccessToken() {
  const clientId = optionalString(process.env.BIFROST_REDDIT_CLIENT_ID ?? process.env.REDDIT_CLIENT_ID);
  const clientSecret = process.env.BIFROST_REDDIT_CLIENT_SECRET ?? process.env.REDDIT_CLIENT_SECRET ?? "";
  const refreshToken = optionalString(process.env.BIFROST_REDDIT_REFRESH_TOKEN ?? process.env.REDDIT_REFRESH_TOKEN);
  const userAgent = getRedditUserAgent();

  if (!clientId || !refreshToken) {
    throw new Error(
      "Set BIFROST_REDDIT_CLIENT_ID and BIFROST_REDDIT_REFRESH_TOKEN before posting to Reddit. " +
        "Set BIFROST_REDDIT_CLIENT_SECRET when the Reddit app uses one.",
    );
  }

  const response = await fetch("https://www.reddit.com/api/v1/access_token", {
    method: "POST",
    headers: {
      Authorization: `Basic ${Buffer.from(`${clientId}:${clientSecret}`).toString("base64")}`,
      "Content-Type": "application/x-www-form-urlencoded",
      "User-Agent": userAgent,
    },
    body: new URLSearchParams({
      grant_type: "refresh_token",
      refresh_token: refreshToken,
    }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Reddit token exchange failed with ${response.status}: ${text}`);
  }

  const payload = JSON.parse(text);
  if (!payload.access_token) {
    throw new Error("Reddit token exchange returned no access token.");
  }
  return payload.access_token;
}

async function submitRedditSelfPost(accessToken, input) {
  const body = new URLSearchParams({
    api_type: "json",
    kind: "self",
    sr: input.subreddit,
    title: input.title,
    text: input.content,
    resubmit: "true",
    send_replies: "true",
  });

  if (input.flairId) {
    body.set("flair_id", input.flairId);
  }
  if (input.flairText) {
    body.set("flair_text", input.flairText.slice(0, 64));
  }

  const response = await fetch("https://oauth.reddit.com/api/submit", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${accessToken}`,
      "Content-Type": "application/x-www-form-urlencoded",
      "User-Agent": getRedditUserAgent(),
    },
    body,
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Reddit post failed with ${response.status}: ${text}`);
  }

  const payload = JSON.parse(text);
  const errors = payload?.json?.errors ?? [];
  if (errors.length > 0) {
    throw new Error(`Reddit post failed: ${JSON.stringify(errors)}`);
  }

  const data = payload?.json?.data ?? {};
  const thingId = data.name ?? data.id ?? "";
  const url = data.url ?? (data.id ? `https://www.reddit.com/r/${input.subreddit}/comments/${data.id}` : "");
  if (!thingId && !url) {
    throw new Error(`Reddit post returned no receipt: ${text}`);
  }

  return {
    thingId,
    url,
  };
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
  return run(resolveGitCommand(), args, cwd, options);
}

function runGitHubCli(options, args, cwd, runOptions = {}) {
  const command = resolveGitHubCommand(options);
  return run(command.exe, [...command.args, ...args], cwd, runOptions);
}

function run(command, args, cwd, options = {}) {
  const env = {
    ...process.env,
    ...(options.env ?? {}),
  };
  const result = isWindowsCommandScript(command)
    ? spawnSync("cmd.exe", ["/d", "/s", "/c", command, ...args], {
        cwd,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"],
        env,
        windowsHide: true,
      })
    : spawnSync(command, args, {
        cwd,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"],
        env,
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

function resolveGitCommand() {
  return optionalString(process.env.BIFROST_GIT_EXECUTABLE) ?? "git";
}

async function beginBridgeAction(options, action) {
  const baseUrl = optionalString(options["bifrost-base-url"]) ?? optionalString(process.env.BIFROST_BRIDGE_BASE_URL);
  if (!baseUrl) {
    return null;
  }

  const token = optionalString(options["bifrost-token"]) ?? optionalString(process.env.BIFROST_BRIDGE_TOKEN);
  if (!token) {
    throw new Error("Set BIFROST_BRIDGE_TOKEN or pass --bifrost-token when using the Bifrost bridge action ledger.");
  }

  const payload = {
    ...action,
    sourceKind: optionalString(options["source-kind"]) ?? optionalString(process.env.BIFROST_BRIDGE_SOURCE_KIND) ?? "",
    sourceId: optionalString(options["source-id"]) ?? optionalString(process.env.BIFROST_BRIDGE_SOURCE_ID) ?? "",
    authorityReference: optionalString(options["authority-ref"]) ?? optionalString(process.env.BIFROST_BRIDGE_AUTHORITY_REF) ?? "",
    workItemId: optionalString(options["work-item-id"]) ?? null,
    motionId: optionalString(options["motion-id"]) ?? null,
  };

  const requested = await postBridgeJson(
    baseUrl,
    token,
    "/bridge/actions/request",
    payload,
    new Set([202, 403]),
  );

  if (requested.status === 403) {
    throw new Error(`Bifrost denied bridge action: ${requested.body?.policyDecision ?? requested.text}`);
  }

  const id = requested.body?.id;
  if (!id) {
    throw new Error(`Bifrost bridge request returned no action id: ${requested.text}`);
  }

  return {
    id,
    async start() {
      await postBridgeJson(baseUrl, token, `/bridge/actions/${id}/start`, undefined, new Set([202]));
    },
    async complete(receipt) {
      await postBridgeJson(baseUrl, token, `/bridge/actions/${id}/complete`, receipt, new Set([200]));
    },
    async fail(error) {
      const failureReason = error instanceof Error ? error.message : String(error);
      try {
        await postBridgeJson(
          baseUrl,
          token,
          `/bridge/actions/${id}/fail`,
          { failureReason },
          new Set([200, 400]),
        );
      } catch {
        // Best effort: the underlying command error is still the real failure.
      }
    },
  };
}

function parseRequiredJson(text, label) {
  try {
    return JSON.parse(text);
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new Error(`${label} was not valid JSON: ${detail}`);
  }
}

function ensureGitHubBridgeGate(options) {
  const baseUrl = optionalString(options["bifrost-base-url"]) ?? optionalString(process.env.BIFROST_BRIDGE_BASE_URL);
  const token = optionalString(options["bifrost-token"]) ?? optionalString(process.env.BIFROST_BRIDGE_TOKEN);
  const allowUngated =
    options["allow-ungated-github"] === "true" ||
    process.env.BIFROST_ALLOW_UNGATED_GITHUB === "true";

  if (allowUngated && process.env.BIFROST_LOCK_RECOVERY_HATCHES === "true") {
    throw new Error(
      "Dispatched Bifrost work cannot use --allow-ungated-github or BIFROST_ALLOW_UNGATED_GITHUB. " +
      "GitHub mutations from a dispatched turn must go through the normal Bifrost gate and receipt path.",
    );
  }

  if (baseUrl && token) {
    return;
  }

  if (allowUngated) {
    return;
  }

  throw new Error(
    "GitHub bridge actions require Bifrost authorization and receipt logging. " +
      "Set BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN, or use --allow-ungated-github true only for explicit operator recovery.",
  );
}

function ensureBridgeReceiptGate(options) {
  const baseUrl = optionalString(options["bifrost-base-url"]) ?? optionalString(process.env.BIFROST_BRIDGE_BASE_URL);
  const token = optionalString(options["bifrost-token"]) ?? optionalString(process.env.BIFROST_BRIDGE_TOKEN);
  const allowUnreceipted =
    options["allow-unreceipted-activity"] === "true" ||
    process.env.BIFROST_ALLOW_UNRECEIPTED_ACTIVITY === "true";

  if (allowUnreceipted && process.env.BIFROST_LOCK_RECOVERY_HATCHES === "true") {
    throw new Error(
      "Dispatched Bifrost work cannot use --allow-unreceipted-activity or BIFROST_ALLOW_UNRECEIPTED_ACTIVITY. " +
      "External bridge activity from a dispatched turn must go through the normal Bifrost receipt path.",
    );
  }

  if (baseUrl && token) {
    return;
  }

  if (allowUnreceipted) {
    return;
  }

  throw new Error(
    "Bridge activity requires BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN so Bifrost can receipt the external crossing. " +
      "Set BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN, or use --allow-unreceipted-activity true only for explicit operator recovery.",
  );
}

async function postBridgeJson(baseUrl, token, path, payload, allowedStatuses) {
  const url = new URL(path, ensureTrailingSlash(baseUrl));
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Bifrost-Bridge-Token": token,
    },
    body: payload === undefined ? undefined : JSON.stringify(payload),
  });

  const text = await response.text();
  let body = null;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }

  if (!allowedStatuses.has(response.status)) {
    throw new Error(`Bifrost bridge call to ${path} failed with ${response.status}: ${text}`);
  }

  return {
    status: response.status,
    text,
    body,
  };
}

function ensureTrailingSlash(value) {
  return value.endsWith("/") ? value : `${value}/`;
}

function detectGitHubRepositoryFullName(options, repoRoot) {
  const result = runGitHubCli(
    options,
    ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"],
    repoRoot,
    { allowFailure: true },
  );
  return optionalString(result.stdout) ?? "";
}

function resolveGitHubExecutable(options) {
  return resolveGitHubCommand(options).exe;
}

function resolveGitHubCommand(options) {
  return {
    exe: optionalString(options["gh-executable"]) ?? optionalString(process.env.BIFROST_GH_EXECUTABLE) ?? "gh",
    args: splitCommandArgs(options["gh-exec-args"] ?? process.env.BIFROST_GH_EXEC_ARGS ?? ""),
  };
}

function isWindowsCommandScript(command) {
  return /\.cmd$/i.test(command) || /\.bat$/i.test(command);
}

function splitCommandArgs(value) {
  if (typeof value !== "string" || value.trim().length === 0) {
    return [];
  }

  return value
    .split(",")
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function extractGitHubNumber(value) {
  const match = optionalString(value)?.match(/\/(\d+)(?:[#?].*)?$/);
  return match ? match[1] : undefined;
}

function normalizeSubreddit(value) {
  const subreddit = optionalString(value)?.replace(/^\/?r\//i, "");
  if (!subreddit || !/^[A-Za-z0-9_]{3,21}$/.test(subreddit)) {
    throw new Error(`Invalid subreddit "${value}". Use a subreddit name such as GameCultOrg.`);
  }
  return subreddit;
}

function getRedditUserAgent() {
  return optionalString(process.env.BIFROST_REDDIT_USER_AGENT ?? process.env.REDDIT_USER_AGENT) ??
    "GameCult Bifrost bridge/0.1 by GameCultOrg";
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
  reddit-post       Create a self-post in r/GameCultOrg through the Bifrost Reddit app

Examples:
  node tools/bifrost-bridge.mjs github-draft-pr --repo-root E:/Projects/AetheriaLore --identity nibu --title "Nibu: Glitchcraft" --path Aetheria/Articles/Nibu/glitchcraft.md --content-file article.md
  node tools/bifrost-bridge.mjs github-pr-comment --repo-root E:/Projects/AetheriaLore --identity nibu --pr 12 --content "This needs a sharper leash."
  node tools/bifrost-bridge.mjs discord-post --channel-id 1501196543150264332 --persona-name Nibu --content "Draft PR opened: https://github.com/..."
  node tools/bifrost-bridge.mjs discord-dm --recipient-id 123456789 --content "Moderation status update..."
  node tools/bifrost-bridge.mjs reddit-post --title "Nibu: Reset-loop continuity" --persona-name Nibu --content-file thread.md

GitHub note:
  GitHub actions require BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN so Bifrost can gate and receipt the crossing.
  Use --allow-ungated-github true only for explicit operator recovery.
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

async function main() {
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
    case "discord-post":
      await postDiscordMessage(options);
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

async function postDiscordMessage(options) {
  const token = process.env.BIFROST_DISCORD_BOT_TOKEN ?? process.env.DISCORD_BOT_TOKEN;
  const channelId = requireOption(options, "channel-id");
  const content = await readOptionText(options, "content", "content-file");
  const dryRun = options["dry-run"] === "true";

  if (dryRun) {
    printJson({
      dryRun: true,
      action: "discord-post",
      channelId,
      content,
    });
    return;
  }

  if (!token) {
    throw new Error("Set BIFROST_DISCORD_BOT_TOKEN or DISCORD_BOT_TOKEN before posting to Discord.");
  }

  const response = await fetch(`https://discord.com/api/v10/channels/${channelId}/messages`, {
    method: "POST",
    headers: {
      Authorization: `Bot ${token}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ content }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Discord post failed with ${response.status}: ${text}`);
  }

  const message = JSON.parse(text);
  printJson({
    action: "discord-post",
    ok: true,
    channelId,
    messageId: message.id,
    url: `https://discord.com/channels/${message.guild_id ?? "@me"}/${channelId}/${message.id}`,
  });
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

function printHelp() {
  process.stdout.write(`Bifrost bridge

Commands:
  github-draft-pr   Write one file in a target repo and open a draft PR through gh
  discord-post      Post a message to Discord through the configured bot token

Examples:
  node tools/bifrost-bridge.mjs github-draft-pr --repo-root E:/Projects/AetheriaLore --identity nibu --title "Nibu: Glitchcraft" --path Aetheria/Articles/Nibu/glitchcraft.md --content-file article.md
  node tools/bifrost-bridge.mjs discord-post --channel-id 1501196543150264332 --content "Draft PR opened: https://github.com/..."
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const wrapperDir = dirname(fileURLToPath(import.meta.url));
const realGh = process.env.BIFROST_REAL_GH || resolveExecutable("gh", wrapperDir);
const realGhArgs = splitCommandArgs(process.env.BIFROST_REAL_GH_ARGS ?? "");
const mutatingCommands = new Set([
  "gist create",
  "gist delete",
  "gist edit",
  "issue close",
  "issue comment",
  "issue create",
  "issue delete",
  "issue edit",
  "issue lock",
  "issue reopen",
  "pr close",
  "pr comment",
  "pr create",
  "pr edit",
  "pr lock",
  "pr merge",
  "pr ready",
  "pr reopen",
  "pr review",
  "release create",
  "release delete",
  "release edit",
  "repo archive",
  "repo create",
  "repo delete",
  "repo edit",
  "repo fork",
  "repo rename",
  "secret delete",
  "secret set",
  "variable delete",
  "variable set",
]);

if (!realGh) {
  process.stderr.write("Bifrost GitHub gate has no real GitHub CLI executable configured.\n");
  process.exit(1);
}

if (
  process.env.BIFROST_ENFORCE_GITHUB_GATE === "true" &&
  isMutatingGitHubCliCommand(args) &&
  process.env.BIFROST_GITHUB_MUTATION_AUTHORIZED !== "true"
) {
  process.stderr.write(
    "Bifrost blocked GitHub CLI mutation in this dispatched turn. Use tools/bifrost-bridge.mjs so GitHub publication is gated and receipted.\n",
  );
  process.exit(1);
}

const result = isWindowsCommandScript(realGh)
  ? spawnSync("cmd.exe", ["/d", "/s", "/c", realGh, ...realGhArgs, ...args], {
      stdio: "inherit",
      env: process.env,
      windowsHide: true,
    })
  : spawnSync(realGh, [...realGhArgs, ...args], {
      stdio: "inherit",
      env: process.env,
      windowsHide: true,
    });

if (result.error) {
  throw result.error;
}

process.exit(result.status ?? 1);

function resolveExecutable(command, excludedDir) {
  const locator = process.platform === "win32" ? "where.exe" : "which";
  const result = spawnSync(locator, [command], {
    cwd: excludedDir,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });

  if (result.status !== 0) {
    return "";
  }

  const matches = `${result.stdout ?? ""}`
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .filter((line) => !isWithinExcludedDir(line, excludedDir));

  return matches[0] ?? "";
}

function isWithinExcludedDir(candidate, excludedDir) {
  const normalizedCandidate = resolve(candidate).toLowerCase();
  const normalizedExcludedDir = `${resolve(excludedDir).toLowerCase()}\\`;
  return normalizedCandidate.startsWith(normalizedExcludedDir);
}

function isWindowsCommandScript(command) {
  return /\.cmd$/i.test(command) || /\.bat$/i.test(command);
}

function isMutatingGitHubCliCommand(input) {
  const [scope = "", action = ""] = input;
  if (!scope) {
    return false;
  }

  if (scope === "api") {
    return apiMethod(input) !== "GET";
  }

  return mutatingCommands.has(`${scope} ${action}`);
}

function apiMethod(input) {
  for (let index = 0; index < input.length; index += 1) {
    const token = input[index];
    if (token === "--method") {
      return `${input[index + 1] ?? "GET"}`.trim().toUpperCase();
    }

    if (token.startsWith("--method=")) {
      return token.slice("--method=".length).trim().toUpperCase();
    }
  }

  return input.includes("--input") ? "POST" : "GET";
}

function splitCommandArgs(value) {
  if (!value) {
    return [];
  }

  const matches = value.match(/(?:[^\s"]+|"[^"]*")+/g) ?? [];
  return matches
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => (part.startsWith("\"") && part.endsWith("\"")) ? part.slice(1, -1) : part);
}

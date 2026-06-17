#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const wrapperDir = dirname(fileURLToPath(import.meta.url));
const realGh = process.env.BIFROST_REAL_GH || resolveExecutable("gh", wrapperDir);
const realGhArgs = splitCommandArgs(process.env.BIFROST_REAL_GH_ARGS ?? "");
const readOnlyTopLevelCommands = new Set([
  "",
  "help",
  "version",
  "status",
  "browse",
  "search",
]);
const readOnlyScopedCommands = new Map([
  ["auth", new Set(["status"])],
  ["issue", new Set(["list", "status", "view"])],
  ["pr", new Set(["checks", "diff", "list", "status", "view"])],
  ["release", new Set(["download", "list", "view"])],
  ["repo", new Set(["list", "view"])],
  ["run", new Set(["download", "list", "view", "watch"])],
  ["secret", new Set(["list"])],
  ["variable", new Set(["list"])],
  ["workflow", new Set(["list", "view"])],
]);
const globalOptionsWithSeparateValues = new Set([
  "-R",
  "--repo",
  "-h",
  "--hostname",
]);

if (!realGh) {
  process.stderr.write("Bifrost GitHub gate has no real GitHub CLI executable configured.\n");
  process.exit(1);
}

if (
  process.env.BIFROST_ENFORCE_GITHUB_GATE === "true" &&
  requiresBridgeAuthorization(args) &&
  process.env.BIFROST_GITHUB_MUTATION_AUTHORIZED !== "true"
) {
  process.stderr.write(
    "Bifrost blocked GitHub CLI mutation or unclassified command in this dispatched turn. Use tools/bifrost-bridge.mjs so GitHub publication is gated and receipted.\n",
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

function requiresBridgeAuthorization(input) {
  const { scope, action } = parseCommand(input);
  if (scope === "api") {
    return apiMethod(input) !== "GET";
  }

  if (readOnlyTopLevelCommands.has(scope)) {
    return false;
  }

  const readOnlyActions = readOnlyScopedCommands.get(scope);
  if (readOnlyActions?.has(action)) {
    return false;
  }

  return true;
}

function parseCommand(input) {
  let index = 0;
  while (index < input.length) {
    const token = input[index];
    if (!token) {
      index += 1;
      continue;
    }

    if (token === "--") {
      index += 1;
      break;
    }

    if (!token.startsWith("-")) {
      break;
    }

    if (globalOptionsWithSeparateValues.has(token)) {
      index += 2;
      continue;
    }

    if (token.startsWith("--repo=") || token.startsWith("--hostname=")) {
      index += 1;
      continue;
    }

    index += 1;
  }

  return {
    scope: input[index] ?? "",
    action: input[index + 1] ?? "",
  };
}

function apiMethod(input) {
  for (let index = 0; index < input.length; index += 1) {
    const token = input[index];
    if (token === "--method" || token === "-X") {
      return `${input[index + 1] ?? "GET"}`.trim().toUpperCase();
    }

    if (token.startsWith("--method=") || token.startsWith("-X=")) {
      const separator = token.includes("=") ? "=" : "";
      const value = separator ? token.slice(token.indexOf("=") + 1) : "";
      return value.trim().toUpperCase();
    }

    if (
      token === "--input" ||
      token === "-f" ||
      token === "-F" ||
      token === "--field" ||
      token === "--raw-field"
    ) {
      return "POST";
    }

    if (
      token.startsWith("--input=") ||
      token.startsWith("-f=") ||
      token.startsWith("-F=") ||
      token.startsWith("--field=") ||
      token.startsWith("--raw-field=")
    ) {
      return "POST";
    }
  }

  return "GET";
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

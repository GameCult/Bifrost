#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const wrapperDir = dirname(fileURLToPath(import.meta.url));
const realGit = process.env.BIFROST_REAL_GIT || resolveExecutable("git", wrapperDir);

if (!realGit) {
  process.stderr.write("Bifrost Git gate has no real git executable configured.\n");
  process.exit(1);
}

const subcommand = findGitSubcommand(args);
if (
  process.env.BIFROST_ENFORCE_GITHUB_GATE === "true" &&
  subcommand === "push" &&
  process.env.BIFROST_GITHUB_PUSH_AUTHORIZED !== "true"
) {
  process.stderr.write(
    "Bifrost blocked git push in this dispatched turn. Use tools/bifrost-bridge.mjs so GitHub publication is gated and receipted.\n",
  );
  process.exit(1);
}

const result = spawnSync(realGit, args, {
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

function findGitSubcommand(input) {
  const optionsWithSeparateValues = new Set([
    "-C",
    "-c",
    "--exec-path",
    "--git-dir",
    "--work-tree",
    "--namespace",
    "--super-prefix",
    "--config-env",
  ]);

  for (let index = 0; index < input.length; index += 1) {
    const token = input[index];
    if (token === "--") {
      return input[index + 1] ?? "";
    }

    if (!token.startsWith("-")) {
      return token;
    }

    if (optionsWithSeparateValues.has(token)) {
      index += 1;
    }
  }

  return "";
}

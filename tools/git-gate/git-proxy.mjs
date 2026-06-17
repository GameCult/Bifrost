#!/usr/bin/env node
import { spawnSync } from "node:child_process";

const args = process.argv.slice(2);
const realGit = process.env.BIFROST_REAL_GIT;

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

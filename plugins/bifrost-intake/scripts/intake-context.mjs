#!/usr/bin/env node
import { spawn } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const bifrostRoot = process.env.BIFROST_ROOT
  ? resolve(process.env.BIFROST_ROOT)
  : resolve(pluginRoot, "..", "..");
const transportCli = resolve(bifrostRoot, "tools", "agent-transport.mjs");

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const repo = requireOption(options, "repo");
  const agent = optionalString(options.agent);
  const claimedBy = optionalString(options["claimed-by"]) ?? agent ?? "codex";
  const claimed = await runTransport([
    "claim",
    "--repo", repo,
    ...optionalArg("--agent", agent),
    "--claimed-by", claimedBy,
    ...optionalArg("--store", options.store),
  ]);

  if (!claimed) {
    const face = agent ? ` for ${agent}` : "";
    process.stdout.write(
      `No Bifrost intake requests are queued for ${repo}${face}. Do not stall on intake for this turn; continue with the user's direct request or the repo's normal next action.\n`,
    );
    return;
  }

  process.stdout.write(formatClaimedRequest(claimed));
  process.stdout.write("\n");
}

function formatClaimedRequest(request) {
  return `# Bifrost Update Request

ID: ${request.id}
Status: ${request.status}
Target repo: ${request.targetRepoName}
Target Persona: ${request.targetAgentIdentity ?? "any"}
Priority: ${request.priority}
Title: ${request.title}

## Request

${request.requestMarkdown}

## Handling

Work only if this request matches the current repository jurisdiction. When finished, close the request through Bifrost intake and include the PR, commit, or cancellation reason in the close note.`;
}

function runTransport(args) {
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(process.execPath, [transportCli, ...args], {
      cwd: bifrostRoot,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });

    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString("utf8");
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString("utf8");
    });
    child.on("error", rejectPromise);
    child.on("close", (code) => {
      if (code !== 0) {
        rejectPromise(new Error(stderr.trim() || `agent-transport exited with code ${code}`));
        return;
      }

      try {
        resolvePromise(JSON.parse(stdout));
      } catch {
        rejectPromise(new Error(`agent-transport returned non-JSON output: ${stdout || stderr}`));
      }
    });
  });
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

function optionalArg(flag, value) {
  return optionalString(value) ? [flag, optionalString(value)] : [];
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

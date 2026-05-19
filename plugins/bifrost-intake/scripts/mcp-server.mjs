#!/usr/bin/env node
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const bifrostRoot = resolveBifrostRoot();
const transportCli = resolve(bifrostRoot, "tools", "agent-transport.mjs");
const serverInfo = { name: "bifrost-intake", version: "0.1.0" };

const tools = [
  {
    name: "get_intake_context",
    description: "Claim the next matching Bifrost request and return a Codex-ready context packet, or say there is no queued intake work.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["repo"],
      properties: {
        repo: { type: "string", description: "Current repository short name, such as AetheriaLore." },
        agent: { type: "string", description: "Optional current Face identity, such as nibu." },
        claimedBy: { type: "string" },
        store: { type: "string" }
      }
    }
  },
  {
    name: "enqueue_update_request",
    description: "Enqueue a Bifrost agent update request in the CultCache-backed intake store.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["repo", "title", "requestMarkdown"],
      properties: {
        repo: { type: "string", description: "Target repository short name, such as AetheriaLore." },
        agent: { type: "string", description: "Optional target Face identity, such as nibu." },
        title: { type: "string" },
        requestMarkdown: { type: "string", description: "Consensus/task packet to inject when claimed." },
        priority: { type: "integer", default: 50 },
        repoFullName: { type: "string" },
        sourceKind: { type: "string", default: "codex" },
        sourceChannelId: { type: "string" },
        sourceMessageIds: { type: "array", items: { type: "string" } },
        sourcePacketPath: { type: "string" },
        sourcePromptPath: { type: "string" },
        createdBy: { type: "string" },
        store: { type: "string", description: "Optional override path for the .cc store." }
      }
    }
  },
  {
    name: "list_update_requests",
    description: "List Bifrost agent update requests, optionally filtered by repo, Face, or status.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      properties: {
        repo: { type: "string" },
        agent: { type: "string" },
        status: { type: "string", enum: ["queued", "claimed", "completed", "cancelled"] },
        store: { type: "string" }
      }
    }
  },
  {
    name: "claim_update_request",
    description: "Claim the highest-priority queued request for a repository jurisdiction.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["repo"],
      properties: {
        repo: { type: "string" },
        agent: { type: "string" },
        claimedBy: { type: "string" },
        store: { type: "string" }
      }
    }
  },
  {
    name: "close_update_request",
    description: "Mark a Bifrost update request completed or cancelled.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["id", "status"],
      properties: {
        id: { type: "string" },
        status: { type: "string", enum: ["completed", "cancelled"] },
        note: { type: "string" },
        store: { type: "string" }
      }
    }
  },
  {
    name: "format_claimed_request",
    description: "Return a Codex-ready prompt packet for a claimed update request id.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["id"],
      properties: {
        id: { type: "string" },
        store: { type: "string" }
      }
    }
  },
  {
    name: "create_transport_snapshot",
    description: "Write a CultNet raw snapshot of the Bifrost intake store.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["out"],
      properties: {
        out: { type: "string" },
        messageId: { type: "string" },
        store: { type: "string" }
      }
    }
  },
  {
    name: "apply_transport_snapshot",
    description: "Apply a CultNet raw snapshot into the Bifrost intake store.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["in"],
      properties: {
        in: { type: "string" },
        store: { type: "string" }
      }
    }
  }
];

let inputBuffer = Buffer.alloc(0);

process.stdin.on("data", (chunk) => {
  inputBuffer = Buffer.concat([inputBuffer, chunk]);
  drainMessages().catch((error) => {
    sendError(null, -32603, error.message);
  });
});

async function drainMessages() {
  while (true) {
    const headerEnd = inputBuffer.indexOf("\r\n\r\n");
    if (headerEnd === -1) {
      return;
    }

    const header = inputBuffer.subarray(0, headerEnd).toString("utf8");
    const lengthMatch = /^Content-Length:\s*(\d+)/imu.exec(header);
    if (!lengthMatch) {
      throw new Error("MCP message is missing Content-Length header.");
    }

    const length = Number(lengthMatch[1]);
    const bodyStart = headerEnd + 4;
    const bodyEnd = bodyStart + length;
    if (inputBuffer.length < bodyEnd) {
      return;
    }

    const rawBody = inputBuffer.subarray(bodyStart, bodyEnd).toString("utf8");
    inputBuffer = inputBuffer.subarray(bodyEnd);
    await handleMessage(JSON.parse(rawBody));
  }
}

async function handleMessage(message) {
  if (!Object.prototype.hasOwnProperty.call(message, "id")) {
    return;
  }

  try {
    switch (message.method) {
      case "initialize":
        sendResult(message.id, {
          protocolVersion: message.params?.protocolVersion ?? "2024-11-05",
          capabilities: { tools: {} },
          serverInfo,
        });
        return;
      case "tools/list":
        sendResult(message.id, { tools });
        return;
      case "tools/call":
        sendResult(message.id, await callTool(message.params ?? {}));
        return;
      case "ping":
        sendResult(message.id, {});
        return;
      default:
        sendError(message.id, -32601, `Unknown method "${message.method}".`);
    }
  } catch (error) {
    sendError(message.id, -32603, error instanceof Error ? error.message : String(error));
  }
}

async function callTool(params) {
  const name = params.name;
  const args = params.arguments ?? {};

  switch (name) {
    case "get_intake_context":
      return textToolResult(await getIntakeContext(args));
    case "enqueue_update_request":
      return jsonToolResult(await runTransport([
        "enqueue",
        "--repo", requireString(args.repo, "repo"),
        ...optionalArg("--agent", args.agent),
        "--title", requireString(args.title, "title"),
        "--request", requireString(args.requestMarkdown, "requestMarkdown"),
        "--priority", String(args.priority ?? 50),
        ...optionalArg("--repo-full-name", args.repoFullName),
        ...optionalArg("--source-kind", args.sourceKind ?? "codex"),
        ...optionalArg("--source-channel-id", args.sourceChannelId),
        ...optionalArg("--source-message-ids", Array.isArray(args.sourceMessageIds) ? args.sourceMessageIds.join(",") : undefined),
        ...optionalArg("--packet-path", args.sourcePacketPath),
        ...optionalArg("--prompt-path", args.sourcePromptPath),
        ...optionalArg("--created-by", args.createdBy),
        ...storeArg(args.store),
      ]));
    case "list_update_requests":
      return jsonToolResult(await runTransport([
        "list",
        ...optionalArg("--repo", args.repo),
        ...optionalArg("--agent", args.agent),
        ...optionalArg("--status", args.status),
        ...storeArg(args.store),
      ]));
    case "claim_update_request":
      return jsonToolResult(await runTransport([
        "claim",
        "--repo", requireString(args.repo, "repo"),
        ...optionalArg("--agent", args.agent),
        ...optionalArg("--claimed-by", args.claimedBy),
        ...storeArg(args.store),
      ]));
    case "close_update_request":
      return jsonToolResult(await runTransport([
        "close",
        "--id", requireString(args.id, "id"),
        "--status", requireString(args.status, "status"),
        ...optionalArg("--note", args.note),
        ...storeArg(args.store),
      ]));
    case "format_claimed_request":
      return textToolResult(formatClaimedRequest(await requireRequestById(args)));
    case "create_transport_snapshot":
      return jsonToolResult(await runTransport([
        "snapshot",
        "--out", requireString(args.out, "out"),
        ...optionalArg("--message-id", args.messageId),
        ...storeArg(args.store),
      ]));
    case "apply_transport_snapshot":
      return jsonToolResult(await runTransport([
        "apply-snapshot",
        "--in", requireString(args.in, "in"),
        ...storeArg(args.store),
      ]));
    default:
      throw new Error(`Unknown tool "${name}".`);
  }
}

async function getIntakeContext(args) {
  const repo = requireString(args.repo, "repo");
  const claimed = await runTransport([
    "claim",
    "--repo", repo,
    ...optionalArg("--agent", args.agent),
    ...optionalArg("--claimed-by", args.claimedBy ?? args.agent),
    ...storeArg(args.store),
  ]);

  if (!claimed) {
    const face = typeof args.agent === "string" && args.agent.trim().length > 0
      ? ` for ${args.agent.trim()}`
      : "";
    return `No Bifrost intake requests are queued for ${repo}${face}. Do not stall on intake for this turn; continue with the user's direct request or the repo's normal next action.`;
  }

  return formatClaimedRequest(claimed);
}

async function requireRequestById(args) {
  const id = requireString(args.id, "id");
  const requests = await runTransport(["list", ...storeArg(args.store)]);
  const request = requests.find((candidate) => candidate.id === id);
  if (!request) {
    throw new Error(`No Bifrost update request found for id "${id}".`);
  }
  return request;
}

function formatClaimedRequest(request) {
  return `# Bifrost Update Request

ID: ${request.id}
Status: ${request.status}
Target repo: ${request.targetRepoName}
Target Face: ${request.targetAgentIdentity ?? "any"}
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
      } catch (error) {
        rejectPromise(new Error(`agent-transport returned non-JSON output: ${stdout || stderr}`));
      }
    });
  });
}

function jsonToolResult(value) {
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(value, null, 2),
      },
    ],
  };
}

function textToolResult(text) {
  return {
    content: [
      {
        type: "text",
        text,
      },
    ],
  };
}

function optionalArg(flag, value) {
  if (typeof value !== "string" || value.trim().length === 0) {
    return [];
  }
  return [flag, value.trim()];
}

function storeArg(value) {
  return optionalArg("--store", value);
}

function requireString(value, name) {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`Missing required argument "${name}".`);
  }
  return value.trim();
}

function sendResult(id, result) {
  sendMessage({ jsonrpc: "2.0", id, result });
}

function sendError(id, code, message) {
  sendMessage({ jsonrpc: "2.0", id, error: { code, message } });
}

function sendMessage(message) {
  const body = Buffer.from(JSON.stringify(message), "utf8");
  process.stdout.write(`Content-Length: ${body.length}\r\n\r\n`);
  process.stdout.write(body);
}

function resolveBifrostRoot() {
  const candidates = [
    process.env.BIFROST_ROOT,
    resolve(pluginRoot, "..", ".."),
    "E:/Projects/Bifrost",
  ].filter((candidate) => typeof candidate === "string" && candidate.trim().length > 0);

  for (const candidate of candidates) {
    const root = resolve(candidate);
    if (existsSync(resolve(root, "tools", "agent-transport.mjs"))) {
      return root;
    }
  }

  throw new Error(
    "Could not find Bifrost root. Set BIFROST_ROOT to the repository that contains tools/agent-transport.mjs.",
  );
}

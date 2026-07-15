#!/usr/bin/env node
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const bifrostRoot = resolve(scriptDir, "..");
const transportCli = resolve(bifrostRoot, "tools", "agent-transport.mjs");
const bridgeCli = resolve(bifrostRoot, "tools", "bifrost-bridge.mjs");
const defaultStatusDir = resolve(bifrostRoot, ".bifrost", "agent-dispatch");
const defaultProjectsRoot = resolve(bifrostRoot, "..");
const defaultAquariumChannelId = "1501196543150264332";
const defaultPersonaName = "Bifrost";
const defaultPersonaAvatarUrl =
  "https://raw.githubusercontent.com/GameCult/Bifrost/main/src/Bifrost.Web/wwwroot/img/bifrost-profile.png";

async function main() {
  loadBifrostLocalEnv();
  const [command, ...rawArgs] = process.argv.slice(2);
  const options = parseArgs(rawArgs);

  if (!command || command === "help" || command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  if (command === "dispatch" || command === "run-claimed") {
    ensureDispatchReceiptGate(options);
  }

  switch (command) {
    case "dispatch":
      await dispatchQueuedRequests(options);
      return;
    case "run-claimed":
      await runClaimedRequest(options);
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/dispatch-agent-requests.mjs help".`);
  }
}

function loadBifrostLocalEnv() {
  if (process.env.BIFROST_SKIP_LOCAL_ENV === "true") {
    return;
  }

  loadLocalEnv(resolve(bifrostRoot, ".env"));
  loadLocalEnv(resolve(defaultProjectsRoot, "VoidBot", ".env"));
}

async function dispatchQueuedRequests(options) {
  const max = parseInteger(options.max ?? "1", "max");
  const statusDir = resolveOptionPath(options["status-dir"] ?? defaultStatusDir);
  await mkdir(statusDir, { recursive: true });

  const dispatched = [];
  for (let index = 0; index < max; index += 1) {
    const request = claimNextRequest(options);
    if (!request) {
      break;
    }

    const repoRoot = resolveRepoRoot(request, options);
    const runDir = resolve(statusDir, request.id);
    mkdirSync(runDir, { recursive: true });
    const requestPath = resolve(runDir, "request.json");
    const promptPath = resolve(runDir, "prompt.md");
    const logPath = resolve(runDir, "codex.log");
    writeFileSync(requestPath, `${JSON.stringify(request, null, 2)}\n`, "utf8");
    writeFileSync(promptPath, buildCodexPrompt(request, repoRoot), "utf8");

    const child = spawn(
      process.execPath,
      [
        fileURLToPath(import.meta.url),
        "run-claimed",
        "--request-file", requestPath,
        "--repo-root", repoRoot,
        "--prompt-file", promptPath,
        "--log", logPath,
        ...optionalArg("--codex-executable", options["codex-executable"]),
        ...optionalArg("--codex-exec-args", options["codex-exec-args"]),
        ...optionalArg("--model", options.model),
        ...optionalArg("--reasoning-effort", options["reasoning-effort"]),
        ...optionalArg("--sandbox", options.sandbox),
        ...optionalArg("--launch-mode", options["launch-mode"]),
        ...optionalArg("--channel-id", options["channel-id"]),
        ...optionalArg("--persona-name", options["persona-name"]),
        ...optionalArg("--persona-avatar-url", options["persona-avatar-url"]),
        ...(options["no-discord"] === "true" ? ["--no-discord", "true"] : []),
      ],
      {
        cwd: bifrostRoot,
        detached: true,
        stdio: "ignore",
        windowsHide: true,
      },
    );
    child.unref();

    const dispatchRecord = {
      requestId: request.id,
      title: request.title,
      repoRoot,
      promptPath,
      logPath,
      pid: child.pid,
      dispatchedAt: new Date().toISOString(),
    };
    writeFileSync(resolve(runDir, "dispatch.json"), `${JSON.stringify(dispatchRecord, null, 2)}\n`, "utf8");

    dispatched.push(dispatchRecord);
  }

  printJson({
    ok: true,
    dispatchedCount: dispatched.length,
    dispatched,
  });
}

function ensureDispatchReceiptGate(options) {
  if (wantsUnreceiptedActivity(options)) {
    throw new Error(
      "Bifrost dispatch cannot use --allow-unreceipted-activity or BIFROST_ALLOW_UNRECEIPTED_ACTIVITY. " +
      "Use the typed Bifrost CultCache/CultMesh dispatch store instead of launching off-book work.",
    );
  }

  if (hasRemovedHttpBridgeConfig()) {
    throw new Error(
      "BIFROST_BRIDGE_BASE_URL / BIFROST_BRIDGE_TOKEN were removed. " +
      "Bifrost dispatch receipts are written to the typed CultCache/CultMesh request/run artifacts.",
    );
  }
}

function hasRemovedHttpBridgeConfig() {
  return Boolean(optionalString(process.env.BIFROST_BRIDGE_BASE_URL) || optionalString(process.env.BIFROST_BRIDGE_TOKEN));
}

function wantsUnreceiptedActivity(options) {
  return options["allow-unreceipted-activity"] === "true" || process.env.BIFROST_ALLOW_UNRECEIPTED_ACTIVITY === "true";
}

async function runClaimedRequest(options) {
  const requestPath = resolveOptionPath(requireOption(options, "request-file"));
  const repoRoot = resolveOptionPath(requireOption(options, "repo-root"));
  const promptPath = resolveOptionPath(requireOption(options, "prompt-file"));
  const logPath = resolveOptionPath(requireOption(options, "log"));
  const request = JSON.parse(await readFile(requestPath, "utf8"));
  const launchMode = options["launch-mode"] ?? process.env.BIFROST_CODEX_LAUNCH_MODE ?? "app-server";

  if (launchMode === "codex-exec") {
    await runClaimedViaCodexExec(request, repoRoot, promptPath, logPath, options);
    return;
  }

  if (launchMode !== "app-server") {
    throw new Error(`Unsupported --launch-mode "${launchMode}". Use app-server or codex-exec.`);
  }

  await runClaimedViaAppServer(request, repoRoot, promptPath, logPath, options);
}

async function runClaimedViaCodexExec(request, repoRoot, promptPath, logPath, options) {
  const codexExecutable = options["codex-executable"] ?? process.env.CODEX_EXECUTABLE ?? "codex";
  const codexExecArgs = splitCommandArgs(options["codex-exec-args"] ?? process.env.CODEX_EXEC_ARGS ?? "");
  const model = options.model ?? process.env.CODEX_MODEL ?? "gpt-5.4";
  const reasoningEffort = options["reasoning-effort"] ?? process.env.CODEX_MODEL_REASONING_EFFORT ?? "medium";
  const sandbox = options.sandbox ?? "workspace-write";
  const startedAt = new Date().toISOString();
  const resultPath = resolve(dirname(logPath), "result.json");
  await mkdir(dirname(logPath), { recursive: true });
  const bridgeContextEnv = buildBridgeContextEnv(request, dirname(logPath));
  appendLog(logPath, `started ${startedAt}`);
  appendLog(logPath, `request ${request.id}: ${request.title}`);
  appendLog(logPath, `repoRoot ${repoRoot}`);
  let dispatchRun = null;

  try {
    dispatchRun = await beginDispatchRun(request, {
      launchMode: "codex-exec",
      workerProcessId: process.pid,
      threadId: "",
      turnId: "",
      logPath,
      resultPath,
      note: "Codex dispatch process started.",
    });

    const prompt = await readFile(promptPath, "utf8");
    const result = spawnSync(
      codexExecutable,
      [
        ...codexExecArgs,
        "exec",
        "-m", model,
        "-c", 'approval_policy="never"',
        "-c", `model_reasoning_effort=${JSON.stringify(reasoningEffort)}`,
        "--skip-git-repo-check",
        "-s", sandbox,
        "-",
      ],
      {
        cwd: repoRoot,
        input: prompt,
        encoding: "utf8",
        stdio: ["pipe", "pipe", "pipe"],
        env: {
          ...process.env,
          ...bridgeContextEnv,
        },
        windowsHide: true,
      },
    );

    appendLog(logPath, result.stdout ?? "");
    appendLog(logPath, result.stderr ?? "");
    const launchError = result.error ? summarizeError(result.error) : "";
    const ok = result.status === 0 && !launchError;
    const closeStatus = ok ? "completed" : "cancelled";
    const note = ok
      ? `Codex dispatch completed from Bifrost. Log: ${logPath}`
      : launchError
        ? `Codex dispatch failed before completion: ${launchError}. Log: ${logPath}`
        : `Codex dispatch failed with exit ${result.status ?? "unknown"}. Log: ${logPath}`;
    const close = runNodeJson([
      transportCli,
      "close",
      "--id", request.id,
      "--status", closeStatus,
      "--note", note,
    ], bifrostRoot);

    if (launchError) {
      await dispatchRun?.fail({
        threadId: "",
        turnId: "",
        resultPath,
        note,
        error: launchError,
      });
    } else {
      await dispatchRun?.complete({
        status: ok ? "Completed" : "Cancelled",
        threadId: "",
        turnId: "",
        resultPath,
        note,
      });
    }

    await writeDispatchResult(logPath, {
      requestId: request.id,
      ok,
      exitCode: result.status,
      error: launchError,
      launchMode: "codex-exec",
      startedAt,
      finishedAt: new Date().toISOString(),
      close,
    });
  } catch (error) {
    const message = summarizeError(error);
    const note = `Codex dispatch failed before completion: ${message}. Log: ${logPath}`;
    const close = runNodeJson([
      transportCli,
      "close",
      "--id", request.id,
      "--status", "cancelled",
      "--note", note,
    ], bifrostRoot);
    await dispatchRun?.fail({
      threadId: "",
      turnId: "",
      resultPath,
      note,
      error: message,
    });
    await writeDispatchResult(logPath, {
      requestId: request.id,
      ok: false,
      error: message,
      launchMode: "codex-exec",
      startedAt,
      finishedAt: new Date().toISOString(),
      close,
    });
    throw error;
  }
}

async function runClaimedViaAppServer(request, repoRoot, promptPath, logPath, options) {
  const model = options.model ?? process.env.CODEX_MODEL ?? "gpt-5.4";
  const reasoningEffort = options["reasoning-effort"] ?? process.env.CODEX_MODEL_REASONING_EFFORT ?? "medium";
  const sandbox = options.sandbox ?? "workspace-write";
  const startedAt = new Date().toISOString();
  const prompt = await readFile(promptPath, "utf8");
  const resultPath = resolve(dirname(logPath), "result.json");
  await mkdir(dirname(logPath), { recursive: true });
  const client = new CodexAppServerClient({
    logPath,
    command: resolveCodexCommand(options),
    env: buildBridgeContextEnv(request, dirname(logPath)),
  });

  appendLog(logPath, `started ${startedAt}`);
  appendLog(logPath, `request ${request.id}: ${request.title}`);
  appendLog(logPath, `repoRoot ${repoRoot}`);
  appendLog(logPath, "launchMode app-server");

  let threadId;
  let turnId;
  const dispatchRun = await beginDispatchRun(request, {
    launchMode: "app-server",
    workerProcessId: process.pid,
    threadId: "",
    turnId: "",
    logPath,
    resultPath,
    note: "Codex app-server launch started.",
  });
  try {
    await client.start();
    await client.request("initialize", {
      clientInfo: {
        name: "bifrost-dispatcher",
        title: "Bifrost Dispatcher",
        version: "0.1.0",
      },
      capabilities: {
        experimentalApi: true,
      },
    });

    const threadStart = await client.request("thread/start", {
      cwd: repoRoot,
      approvalPolicy: "never",
      approvalsReviewer: "user",
      sandbox,
      model,
      personality: "pragmatic",
      ephemeral: false,
      sessionStartSource: "startup",
      experimentalRawEvents: false,
      persistExtendedHistory: true,
    });
    threadId = threadStart.thread?.id;
    if (!threadId) {
      throw new Error("Codex app-server thread/start returned no thread id.");
    }

    const turnStart = await client.request("turn/start", {
      threadId,
      cwd: repoRoot,
      approvalPolicy: "never",
      approvalsReviewer: "user",
      sandboxPolicy: sandboxPolicyFromMode(sandbox),
      model,
      effort: reasoningEffort,
      personality: "pragmatic",
      input: [
        {
          type: "text",
          text: prompt,
          text_elements: [],
        },
      ],
    });
    turnId = turnStart.turn?.id;
    if (!turnId) {
      throw new Error("Codex app-server turn/start returned no turn id.");
    }

    await writeFile(
      resolve(dirname(logPath), "app-server-dispatch.json"),
      `${JSON.stringify({
        requestId: request.id,
        launchMode: "app-server",
        threadId,
        turnId,
        startedAt,
      }, null, 2)}\n`,
      "utf8",
    );

    if (options["no-discord"] !== "true") {
      postDispatchReceipt(request, { threadId, turnId, startedAt }, options);
    }

    const completed = await client.waitForTurnCompleted(threadId, turnId);
    const ok = completed.turn?.status === "completed";
    const closeStatus = ok ? "completed" : "cancelled";
    const note = ok
      ? `Codex app turn completed for ${threadId}/${turnId}.`
      : `Codex app turn ended as ${completed.turn?.status ?? "unknown"} for ${threadId}/${turnId}.`;
    const close = runNodeJson([
      transportCli,
      "close",
      "--id", request.id,
      "--status", closeStatus,
      "--note", note,
    ], bifrostRoot);
    await dispatchRun?.complete({
      status: ok ? "Completed" : "Cancelled",
      threadId,
      turnId,
      resultPath,
      note,
    });
    await writeDispatchResult(logPath, {
      requestId: request.id,
      ok,
      launchMode: "app-server",
      threadId,
      turnId,
      startedAt,
      finishedAt: new Date().toISOString(),
      close,
    });
  } catch (error) {
    const message = summarizeError(error);
    appendLog(logPath, message);
    if (!threadId || !turnId) {
      releaseClaimedRequest(request.id, `Visible Codex app turn did not start: ${message}`);
    } else {
      runNodeJson([
        transportCli,
        "close",
        "--id", request.id,
        "--status", "cancelled",
        "--note", `Codex app dispatch failed after turn start for ${threadId}/${turnId}.`,
      ], bifrostRoot);
    }
    await dispatchRun?.fail({
      threadId: threadId ?? "",
      turnId: turnId ?? "",
      resultPath,
      note: "Codex dispatch failed.",
      error: message,
    });
    await writeDispatchResult(logPath, {
      requestId: request.id,
      ok: false,
      launchMode: "app-server",
      threadId,
      turnId,
      startedAt,
      finishedAt: new Date().toISOString(),
      error: message,
    });
    process.exitCode = 1;
  } finally {
    client.stop();
  }
}

function claimNextRequest(options) {
  const repo = optionalString(options.repo);
  const agent = optionalString(options.agent);
  if (!repo || repo === "*") {
    const queued = runNodeJson([
      transportCli,
      "list",
      "--status", "queued",
      ...optionalArg("--store", options.store),
    ], bifrostRoot);
    const next = queued[0];
    if (!next) {
      return null;
    }
    return runNodeJson([
      transportCli,
      "claim",
      "--repo", next.targetRepoName,
      ...optionalArg("--agent", agent ?? next.targetAgentIdentity),
      "--claimed-by", options["claimed-by"] ?? "bifrost-dispatcher",
      ...optionalArg("--store", options.store),
    ], bifrostRoot);
  }

  const args = [
    transportCli,
    "claim",
    "--repo", repo,
    ...optionalArg("--agent", agent),
    "--claimed-by", options["claimed-by"] ?? "bifrost-dispatcher",
    ...optionalArg("--store", options.store),
  ];
  return runNodeJson(args, bifrostRoot);
}

function resolveRepoRoot(request, options) {
  if (options["repo-root"]) {
    return resolveOptionPath(options["repo-root"]);
  }
  const projectsRoot = resolveOptionPath(options["projects-root"] ?? defaultProjectsRoot);
  return resolve(projectsRoot, request.targetRepoName);
}

function buildCodexPrompt(request, repoRoot) {
  return `You are ${request.targetAgentIdentity ?? "Codex"}, receiving a Bifrost-dispatched update request.

Target repo: ${request.targetRepoName}
Target workspace: ${repoRoot}
Bifrost request id: ${request.id}
Priority: ${request.priority}

Work the request in this workspace. Prefer a small coherent change, create or update tests/docs when appropriate, and leave the repo in a reviewable state. If this is only an analysis/proposal request, produce the artifact requested. Bifrost will close the transport request after this Codex turn exits.

Transport policy: do not push to GitHub, open a pull request, or publish external changes unless this specific request explicitly asks for that. A Bifrost-dispatched turn may prepare local changes and report the commit/PR command it would run, but the bridge owns publication policy.
If this request explicitly authorizes a governed crossing and you use \`tools/bifrost-bridge.mjs\`, the dispatch runtime already provides bridge provenance for request \`${request.id}\`. Preserve it.

${request.requestMarkdown}
`;
}

function postDispatchReceipt(request, dispatchRecord, options) {
  const channelId = resolveDispatchReceiptChannelId(options);
  const personaName = options["persona-name"] ?? process.env.BIFROST_DISCORD_PERSONA_NAME ?? defaultPersonaName;
  const personaAvatarUrl =
    optionalString(options["persona-avatar-url"]) ??
    optionalString(process.env.BIFROST_DISCORD_PERSONA_AVATAR_URL) ??
    optionalString(process.env.DISCORD_PERSONA_AVATAR_URL_BIFROST) ??
    defaultPersonaAvatarUrl;
  const content = renderDispatchReceiptContent(request);

  runNodeJson([
    bridgeCli,
    "discord-post",
    "--channel-id", channelId,
    "--persona-name", personaName,
    "--content", content,
    "--source-kind", "bifrost_agent_transport_request",
    "--source-id", request.id,
    "--authority-ref", "bifrost_dispatch_started_receipt",
    ...optionalArg("--target-repository-full-name", request.targetRepositoryFullName),
    ...optionalArg("--persona-avatar-url", personaAvatarUrl),
    ...bridgeRecoveryArgs(options),
  ], bifrostRoot);
}

function resolveDispatchReceiptChannelId(options) {
  return (
    optionalString(options["channel-id"]) ??
    optionalString(process.env.BIFROST_DISCORD_CHANNEL_ID) ??
    optionalString(process.env.DISCORD_BIFROST_CHANNEL_ID) ??
    defaultAquariumChannelId
  );
}

function bridgeRecoveryArgs(options) {
  return [];
}

function renderDispatchReceiptContent(request) {
  const topic = summarizeRequestTopic(request);
  const actor = request.targetAgentIdentity
    ? `${request.targetAgentIdentity} / ${request.targetRepoName}`
    : request.targetRepoName;
  return [
    `Bifrost Codex dispatch started`,
    ``,
    `**${request.targetRepoName}: ${topic}**`,
    `Request: \`${request.id}\``,
    `Target: ${actor}`,
    `Status: claimed by dispatcher`,
    `Codex job: started`,
    ``,
    `Codex has started a turn. The claim is now specific enough to inspect, and the work is no longer sitting unclaimed in chat.`,
    `Keep discussion here if it sharpens this request; Bifrost will carry the result back across the bridge.`,
  ].join("\n");
}

function buildBridgeContextEnv(request, runtimeDir) {
  const hooksPath = resolve(bifrostRoot, "tools", "git-hooks");
  const gitGatePath = resolve(bifrostRoot, "tools", "git-gate");
  const githubConfigDir = resolve(runtimeDir, "github-gh-config");
  const gitGlobalConfigPath = resolve(runtimeDir, "github-gitconfig");
  mkdirSync(githubConfigDir, { recursive: true });
  if (!existsSync(gitGlobalConfigPath)) {
    writeFileSync(gitGlobalConfigPath, "", "utf8");
  }
  return {
    BIFROST_BRIDGE_SOURCE_KIND: "bifrost_agent_transport_request",
    BIFROST_BRIDGE_SOURCE_ID: request.id,
    BIFROST_BRIDGE_AUTHORITY_REF: "bifrost_dispatch_execution",
    BIFROST_ENFORCE_GITHUB_GATE: "true",
    BIFROST_LOCK_RECOVERY_HATCHES: "true",
    BIFROST_GIT_EXECUTABLE: resolve(gitGatePath, "git.cmd"),
    BIFROST_GH_EXECUTABLE: resolve(gitGatePath, "gh.cmd"),
    BIFROST_NODE_EXECUTABLE: process.execPath,
    ...buildGitHooksConfigEnv(hooksPath),
    ...buildPathPrependEnv(gitGatePath),
    GH_CONFIG_DIR: githubConfigDir,
    GIT_CONFIG_GLOBAL: gitGlobalConfigPath,
    GH_TOKEN: "",
    GITHUB_TOKEN: "",
    GIT_ASKPASS: "",
    SSH_ASKPASS: "",
    SSH_AUTH_SOCK: "",
    GIT_TERMINAL_PROMPT: "0",
    GCM_INTERACTIVE: "never",
  };
}

function buildGitHooksConfigEnv(hooksPath) {
  const entries = [
    ["core.hooksPath", hooksPath],
    ["credential.helper", ""],
    ["credential.interactive", "never"],
  ];
  const existingCount = Number.parseInt(process.env.GIT_CONFIG_COUNT ?? "", 10);
  const startIndex = Number.isInteger(existingCount) && existingCount >= 0 ? existingCount : 0;
  const env = {
    GIT_CONFIG_COUNT: String(startIndex + entries.length),
  };

  entries.forEach(([key, value], index) => {
    const slot = startIndex + index;
    env[`GIT_CONFIG_KEY_${slot}`] = key;
    env[`GIT_CONFIG_VALUE_${slot}`] = value;
  });

  return env;
}

function buildPathPrependEnv(directory) {
  const currentPath = process.env.Path ?? process.env.PATH ?? "";
  const nextPath = currentPath ? `${directory};${currentPath}` : directory;
  return {
    PATH: nextPath,
    Path: nextPath,
  };
}

class CodexAppServerClient {
  constructor({ command, env, logPath }) {
    this.command = command;
    this.env = env;
    this.logPath = logPath;
    this.child = undefined;
    this.nextId = 1;
    this.buffer = "";
    this.pending = new Map();
    this.turnWaiters = [];
    this.completedTurns = new Map();
  }

  async start() {
    this.child = spawn(this.command.exe, [...this.command.args, "app-server", "--listen", "stdio://"], {
      cwd: bifrostRoot,
      env: {
        ...process.env,
        ...this.env,
      },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });

    this.child.stdout.on("data", (chunk) => this.readStdout(chunk.toString()));
    this.child.stderr.on("data", (chunk) => appendLog(this.logPath, chunk.toString()));
    this.child.on("exit", (code, signal) => {
      const error = new Error(`codex app-server exited with ${code ?? signal ?? "unknown"}`);
      for (const waiter of this.pending.values()) {
        waiter.reject(error);
      }
      this.pending.clear();
      for (const waiter of this.turnWaiters) {
        waiter.reject(error);
      }
      this.turnWaiters = [];
      this.completedTurns.clear();
    });
    this.child.on("error", (error) => {
      for (const waiter of this.pending.values()) {
        waiter.reject(error);
      }
      this.pending.clear();
      for (const waiter of this.turnWaiters) {
        waiter.reject(error);
      }
      this.turnWaiters = [];
      this.completedTurns.clear();
    });
  }

  request(method, params) {
    if (!this.child?.stdin.writable) {
      return Promise.reject(new Error("codex app-server stdin is not writable."));
    }

    const id = this.nextId;
    this.nextId += 1;
    appendLog(this.logPath, `client-request ${method}#${id}`);
    return new Promise((resolvePromise, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Timed out waiting for ${method}#${id}`));
      }, 60000);
      this.pending.set(id, {
        resolve: (value) => {
          clearTimeout(timer);
          resolvePromise(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
      this.child.stdin.write(`${JSON.stringify({ id, method, params })}\n`);
    });
  }

  waitForTurnCompleted(threadId, turnId) {
    const key = `${threadId}:${turnId}`;
    const completed = this.completedTurns.get(key);
    if (completed) {
      this.completedTurns.delete(key);
      return Promise.resolve(completed);
    }
    return new Promise((resolvePromise, reject) => {
      this.turnWaiters.push({ threadId, turnId, resolve: resolvePromise, reject });
    });
  }

  readStdout(text) {
    this.buffer += text;
    while (true) {
      const newline = this.buffer.indexOf("\n");
      if (newline === -1) {
        return;
      }
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      if (line) {
        this.handleMessage(line);
      }
    }
  }

  handleMessage(line) {
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      appendLog(this.logPath, `app-server non-json stdout: ${line}`);
      return;
    }

    if (Object.hasOwn(message, "id") && (Object.hasOwn(message, "result") || Object.hasOwn(message, "error"))) {
      const waiter = this.pending.get(message.id);
      if (!waiter) {
        appendLog(this.logPath, `app-server response for unknown id ${message.id}`);
        return;
      }
      this.pending.delete(message.id);
      if (message.error) {
        waiter.reject(new Error(JSON.stringify(message.error)));
      } else {
        waiter.resolve(message.result);
      }
      return;
    }

    appendLog(this.logPath, `app-server ${message.method ?? "message"}`);
    if (message.method === "turn/completed") {
      const params = message.params ?? {};
      const completedTurnId = params.turn?.id;
      const key = `${params.threadId ?? ""}:${completedTurnId ?? ""}`;
      const remaining = [];
      let resolved = false;
      for (const waiter of this.turnWaiters) {
        if (waiter.threadId === params.threadId && waiter.turnId === completedTurnId) {
          waiter.resolve(params);
          resolved = true;
        } else {
          remaining.push(waiter);
        }
      }
      this.turnWaiters = remaining;
      if (!resolved && params.threadId && completedTurnId) {
        this.completedTurns.set(key, params);
      }
    }
  }

  stop() {
    this.child?.kill();
  }
}

function resolveCodexCommand(options) {
  const executable = options["codex-executable"] ?? process.env.CODEX_EXECUTABLE;
  const extraArgs = splitCommandArgs(options["codex-exec-args"] ?? process.env.CODEX_EXEC_ARGS ?? "");
  if (executable) {
    return { exe: executable, args: extraArgs };
  }

  const codexJs = resolve(process.env.APPDATA ?? "", "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
  if (existsSync(codexJs)) {
    return { exe: process.execPath, args: [codexJs] };
  }

  return { exe: "codex", args: [] };
}

function sandboxPolicyFromMode(mode) {
  if (mode === "danger-full-access") {
    return { type: "dangerFullAccess" };
  }
  if (mode === "read-only") {
    return { type: "readOnly", access: { type: "fullAccess" }, networkAccess: false };
  }
  return {
    type: "workspaceWrite",
    writableRoots: [],
    readOnlyAccess: { type: "fullAccess" },
    networkAccess: false,
    excludeTmpdirEnvVar: false,
    excludeSlashTmp: false,
  };
}

function releaseClaimedRequest(id, note) {
  runNodeJson([
    transportCli,
    "release",
    "--id", id,
    "--note", note,
  ], bifrostRoot);
}

async function writeDispatchResult(logPath, result) {
  await writeFile(
    resolve(dirname(logPath), "result.json"),
    `${JSON.stringify(result, null, 2)}\n`,
    "utf8",
  );
}

async function beginDispatchRun(request, input) {
  void request;
  void input;
  return null;
}

async function postDispatchRunJson(baseUrl, token, path, payload, allowedStatuses) {
  const url = new URL(path, ensureTrailingSlash(baseUrl));
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Bifrost-Bridge-Token": token,
    },
    body: JSON.stringify(payload),
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
    throw new Error(`Bifrost dispatch run call to ${path} failed with ${response.status}: ${text}`);
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

function summarizeError(error) {
  if (error instanceof Error) {
    return error.stack ?? error.message;
  }

  return String(error);
}

function summarizeRequestTopic(request) {
  const title = String(request.title ?? "").trim();
  if (title && !/^Recent\b/i.test(title)) {
    return title;
  }

  const text = String(request.requestMarkdown ?? "");
  const section = text.match(/## What Needs To Be Done\s+([\s\S]*?)(?:\n## |\n```|$)/i)?.[1]?.trim();
  const firstParagraph = section
    ?.split(/\n\s*\n/)
    .map((entry) => entry.replace(/\s+/g, " ").trim())
    .find(Boolean);
  if (firstParagraph) {
    return summarizeAction(firstParagraph);
  }

  const heading = text.match(/^#\s+(.+)$/m)?.[1]?.trim();
  return truncate(heading || "routed update request", 96);
}

function truncate(value, maxLength) {
  return value.length > maxLength ? `${value.slice(0, maxLength - 3)}...` : value;
}

function summarizeAction(value) {
  const normalized = value.replace(/\s+/g, " ").trim();
  const match =
    normalized.match(/^Implement the ([^:]+?) as /i) ??
    normalized.match(/^Update ([^:]+?) from /i) ??
    normalized.match(/^Add (.+?)(?:\.|$)/i);
  return truncate(stripTrailingRepoName(match?.[1] ?? normalized), 96);
}

function stripTrailingRepoName(value) {
  return value.replace(/\s+for\s+[A-Z][A-Za-z0-9._-]+$/g, "");
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

function appendLog(path, text) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${text ?? ""}\n`, { encoding: "utf8", flag: "a" });
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

function optionalArg(flag, value) {
  const normalized = optionalString(value);
  return normalized ? [flag, normalized] : [];
}

function optionalString(value) {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}

function parseInteger(value, name) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isInteger(parsed) || parsed < 1) {
    throw new Error(`--${name} must be a positive integer.`);
  }
  return parsed;
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

function resolveOptionPath(path) {
  return resolve(process.cwd(), path);
}

function printJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
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

function printHelp() {
  process.stdout.write(`Bifrost agent request dispatcher

Commands:
  dispatch     Claim queued Bifrost requests and launch detached Codex turns
  run-claimed  Internal worker for one claimed request

Example:
  node tools/dispatch-agent-requests.mjs dispatch --repo AquaSynth --agent aqua --max 1
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

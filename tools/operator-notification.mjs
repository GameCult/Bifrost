#!/usr/bin/env node
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { createHash } from "node:crypto";

const repoRoot = resolve(import.meta.dirname, "..");
const projectsRoot = resolve(repoRoot, "..");
const defaultStorePath = resolve(projectsRoot, "VoidBot", ".voidbot", "status", "cultmesh", "voidbot-swarm-state.cc");
const requestDocumentType = "gamecult.operator_dm_request";
const requestSchemaId = "gamecult.operator_dm_request.v1";

async function main() {
  const [command, ...rawArgs] = process.argv.slice(2);
  const options = parseArgs(rawArgs);

  if (!command || command === "help" || command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  switch (command) {
    case "publish-idunn-alarm":
      await publishIdunnAlarm(options);
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/operator-notification.mjs help".`);
  }
}

async function publishIdunnAlarm(options) {
  const storePath = resolve(options.store ?? defaultStorePath);
  const alarm = {
    alarmId: readOptionOrEnv(options, "alarm-id", "IDUNN_ALARM_ID", "unknown-alarm"),
    daemonId: readOptionOrEnv(options, "daemon-id", "IDUNN_ALARM_DAEMON_ID", "unknown-daemon"),
    severity: readOptionOrEnv(options, "severity", "IDUNN_ALARM_SEVERITY", "operator-action-required"),
    reason: readOptionOrEnv(options, "reason", "IDUNN_ALARM_REASON", "Idunn raised an operator alarm."),
    raisedAt: readOptionOrEnv(options, "raised-at", "IDUNN_ALARM_RAISED_AT", new Date().toISOString()),
  };
  const request = buildOperatorDmRequest(alarm);

  if (options["dry-run"] === "true") {
    printJson({
      dryRun: true,
      action: "publish-idunn-alarm",
      storePath,
      documentType: requestDocumentType,
      schemaId: requestSchemaId,
      request,
    });
    return;
  }

  const { CultMesh, defineDocumentType } = loadCultMeshRuntime();
  const requestDefinition = defineDocumentType({
    type: requestDocumentType,
    schemaName: requestDocumentType,
    schemaId: requestSchemaId,
    schemaVersion: requestSchemaId,
    contentHash: requestSchemaId,
    global: false,
    name: "requestId",
    schema: parseObjectDocument("Operator DM request"),
  });
  const node = await CultMesh.createNode(storePath, {
    documents: [
      requestDefinition,
      defineDocumentType({
        type: "voidbot.swarm_state_snapshot",
        schemaName: "voidbot.swarm_state_snapshot",
        schemaId: "voidbot.swarm_state_snapshot.v1",
        schemaVersion: "voidbot.swarm_state_snapshot.v1",
        contentHash: "voidbot.swarm_state_snapshot.v1",
        global: false,
        schema: parseObjectDocument("VoidBot swarm snapshot"),
      }),
      defineDocumentType({
        type: "gamecult.eve.provider_advertisement",
        schemaName: "gamecult.eve.provider_advertisement",
        schemaId: "gamecult.eve.provider_advertisement.v1",
        schemaVersion: "gamecult.eve.provider_advertisement.v1",
        contentHash: "gamecult.eve.provider_advertisement.v1",
        global: false,
        name: "providerId",
        schema: parseObjectDocument("Eve provider advertisement"),
      }),
      defineDocumentType({
        type: "gamecult.eve.surface_state",
        schemaName: "gamecult.eve.surface_state",
        schemaId: "gamecult.eve.surface_state.v1",
        schemaVersion: "gamecult.eve.surface_state.v1",
        contentHash: "gamecult.eve.surface_state.v1",
        global: false,
        name: "providerId",
        schema: parseObjectDocument("Eve surface state"),
      }),
      defineDocumentType({
        type: "gamecult.eve.interface_binding",
        schemaName: "gamecult.eve.interface_binding",
        schemaId: "gamecult.eve.interface_binding.v1",
        schemaVersion: "gamecult.eve.interface_binding.v1",
        contentHash: "gamecult.eve.interface_binding.v1",
        global: false,
        name: "bindingId",
        schema: parseObjectDocument("Eve interface binding"),
      }),
    ],
  });
  await node.put(requestDefinition, request.requestId, request);
  await node.flush?.();

  printJson({
    ok: true,
    action: "publish-idunn-alarm",
    storePath,
    documentType: requestDocumentType,
    schemaId: requestSchemaId,
    requestId: request.requestId,
  });
}

function buildOperatorDmRequest(alarm) {
  const requestId = `idunn-alarm-${createHash("sha1")
    .update(JSON.stringify(alarm))
    .digest("hex")
    .slice(0, 16)}`;
  const message = [
    "Idunn needs operator intervention.",
    "",
    `Service: ${alarm.daemonId}`,
    `Severity: ${alarm.severity}`,
    `Reason: ${alarm.reason}`,
    `Alarm: ${alarm.alarmId}`,
    `Raised: ${alarm.raisedAt}`,
    "",
    "Automatic recovery either was not authorized or failed.",
  ].join("\n");

  return {
    requestId,
    command: "owner.dm.send",
    status: "pending",
    service: alarm.daemonId,
    sourceId: alarm.alarmId,
    severity: alarm.severity,
    reason: alarm.reason,
    message,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    requestedBy: "idunn",
    transportOwner: "bifrost",
    schemaName: requestDocumentType,
    schemaVersion: requestSchemaId,
  };
}

function loadCultMeshRuntime() {
  const candidates = [
    resolve(projectsRoot, "CultLib", "packages", "cultmesh-ts", "package.json"),
    resolve(projectsRoot, "CultMeshTS", "package.json"),
  ];

  for (const packageJson of candidates) {
    if (!existsSync(packageJson)) {
      continue;
    }
    try {
      const requireCult = createRequire(packageJson);
      const { CultMesh } = requireCult("cultmesh-ts");
      const { defineDocumentType } = requireCult("cultcache-ts");
      if (CultMesh && defineDocumentType) {
        return { CultMesh, defineDocumentType };
      }
    } catch {
    }
  }

  throw new Error("CultMesh/CultCache TypeScript runtime is unavailable.");
}

function parseObjectDocument(label) {
  return {
    parse(input) {
      if (!input || typeof input !== "object") {
        throw new Error(`${label} must be an object.`);
      }
      return input;
    },
  };
}

function readOptionOrEnv(options, optionName, envName, fallback) {
  return optionalString(options[optionName]) ?? optionalString(process.env[envName]) ?? fallback;
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

function optionalString(value) {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function printJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

function printHelp() {
  process.stdout.write(`Bifrost operator notification CultMesh publisher

Commands:
  publish-idunn-alarm   Publish an Idunn alarm as gamecult.operator_dm_request.v1

Examples:
  node tools/operator-notification.mjs publish-idunn-alarm --dry-run
  node tools/operator-notification.mjs publish-idunn-alarm --daemon-id voidbot --reason "restart failed"
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

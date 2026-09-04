#!/usr/bin/env node
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { createHash } from "node:crypto";
import {
  discordPostCommandDefinition,
  discordPostCommandDocumentType as commandDocumentType,
  discordPostCommandSchemaId as commandSchemaId,
  discordPostReceiptDefinition,
  discordPostReceiptDocumentType as receiptDocumentType,
} from "./bifrost-discord-command-documents.mjs";

const repoRoot = resolve(import.meta.dirname, "..");
const projectsRoot = resolve(repoRoot, "..");

// The gate's own provider store. Bifrost owns the Discord crossing, so an alarm
// is a command addressed to the gate, not a document dropped into somebody
// else's state. cultmesh-bridge-commands.mjs reads this same path by default.
const defaultStorePath = resolve(repoRoot, ".bifrost", "provider-store.cc");

async function main() {
  const [verb, ...rawArgs] = process.argv.slice(2);
  const options = parseArgs(rawArgs);

  if (!verb || verb === "help" || verb === "--help" || verb === "-h") {
    printHelp();
    return;
  }

  switch (verb) {
    case "publish-idunn-alarm":
      await publishIdunnAlarm(options);
      return;
    default:
      throw new Error(`Unknown command "${verb}". Run "node tools/operator-notification.mjs help".`);
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
  const recipientId =
    optionalString(options["recipient-id"]) ??
    optionalString(process.env.IDUNN_OPERATOR_DISCORD_ID) ??
    optionalString(process.env.BIFROST_OPERATOR_DISCORD_ID) ??
    optionalString(process.env.OWNER_DISCORD_ID);
  if (!recipientId) {
    throw new Error(
      "Set --recipient-id, IDUNN_OPERATOR_DISCORD_ID, BIFROST_OPERATOR_DISCORD_ID, or OWNER_DISCORD_ID " +
        "so Bifrost knows which operator to reach.",
    );
  }

  const command = buildDiscordDmCommand(alarm, recipientId);
  const waitSeconds =
    Number.parseInt(readOptionOrEnv(options, "wait-seconds", "IDUNN_ALARM_WAIT_SECONDS", "0"), 10) || 0;

  // Build the runtime and definitions before honouring --dry-run. A dry run that
  // skips the runtime cannot notice a broken document definition, which is how
  // this publisher shipped unable to publish anything at all.
  const { CultMesh, defineDocumentType } = loadCultMeshRuntime();
  const commandDefinition = discordPostCommandDefinition(defineDocumentType);
  const receiptDefinition = discordPostReceiptDefinition(defineDocumentType);

  if (options["dry-run"] === "true") {
    printJson({
      dryRun: true,
      action: "publish-idunn-alarm",
      storePath,
      documentType: commandDocumentType,
      schemaId: commandSchemaId,
      command,
    });
    return;
  }

  const node = await CultMesh.createNode(storePath, {
    documents: [commandDefinition, receiptDefinition],
  });
  await node.put(commandDefinition, command.commandId, command);
  await node.flush?.();

  const receipt =
    waitSeconds > 0
      ? await waitForReceipt(node, receiptDefinition, command.commandId, waitSeconds * 1000)
      : undefined;

  printJson({
    ok: receipt ? receipt.ok === true : true,
    action: "publish-idunn-alarm",
    storePath,
    documentType: commandDocumentType,
    schemaId: commandSchemaId,
    commandId: command.commandId,
    delivery: receipt
      ? {
          status: receipt.status,
          ok: receipt.ok,
          messageId: receipt.messageId,
          transport: receipt.transport,
          error: receipt.error,
        }
      : { status: "pending", note: "Not awaited. Pass --wait-seconds to confirm delivery." },
  });

  // A receipt that says the crossing failed must fail the caller. Idunn reads
  // the exit code; reporting ok:false on stdout and exiting 0 would hand it a
  // success it can act on.
  if (receipt && receipt.ok !== true) {
    process.exitCode = 1;
  }
}

function buildDiscordDmCommand(alarm, recipientId) {
  const commandId = `idunn-alarm-${createHash("sha1").update(JSON.stringify(alarm)).digest("hex").slice(0, 16)}`;
  const content = [
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
  const requestedAt = new Date().toISOString();

  const command = {
    schemaName: commandDocumentType,
    schemaVersion: commandSchemaId,
    commandId,
    command: "discord-dm",
    status: "pending",
    requestedBy: "idunn",
    requestedAt,
    updatedAt: requestedAt,
    source: { kind: "idunn-operator-alarm", id: alarm.alarmId },
    actor: { id: "idunn", displayName: "Idunn" },
    payload: { recipientId, content },
  };

  const commandUri = optionalString(process.env.BIFROST_CULTMESH_COMMAND_URI);
  if (commandUri) {
    command.commandUri = commandUri;
  }
  return command;
}

async function waitForReceipt(node, definition, commandId, timeoutMs) {
  const startedAt = Date.now();
  while (Date.now() - startedAt <= timeoutMs) {
    await node.cache?.pullAllBackingStores?.();
    const receipt = await node.get(definition, commandId);
    if (receipt) {
      return receipt;
    }
    await new Promise((done) => setTimeout(done, 250));
  }
  throw new Error(
    `Timed out after ${Math.round(timeoutMs / 1000)}s waiting for a ${receiptDocumentType} for ${commandId}. ` +
      "Is the Bifrost bridge command pump running?",
  );
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

Publishes an Idunn operator alarm as a ${commandSchemaId} document addressed to
the Bifrost Discord gate. Bifrost owns the crossing; this tool only asks. The
bridge command pump turns the command into a DM and writes back a
${receiptDocumentType}.v1 receipt.

Commands:
  publish-idunn-alarm   Publish an Idunn alarm as a discord-dm gate command

Options:
  --recipient-id <id>   Operator Discord user id (or IDUNN_OPERATOR_DISCORD_ID /
                        BIFROST_OPERATOR_DISCORD_ID / OWNER_DISCORD_ID)
  --store <path>        Gate provider store (default: .bifrost/provider-store.cc)
  --wait-seconds <n>    Wait for the delivery receipt instead of returning at publish
  --dry-run             Build the command and definitions without writing

Examples:
  node tools/operator-notification.mjs publish-idunn-alarm --dry-run
  node tools/operator-notification.mjs publish-idunn-alarm --daemon-id voidbot --reason "restart failed" --wait-seconds 30
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

#!/usr/bin/env node
import { createRequire } from "node:module";
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, "..");
const projectsRoot = resolve(repoRoot, "..");
const defaultStorePath = resolve(repoRoot, ".bifrost", "provider-advertisement.cc");

const cultCacheRequire = createRequire(resolve(projectsRoot, "CultCacheTS", "package.json"));
const {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentType,
} = cultCacheRequire(resolve(projectsRoot, "CultCacheTS", "dist", "index.js"));

const documentType = "gamecult.eve.provider_advertisement";
const schemaId = "gamecult.eve.provider_advertisement.v1";
const documentId = "bifrost";

const advertisementDefinition = defineDocumentType({
  type: documentType,
  schemaId,
  schemaName: documentType,
  schemaVersion: schemaId,
  schema: {
    parse: parseAdvertisement,
  },
  name: "providerId",
  indexes: {
    serviceName: "serviceName",
  },
  members: [
    { slot: 1, memberName: "providerId", typeName: "string", isName: true },
    { slot: 2, memberName: "serviceName", typeName: "string", indexAlias: "serviceName" },
    { slot: 3, memberName: "contractPath", typeName: "string" },
    { slot: 4, memberName: "generatedAt", typeName: "string" },
    { slot: 5, memberName: "authority", typeName: "object" },
    { slot: 6, memberName: "namespaces", typeName: "object", isMany: true },
    { slot: 7, memberName: "schemas", typeName: "object", isMany: true },
    { slot: 8, memberName: "witnesses", typeName: "object", isMany: true },
    { slot: 9, memberName: "surfaces", typeName: "object", isMany: true },
    { slot: 10, memberName: "commandBoundaries", typeName: "object", isMany: true },
    { slot: 11, memberName: "styleCapabilities", typeName: "object", isMany: true },
    { slot: 12, memberName: "demotions", typeName: "string", isMany: true },
  ],
});

async function main() {
  const [command, ...rawArgs] = process.argv.slice(2);
  const options = parseArgs(rawArgs);

  if (!command || command === "help" || command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  switch (command) {
    case "export":
      await exportAdvertisement(options);
      return;
    case "print":
      printJson(buildAdvertisement(options));
      return;
    case "schema":
      printJson({
        documentType,
        schemaId,
        documentId,
        members: advertisementDefinition.members,
      });
      return;
    default:
      throw new Error(`Unknown command "${command}". Run "node tools/provider-advertisement.mjs help".`);
  }
}

async function exportAdvertisement(options) {
  const storePath = resolveOptionPath(options.out ?? options.store ?? defaultStorePath);
  await mkdir(dirname(storePath), { recursive: true });

  const cache = CultCache.builder()
    .withDocumentType(advertisementDefinition)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.pullAllBackingStores();
  const advertisement = buildAdvertisement(options);
  await cache.put(advertisementDefinition, advertisement.providerId, advertisement);

  printJson({
    ok: true,
    documentType,
    schemaId,
    providerId: advertisement.providerId,
    out: storePath,
    surfaces: advertisement.surfaces.map((surface) => surface.id),
    witnesses: advertisement.witnesses.map((witness) => witness.path),
  });
}

function buildAdvertisement(options) {
  return parseAdvertisement({
    providerId: "bifrost",
    serviceName: "Bifrost",
    contractPath: "docs/verse-service-contract.md",
    generatedAt: options["generated-at"] ?? new Date().toISOString(),
    authority: {
      owner: "Bifrost",
      role: "GameCult labor, governance, patron pressure, project work, account membership, and governed-public-crossing provider",
      presentationOwner: "Eve/CultUI",
      discoveryOwner: "Odin through CultMesh",
      stateOwner: "Bifrost typed state with CultCache .cc witnesses or export paths",
      runtimeMigration: "not performed by this advertisement",
    },
    namespaces: [
      namespace("gamecult.bifrost.service", "service registration, build/version, schema catalog, and command discovery"),
      namespace("gamecult.bifrost.governance", "motions, topic threads, comments, votes, approvals, objections, and policy receipts"),
      namespace("gamecult.bifrost.work", "projects, work items, claims, review state, completion artifacts, and maintainer acceptance"),
      namespace("gamecult.bifrost.economics", "patron pressure, contributor credit, ledger entries, payout proposal batches, and revenue-share inputs"),
      namespace("gamecult.bifrost.bridge", "GitHub, Discord, CultNet/CultCache, and future collaboration crossings plus receipts"),
      namespace("gamecult.bifrost.surface.product", "member, patron, contributor, and project-facing Eve product surfaces"),
      namespace("gamecult.bifrost.surface.operator", "readiness, witness, bridge, schema, migration, and deploy operator surfaces"),
    ],
    schemas: [
      schema(schemaId, documentType, "this provider advertisement"),
      schema("gamecult.eve.surface.v1", "gamecult.eve.surface", "Eve/CultUI surface compositions lowered by Eve runtimes"),
      schema("bifrost.governance.topic.v0", "bifrost.governance.topic", "existing CultCache governance topic witness"),
      schema("bifrost.governance.topic_comment.v0", "bifrost.governance.topic-comment", "existing CultCache governance comment witness"),
      schema("bifrost.agent-transport.update-request.v0", "bifrost.agent-transport.update-request", "existing CultCache/CultNet agent intake witness"),
      schema("bifrost.work_item.v0", "bifrost.work-item", "planned Postgres-to-CultCache work item witness"),
      schema("bifrost.motion.v0", "bifrost.motion", "planned app-native motion witness"),
      schema("bifrost.vote.v0", "bifrost.vote", "planned motion vote witness"),
      schema("bifrost.ledger_entry.v0", "bifrost.ledger-entry", "planned contributor/patron ledger witness"),
      schema("bifrost.bridge_action.v0", "bifrost.bridge-action", "planned governed crossing command witness"),
      schema("bifrost.bridge_receipt.v0", "bifrost.bridge-receipt", "planned governed crossing result witness"),
      schema("bifrost.member_capability_snapshot.v0", "bifrost.member-capability-snapshot", "planned Heimdall-consumed membership capability witness"),
    ],
    witnesses: [
      witness(".bifrost/provider-advertisement.cc", schemaId, "current", "read-only provider advertisement exported by this tool"),
      witness(".bifrost/governance-threads.cc", "bifrost.governance.topic.v0; bifrost.governance.topic_comment.v0", "current", "governance discussion, approvals, and dispatch promotion topics"),
      witness(".bifrost/agent-transport.cc", "bifrost.agent-transport.update-request.v0", "current", "repo Face update requests and dispatch queue state"),
      witness(".bifrost/work-items.cc", "bifrost.work_item.v0", "planned-export", "work items exported from the alpha transactional store"),
      witness(".bifrost/motions.cc", "bifrost.motion.v0; bifrost.vote.v0", "planned-export", "app-native motions and votes exported from the alpha transactional store"),
      witness(".bifrost/ledger.cc", "bifrost.ledger_entry.v0", "planned-export", "patron and contributor ledger entries exported from the alpha transactional store"),
      witness(".bifrost/member-capabilities.cc", "bifrost.member_capability_snapshot.v0", "planned-export", "membership and account capability snapshots consumed by Bifrost"),
      witness(".bifrost/bridge-receipts.cc", "bifrost.bridge_action.v0; bifrost.bridge_receipt.v0", "planned-export", "governed public crossing actions and receipts"),
      witness(".bifrost/eve-surfaces.cc", "gamecult.eve.surface.v1", "planned-export", "product and operator Eve/CultUI surface publications"),
    ],
    surfaces: [
      surface("bifrost.account", "Account Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", [
        "membership status",
        "Heimdall-linked account projection",
        "grant consumption",
        "audit trail lowerings",
      ]),
      surface("bifrost.patron", "Patron Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", [
        "patron standing",
        "priority pressure",
        "pledge/reward influence",
        "receipts",
      ]),
      surface("bifrost.project", "Project Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", [
        "project membership",
        "repository links",
        "maintainer authority",
        "work boards",
      ]),
      surface("bifrost.work", "Work Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", [
        "work items",
        "claims",
        "review",
        "blockers",
        "completion artifacts",
      ]),
      surface("bifrost.motion", "Motion Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", [
        "motions",
        "topic threads",
        "votes",
        "approvals",
        "objections",
      ]),
      surface("bifrost.operator", "Bifrost Operator Verse", "gamecult.bifrost.surface.operator", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", [
        "readiness",
        "witness freshness",
        "bridge queues",
        "schema catalog",
        "migration drift",
      ]),
    ],
    commandBoundaries: [
      boundary("work", "Bifrost", [
        "claim work",
        "submit completion",
        "record blockers",
        "maintainer accept/reject",
      ], [
        "does not execute payout",
        "does not let GitHub issue state override Bifrost work authority",
      ]),
      boundary("motion", "Bifrost", [
        "open motion",
        "comment",
        "vote",
        "approve",
        "object",
        "promote to dispatch",
      ], [
        "Discord mirrors cannot become canonical governance without a committed Bifrost document",
      ]),
      boundary("patron", "Bifrost", [
        "record patron pressure",
        "surface reward influence",
        "emit standing receipts",
      ], [
        "does not charge cards",
        "does not execute external payout rails",
      ]),
      boundary("project", "Bifrost plus project maintainers", [
        "link repositories",
        "publish work boards",
        "surface maintainer authority",
      ], [
        "does not seize repo Face cognition or project-local ownership",
      ]),
      boundary("account", "Bifrost consumes Heimdall claims", [
        "display membership status",
        "consume linked-account grants",
        "record Bifrost audit trails",
      ], [
        "does not mint identity grants",
        "does not own OAuth provider truth",
      ]),
      boundary("bridge", "Bifrost", [
        "prepare governed public crossings",
        "execute approved handoffs",
        "record receipts",
      ], [
        "does not treat local protocol JSON as work authority",
        "does not touch secrets in this advertisement",
      ]),
    ],
    styleCapabilities: [
      style("density", ["compact TUI grids", "operator scan tables", "member-facing board/list/detail lowerings"]),
      style("visualEncoding", ["status badges", "priority swatches", "role/tier labels", "receipt/audit timelines"]),
      style("interaction", ["tabs", "filters", "segmented modes", "claim/review/vote command affordances", "read-only witness inspection"]),
      style("loweringTargets", ["Eve native", "browser/Razor transitional lowering", "compact TUI", "future room/overlay projections"]),
      style("tone", ["quiet operational product", "governance/labor clarity", "no crypto/DAO styling", "no separate marketing dashboard truth"]),
    ],
    demotions: [
      "Razor Pages are browser lowerings, not the canonical presentation owner.",
      "HTTP health/readiness JSON is a probe, not service truth.",
      "Discord messages are mirrors and input surfaces until Bifrost commits typed state.",
      "Local dispatch JSON is evidence for receipts, not command authority.",
      "This advertisement is read-only discovery metadata and does not migrate runtime state.",
    ],
  });
}

function namespace(id, purpose) {
  return { id, purpose };
}

function schema(id, type, purpose) {
  return { id, type, purpose };
}

function witness(path, schemaIds, status, purpose) {
  return { path, schemaIds, status, purpose };
}

function surface(id, name, namespace, schemaId, witnessPath, capabilities) {
  return { id, name, namespace, schemaId, witnessPath, capabilities };
}

function boundary(area, owner, commands, forbiddenAuthority) {
  return { area, owner, commands, forbiddenAuthority };
}

function style(area, capabilities) {
  return { area, capabilities };
}

function parseAdvertisement(input) {
  if (!input || typeof input !== "object") {
    throw new Error("Provider advertisement must be an object.");
  }

  const advertisement = {
    providerId: requireString(input.providerId, "providerId"),
    serviceName: requireString(input.serviceName, "serviceName"),
    contractPath: requireString(input.contractPath, "contractPath"),
    generatedAt: requireString(input.generatedAt, "generatedAt"),
    authority: requireObject(input.authority, "authority"),
    namespaces: requireObjectArray(input.namespaces, "namespaces"),
    schemas: requireObjectArray(input.schemas, "schemas"),
    witnesses: requireObjectArray(input.witnesses, "witnesses"),
    surfaces: requireObjectArray(input.surfaces, "surfaces"),
    commandBoundaries: requireObjectArray(input.commandBoundaries, "commandBoundaries"),
    styleCapabilities: requireObjectArray(input.styleCapabilities, "styleCapabilities"),
    demotions: requireStringArray(input.demotions, "demotions"),
  };

  const requiredSurfaces = new Set(["bifrost.work", "bifrost.motion", "bifrost.patron", "bifrost.project", "bifrost.account"]);
  const surfaceIds = new Set(advertisement.surfaces.map((surface) => surface.id));
  for (const id of requiredSurfaces) {
    if (!surfaceIds.has(id)) {
      throw new Error(`Provider advertisement must name ${id}.`);
    }
  }

  return advertisement;
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

function requireString(value, field) {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`Provider advertisement field "${field}" must be a non-empty string.`);
  }

  return value.trim();
}

function requireObject(value, field) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`Provider advertisement field "${field}" must be an object.`);
  }

  return value;
}

function requireObjectArray(value, field) {
  if (!Array.isArray(value) || value.some((item) => !item || typeof item !== "object" || Array.isArray(item))) {
    throw new Error(`Provider advertisement field "${field}" must be an array of objects.`);
  }

  return value;
}

function requireStringArray(value, field) {
  if (!Array.isArray(value) || value.some((item) => typeof item !== "string" || item.trim().length === 0)) {
    throw new Error(`Provider advertisement field "${field}" must be an array of non-empty strings.`);
  }

  return value.map((item) => item.trim());
}

function resolveOptionPath(path) {
  return resolve(process.cwd(), path);
}

function printJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

function printHelp() {
  process.stdout.write(`Bifrost Eve provider advertisement

Commands:
  export   Write the Bifrost gamecult.eve.provider_advertisement.v1 document to a CultCache .cc witness
  print    Print the advertisement as protocol-debug JSON without writing state
  schema   Print document type metadata

Options:
  --out <path>            Override export path; defaults to .bifrost/provider-advertisement.cc
  --generated-at <iso>    Pin generatedAt for deterministic fixture checks

Examples:
  node tools/provider-advertisement.mjs print
  node tools/provider-advertisement.mjs export --out .bifrost/provider-advertisement.cc
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

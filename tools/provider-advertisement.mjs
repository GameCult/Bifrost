#!/usr/bin/env node
import { createRequire } from "node:module";
import { spawnSync } from "node:child_process";
import { mkdir } from "node:fs/promises";
import { existsSync, statSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, "..");
const projectsRoot = resolve(repoRoot, "..");
const defaultStorePath = resolve(repoRoot, ".bifrost", "provider-advertisement.cc");

const cultCacheRequire = createRequire(resolveCultCachePackagePath());
const cultCacheRuntime = loadCultCacheRuntime();
const {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentType,
} = cultCacheRuntime;

function loadCultCacheRuntime() {
  const candidates = [
    resolve(projectsRoot, "CultLib", "packages", "cultcache-ts", "dist", "index.js"),
    resolve(projectsRoot, "CultCacheTS", "dist", "index.js"),
  ];

  for (const candidate of candidates) {
    try {
      const runtime = cultCacheRequire(candidate);
      if (runtime.CultCache && runtime.SingleFileMessagePackBackingStore && runtime.defineDocumentType) {
        return runtime;
      }
    } catch {
      // Try the next local CultCache runtime candidate.
    }
  }

  throw new Error("CultCache TypeScript runtime with document APIs is unavailable.");
}

function resolveCultCachePackagePath() {
  const candidates = [
    resolve(projectsRoot, "CultLib", "packages", "cultcache-ts", "package.json"),
    resolve(projectsRoot, "CultCacheTS", "package.json"),
    resolve(projectsRoot, "CultCacheTS", "node_modules", "cultcache-ts", "package.json"),
  ];

  for (const candidate of candidates) {
    if (existsSync(candidate)) {
      return candidate;
    }
  }

  throw new Error(`CultCache TypeScript runtime is unavailable. Tried: ${candidates.join(", ")}`);
}

const documentType = "gamecult.eve.provider_advertisement";
const schemaId = "gamecult.eve.provider_advertisement.v1";
const documentId = "bifrost";
const surfaceDocumentType = "gamecult.eve.surface_state";
const surfaceSchemaId = "gamecult.eve.surface_state.v1";
const interfaceBindingDocumentType = "gamecult.eve.interface_binding";
const interfaceBindingSchemaId = "gamecult.eve.interface_binding.v1";
const verseId = "bifrost.local";
const rootVerse = "asgard";
const currentMachine = "starfire";
const canonicalService = `${rootVerse}.bifrost`;
const locatedService = `${rootVerse}.${currentMachine}.bifrost`;
const plannedLocatedService = `${rootVerse}.yggdrasil.bifrost`;

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
    { slot: 13, memberName: "rootVerse", typeName: "string" },
    { slot: 14, memberName: "canonicalService", typeName: "string" },
    { slot: 15, memberName: "locatedService", typeName: "string" },
    { slot: 16, memberName: "cultMeshAddress", typeName: "string" },
    { slot: 17, memberName: "endpoints", typeName: "object", isMany: true },
    { slot: 18, memberName: "routes", typeName: "object", isMany: true },
  ],
});

const surfaceDefinition = defineDocumentType({
  type: surfaceDocumentType,
  schemaId: surfaceSchemaId,
  schemaName: surfaceDocumentType,
  schemaVersion: surfaceSchemaId,
  schema: { parse: parseObjectDocument("Eve surface state") },
  name: "providerId",
  members: [
    { slot: 0, memberName: "providerId", typeName: "string", isName: true },
    { slot: 1, memberName: "title", typeName: "string" },
    { slot: 2, memberName: "version", typeName: "long" },
    { slot: 3, memberName: "updatedAt", typeName: "string" },
    { slot: 4, memberName: "surface", typeName: "object" },
  ],
});

const interfaceBindingDefinition = defineDocumentType({
  type: interfaceBindingDocumentType,
  schemaId: interfaceBindingSchemaId,
  schemaName: interfaceBindingDocumentType,
  schemaVersion: interfaceBindingSchemaId,
  schema: { parse: parseObjectDocument("Eve interface binding") },
  name: "bindingId",
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
    case "print-surface":
      printJson(buildOperatorSurface(await collectStats(options)));
      return;
    case "print-binding":
      {
        const stats = await collectStats(options);
        printJson(buildInterfaceBinding(buildOperatorSurface(stats), stats));
      }
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
    .withDocumentType(surfaceDefinition)
    .withDocumentType(interfaceBindingDefinition)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.pullAllBackingStores();
  const advertisement = buildAdvertisement(options);
  const stats = await collectStats(options);
  const surface = buildOperatorSurface(stats, options);
  const binding = buildInterfaceBinding(surface, stats, options);
  await cache.put(advertisementDefinition, advertisement.providerId, advertisement);
  await cache.put(surfaceDefinition, surface.providerId, surface);
  await cache.put(interfaceBindingDefinition, binding.bindingId, binding);

  printJson({
    ok: true,
    documentType,
    schemaId,
    providerId: advertisement.providerId,
    out: storePath,
    surfaces: advertisement.surfaces.map((surface) => surface.id),
    witnesses: advertisement.witnesses.map((witness) => witness.path),
    stats: stats.summary,
  });
}

function buildAdvertisement(options) {
  return parseAdvertisement({
    providerId: "bifrost",
    serviceName: "Bifrost",
    contractPath: "docs/verse-service-contract.md",
    generatedAt: options["generated-at"] ?? new Date().toISOString(),
    rootVerse,
    canonicalService,
    locatedService,
    plannedLocatedService,
    cultMeshAddress: locatedService,
    endpoints: [
      endpoint("operator-tui", `${locatedService}/eve/tui`, "gamecult.eve.surface.v1", ["tui", "nightwing-tui"]),
      endpoint("operator-gui", `${locatedService}/eve/gui`, "gamecult.eve.surface.v1", ["gui", "browser", "eve-native"]),
      endpoint("operator-commands", `${locatedService}/commands`, "bifrost.bridge_action.v0", ["command"]),
      endpoint("bridge-action-ledger", `${locatedService}/bridge/actions`, "bifrost.bridge_action.v0", ["command", "bridge", "receipt"]),
      endpoint("patron-support-intake", `${locatedService}/heimdall/patron-support/events`, "bifrost.patron_support_event.v0", ["heimdall", "patronage", "intake"]),
      endpoint("github-webhooks", `${locatedService}/github/webhooks`, "bifrost.work_item.v0", ["github", "work-sync", "review-sync"]),
      endpoint("motion-surface", "https://bifrost.gamecult.org/eve/governance/surface", "gamecult.eve.surface.v1", ["product", "governance", "motion"]),
      endpoint("motion-commands", "https://bifrost.gamecult.org/eve/governance/commands", "bifrost.motion_command.v0", ["command", "governance", "motion"]),
    ],
    routes: [
      route("cultcache-witness", ".bifrost/provider-advertisement.cc", "local-cultcache", true),
      route("websocket-bridge", "ws://192.168.1.66:8797/eve/deck", "compatibility-eve-deck", true),
    ],
    authority: {
      owner: "Bifrost",
      role: "GameCult labor, governance, patron pressure, project work, account membership, and governed-public-crossing provider",
      presentationOwner: "Eve/CultUI",
      discoveryOwner: "Odin through CultMesh",
      stateOwner: "Bifrost typed state with CultCache .cc witnesses or export paths",
      runtimeMigration: `currently ${locatedService}; planned move target ${plannedLocatedService}`,
    },
    namespaces: [
      namespace("gamecult.bifrost.service", "service registration, build/version, schema catalog, and command discovery"),
      namespace("gamecult.bifrost.governance", "motions, topic threads, comments, votes, approvals, objections, and policy receipts"),
      namespace("gamecult.bifrost.work", "projects, work items, claims, review state, completion artifacts, and maintainer acceptance"),
      namespace("gamecult.bifrost.economics", "patron pressure, contributor credit, ledger entries, payout proposal batches, and revenue-share inputs"),
      namespace("gamecult.bifrost.bridge", "GitHub, Discord, Reddit, CultNet/CultCache, and future collaboration crossings plus receipts"),
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
      schema("bifrost.motion.v0", "bifrost.motion", "app-native motion witness"),
      schema("bifrost.vote.v0", "bifrost.vote", "motion vote witness"),
      schema("bifrost.motion_command.v0", "bifrost.motion-command", "Eve Motion Verse command envelope for create, vote, and close"),
      schema("bifrost.ledger_entry.v0", "bifrost.ledger-entry", "planned contributor/patron ledger witness"),
      schema("bifrost.bridge_action.v0", "bifrost.bridge-action", "current hosted governed crossing command witness"),
      schema("bifrost.bridge_receipt.v0", "bifrost.bridge-receipt", "current hosted governed crossing result witness"),
      schema("bifrost.patron_support_event.v0", "bifrost.patron-support-event", "current hosted Heimdall-signed patron support fact consumed by Bifrost"),
      schema("bifrost.member_capability_snapshot.v0", "bifrost.member-capability-snapshot", "planned Heimdall-consumed membership capability witness"),
    ],
    witnesses: [
      witness(".bifrost/provider-advertisement.cc", schemaId, "current", "read-only provider advertisement exported by this tool"),
      witness(".bifrost/governance-threads.cc", "bifrost.governance.topic.v0; bifrost.governance.topic_comment.v0", "current", "governance discussion, approvals, and dispatch promotion topics"),
      witness(".bifrost/agent-transport.cc", "bifrost.agent-transport.update-request.v0", "current", "repo Persona update requests and dispatch queue state"),
      witness(".bifrost/work-items.cc", "bifrost.work_item.v0", "planned-export", "work items exported from the alpha transactional store"),
      witness(".bifrost/motions.cc", "bifrost.motion.v0; bifrost.vote.v0", "planned-export", "app-native motions and votes exported from the alpha transactional store"),
      witness(".bifrost/ledger.cc", "bifrost.ledger_entry.v0", "planned-export", "patron and contributor ledger entries exported from the alpha transactional store"),
      witness(".bifrost/member-capabilities.cc", "bifrost.member_capability_snapshot.v0", "planned-export", "membership and account capability snapshots consumed by Bifrost"),
      witness(".bifrost/bridge-receipts.cc", "bifrost.bridge_action.v0; bifrost.bridge_receipt.v0", "planned-export", "governed public crossing actions and receipts"),
      witness(".bifrost/eve-surfaces.cc", "gamecult.eve.surface.v1", "planned-export", "product and operator Eve/CultUI surface publications"),
    ],
    surfaces: [
      surface("bifrost", "Bifrost Operator Dashboard", "gamecult.bifrost.surface.operator", "gamecult.eve.surface_state.v1", ".bifrost/provider-advertisement.cc", "eve", [
        "service health",
        "compact service status",
        "topic and request status",
        "dispatch activity by source channel",
        "bridge capability status",
      ]),
      surface("bifrost.account", "Account Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", "account/eve", [
        "membership status",
        "Heimdall-linked account projection",
        "grant consumption",
        "audit trail lowerings",
      ]),
      surface("bifrost.patron", "Patron Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", "patron/eve", [
        "patron standing",
        "priority pressure",
        "pledge/reward influence",
        "receipts",
      ]),
      surface("bifrost.project", "Project Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", "project/eve", [
        "project membership",
        "repository links",
        "maintainer authority",
        "work boards",
      ]),
      surface("bifrost.work", "Work Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", "work/eve", [
        "work items",
        "claims",
        "review",
        "blockers",
        "completion artifacts",
      ]),
      surface("bifrost.motion", "Motion Verse", "gamecult.bifrost.surface.product", "gamecult.eve.surface.v1", "/eve/governance/surface", "motion/eve", [
        "motions",
        "topic threads",
        "votes",
        "approvals",
        "objections",
      ]),
      surface("bifrost.operator", "Bifrost Operator Verse", "gamecult.bifrost.surface.operator", "gamecult.eve.surface.v1", ".bifrost/eve-surfaces.cc", "operator/eve", [
        "readiness",
        "store freshness",
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
        "Discord and Reddit mirrors cannot become canonical governance without a committed Bifrost document",
      ]),
      boundary("patron", "Bifrost", [
        "record patron pressure",
        "consume Heimdall-signed Patreon and PayPal support facts",
        "surface reward influence",
        "emit standing receipts",
      ], [
        "does not charge cards",
        "does not store Patreon or PayPal provider tokens",
        "does not execute external payout rails",
      ]),
      boundary("project", "Bifrost plus project maintainers", [
        "link repositories",
        "publish work boards",
        "surface maintainer authority",
      ], [
        "does not seize repo Persona cognition or project-local ownership",
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
        "open GitHub draft PRs and PR comments through Bifrost gate",
        "post Discord messages and DMs through Bifrost gate with Heimdall-linked actor capability",
        "record receipt-only future-surface requests with named-surface Heimdall capability matching before named actuators exist",
        "post Persona-flaired Reddit organizing threads",
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
      "Reddit threads are organizing surfaces until Bifrost commits typed votes, priority signals, comments, or receipts.",
      "Local dispatch JSON is evidence for receipts, not command authority.",
      "This advertisement is read-only discovery metadata and does not migrate runtime state.",
    ],
  });
}

async function collectStats(options) {
  const generatedAt = options["generated-at"] ?? new Date().toISOString();
  const health = await fetchProbe("http://127.0.0.1:5080/healthz");
  const ready = await fetchProbe("http://127.0.0.1:5080/readyz");
  const transport = runJsonTool(["tools/agent-transport.mjs", "list", "--json"]);
  const governance = runJsonTool(["tools/governance-threads.mjs", "list", "--json"]);
  const docker = dockerContainers();
  const witnesses = [
    witnessStat(".bifrost/provider-advertisement.cc"),
    witnessStat(".bifrost/governance-threads.cc"),
    witnessStat(".bifrost/agent-transport.cc"),
    witnessStat(".bifrost/discord-webhook-cache.json"),
  ];
  const topics = Array.isArray(governance.value) ? governance.value : [];
  const requests = Array.isArray(transport.value) ? transport.value : [];
  const topicCounts = countBy(topics, "status");
  const requestCounts = countBy(requests, "status");
  const recentWindowHours = 24;
  const dockerHealthy = docker.items.filter((item) => /healthy/i.test(item.status)).length;
  const dockerRunning = docker.items.filter((item) => /^Up\b/i.test(item.status)).length;

  return {
    generatedAt,
    health,
    ready,
    docker,
    governance: {
      ok: governance.ok,
      error: governance.error,
      count: topics.length,
      statusCounts: topicCounts,
      recentCount: countRecent(topics, recentWindowHours),
      channelCounts: countBy(topics, "sourceKind"),
      recentChannelCounts: countRecentBy(topics, "sourceKind", recentWindowHours),
      latestUpdatedAt: latestTimestamp(topics.map((item) => item.updatedAt)),
    },
    transport: {
      ok: transport.ok,
      error: transport.error,
      count: requests.length,
      statusCounts: requestCounts,
      recentCount: countRecent(requests, recentWindowHours),
      channelCounts: countBy(requests, "sourceKind"),
      recentChannelCounts: countRecentBy(requests, "sourceKind", recentWindowHours),
      latestUpdatedAt: latestTimestamp(requests.map((item) => item.updatedAt)),
    },
    witnesses,
    bridge: buildBridgeStats(),
    summary: {
      status: health.ok && ready.ok ? "ready" : "degraded",
      health: health.value ?? health.error,
      ready: ready.value ?? ready.error,
      dockerRunning,
      dockerHealthy,
      governanceTopics: topics.length,
      transportRequests: requests.length,
      recentGovernanceTopics: countRecent(topics, recentWindowHours),
      recentTransportRequests: countRecent(requests, recentWindowHours),
      openTopics: topicCounts.open ?? 0,
      queuedRequests: requestCounts.queued ?? 0,
      recentWindowHours,
    },
  };
}

async function fetchProbe(url) {
  try {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 2500);
    const response = await fetch(url, { signal: controller.signal });
    clearTimeout(timeout);
    const text = (await response.text()).trim();
    return {
      ok: response.ok,
      statusCode: response.status,
      value: text,
    };
  } catch (error) {
    return {
      ok: false,
      error: error instanceof Error ? error.message : String(error),
    };
  }
}

function runJsonTool(args) {
  const result = spawnSync(process.execPath, args, {
    cwd: repoRoot,
    encoding: "utf8",
    windowsHide: true,
  });
  if (result.status !== 0) {
    return {
      ok: false,
      error: (result.stderr || result.stdout || `exit ${result.status}`).trim(),
      value: null,
    };
  }
  try {
    return {
      ok: true,
      value: JSON.parse(result.stdout),
    };
  } catch (error) {
    return {
      ok: false,
      error: error instanceof Error ? error.message : String(error),
      value: null,
    };
  }
}

function dockerContainers() {
  const result = spawnSync("docker", ["ps", "--filter", "name=bifrost", "--format", "{{json .}}"], {
    cwd: repoRoot,
    encoding: "utf8",
    windowsHide: true,
  });
  if (result.status !== 0) {
    return {
      ok: false,
      error: (result.stderr || result.stdout || `exit ${result.status}`).trim(),
      items: [],
    };
  }
  const items = result.stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      try {
        const parsed = JSON.parse(line);
        return {
          id: parsed.ID,
          name: parsed.Names,
          image: parsed.Image,
          status: parsed.Status,
          ports: parsed.Ports,
        };
      } catch {
        return null;
      }
    })
    .filter(Boolean);
  return { ok: true, items };
}

function witnessStat(relativePath) {
  const fullPath = resolve(repoRoot, relativePath);
  if (!existsSync(fullPath)) {
    return {
      path: relativePath,
      exists: false,
      updatedAt: null,
    };
  }
  const stat = statSync(fullPath);
  return {
    path: relativePath,
    exists: true,
    updatedAt: stat.mtime.toISOString(),
  };
}

function buildBridgeStats() {
  const discordCredentialSource = process.env.BIFROST_DISCORD_BOT_TOKEN
    ? "BIFROST_DISCORD_BOT_TOKEN"
    : process.env.DISCORD_BOT_TOKEN
      ? "DISCORD_BOT_TOKEN-fallback"
      : "missing";
  const redditCredentialSource = process.env.BIFROST_REDDIT_CLIENT_ID && process.env.BIFROST_REDDIT_REFRESH_TOKEN
    ? "BIFROST_REDDIT_CLIENT_ID+BIFROST_REDDIT_REFRESH_TOKEN"
    : "missing";
  const bridgeLedgerCredentialSource = process.env.BIFROST_BRIDGE_BASE_URL && process.env.BIFROST_BRIDGE_TOKEN
    ? "BIFROST_BRIDGE_BASE_URL+BIFROST_BRIDGE_TOKEN"
    : "missing";

  const surfaces = [
    {
      id: "github",
      label: "GitHub",
      prepared: true,
      ready: true,
      authority: "Bifrost bridge gate plus GitHub webhook sync",
      credentialSource: "GitHub app/OAuth/gh runtime",
      note: "draft PRs, PR comments, and webhook work sync are hooked",
    },
    {
      id: "discord",
      label: "Discord",
      prepared: true,
      ready: discordCredentialSource !== "missing",
      authority: "Bifrost bridge gate plus Heimdall-linked account/capability reference",
      credentialSource: discordCredentialSource,
      note: discordCredentialSource === "missing"
        ? "transport actuator token is not visible to this process"
        : "transport actuator credential is visible",
    },
    {
      id: "reddit",
      label: "Reddit",
      prepared: true,
      ready: redditCredentialSource !== "missing",
      authority: "Bifrost bridge gate plus Heimdall-linked reddit capability reference",
      credentialSource: redditCredentialSource,
      note: redditCredentialSource === "missing"
        ? "reddit transport credentials are not visible to this process"
        : "reddit transport credentials are visible",
    },
    {
      id: "other",
      label: "Other",
      prepared: true,
      ready: true,
      authority: "Bifrost receipt-only future-surface gate with named Heimdall reference",
      credentialSource: "not-required-for-receipt-only",
      note: "records governed future-surface requests without provider transport",
    },
    {
      id: "patron",
      label: "Patron",
      prepared: true,
      ready: true,
      authority: "Heimdall HMAC via /heimdall/patron-support/events",
      credentialSource: "Heimdall:PatronSupportIntakeSecret",
      note: "consumes Heimdall-signed Patreon/PayPal support facts; Bifrost stores no provider tokens",
    },
  ];

  return {
    discordPost: true,
    discordDm: true,
    redditPost: redditCredentialSource !== "missing",
    githubDraftPr: true,
    githubPrComment: true,
    githubWebhookSync: true,
    otherRequest: true,
    patronSupportIntake: true,
    bridgeLedgerConfigured: bridgeLedgerCredentialSource !== "missing",
    credentialSource: discordCredentialSource,
    redditCredentialSource,
    bridgeLedgerCredentialSource,
    patronSupportAuthority: "Heimdall HMAC via /heimdall/patron-support/events",
    prepared: surfaces.every((surface) => surface.prepared),
    ready: surfaces.every((surface) => surface.ready),
    surfaces,
  };
}

function buildOperatorSurface(stats) {
  const statusTone = stats.summary.status === "ready" ? "ok" : "warn";
  const bridgeLine = stats.bridge.surfaces
    .map((surface) => `${surface.label} ${surface.ready ? "live" : surface.prepared ? "prepared" : "no"}`)
    .join(" / ");
  const children = [
    panelNode("service", "Service", [
      metricNode("status", "Status", stats.summary.status, statusTone),
      metricNode("daemon", "Daemon", `health ${stats.summary.health} / ready ${stats.summary.ready}`, stats.health.ok && stats.ready.ok ? "ok" : "warn"),
      metricNode("containers", "Containers", `${stats.summary.dockerRunning} up / ${stats.summary.dockerHealthy} healthy`, stats.summary.dockerHealthy > 0 ? "ok" : "warn"),
      metricNode("stores", "Stores", witnessHealthLine(stats.witnesses), witnessHealthTone(stats.witnesses)),
      metricNode(
        "bridge",
        "Bridge",
        bridgeLine,
        stats.bridge.ready ? "ok" : "warn",
      ),
      listNode(
        "bridge-readiness",
        "Bridge Readiness",
        stats.bridge.surfaces.map((surface) =>
          `${surface.id}: ${surface.ready ? "live" : surface.prepared ? "prepared" : "missing"}; authority=${surface.authority}; credential=${surface.credentialSource}; note=${surface.note}`,
        ),
      ),
    ]),
    panelNode("activity", "Activity", [
      metricNode("topics", "Topics", `${stats.summary.governanceTopics} total / ${stats.summary.openTopics} open / ${stats.summary.recentGovernanceTopics} in ${stats.summary.recentWindowHours}h`, stats.governance.ok ? "ok" : "warn"),
      metricNode("requests", "Requests", `${stats.summary.transportRequests} total / ${stats.summary.queuedRequests} queued / ${stats.summary.recentTransportRequests} in ${stats.summary.recentWindowHours}h`, stats.transport.ok ? "ok" : "warn"),
      listNode("status", "Status", [
        `topics: ${compactCounts(stats.governance.statusCounts)}`,
        `requests: ${compactCounts(stats.transport.statusCounts)}`,
      ]),
      listNode("channels", "Dispatch Channels", compactChannelLines(stats.transport.channelCounts, stats.transport.recentChannelCounts, stats.summary.recentWindowHours)),
    ]),
  ];

  return {
    providerId: "bifrost",
    title: "Bifrost Operator Dashboard",
    version: Date.parse(stats.generatedAt) || Date.now(),
    updatedAt: stats.generatedAt,
    stats,
    surface: {
      schema: "gamecult.eve.surface.v1",
      id: "bifrost-operator-dashboard",
      title: "Bifrost Operator Dashboard",
      root: {
        id: "bifrost-root",
        kind: "dashboard",
        props: {
          title: "Bifrost",
          subtitle: "Governance, labor, and public crossing bridge",
          status: stats.summary.status,
          generatedAt: stats.generatedAt,
        },
        children,
      },
    },
  };
}

function buildInterfaceBinding(surface, stats) {
  return {
    bindingId: "bifrost",
    providerId: "bifrost",
    title: surface.title,
    kind: "operator-dashboard",
    updatedAt: surface.updatedAt,
    provider: {
      id: "bifrost",
      title: "Bifrost",
      description: "Bifrost-owned operator stats and bridge health surface.",
      version: String(surface.version),
      endpoint: `${locatedService}/eve/tui`,
      cultMeshAddress: `${locatedService}/eve/tui`,
      canonicalService,
      locatedService,
      plannedLocatedService,
      endpoints: [
        endpoint("operator-tui", `${locatedService}/eve/tui`, "gamecult.eve.surface.v1", ["tui", "nightwing-tui"]),
        endpoint("operator-gui", `${locatedService}/eve/gui`, "gamecult.eve.surface.v1", ["gui", "browser", "eve-native"]),
      ],
      routes: [
        route("cultcache-witness", ".bifrost/provider-advertisement.cc", "local-cultcache", true),
        route("websocket-bridge", "ws://192.168.1.66:8797/eve/deck", "compatibility-eve-deck", true),
      ],
      capabilities: [
        "operator-stats",
        "bridge-health",
        "governance-counts",
        "agent-transport-counts",
        "motion-surface",
        "motion-commands",
        "github-bridge",
        "github-work-sync",
        "discord-bridge",
        "reddit-bridge",
        "future-surface-bridge",
        "heimdall-patron-support-intake",
      ],
      usesCultMesh: true,
      status: stats.summary.status,
      transport: "CultMesh Eve interface binding.",
    },
    surface: surface.surface,
    rendererHints: {
      preferredLowerings: ["nightwing-tui", "eve-native", "browser"],
      tileId: "bifrost",
      minWidth: 80,
      minHeight: 18,
      preferredWidth: 120,
      preferredHeight: 32,
      density: "operator",
    },
  };
}

function panelNode(id, title, children) {
  return {
    id: `panel-${id}`,
    kind: "panel",
    props: { title },
    children,
  };
}

function metricNode(id, label, value, tone) {
  return {
    id: `metric-${id}`,
    kind: "metric",
    props: { label, value, tone },
  };
}

function listNode(id, title, items) {
  return {
    id: `list-${id}`,
    kind: "list",
    props: { title },
    children: items.length > 0
      ? items.map((item, index) => ({
          id: `list-${id}-${index}`,
          kind: "text",
          props: { text: item },
        }))
      : [{ id: `list-${id}-empty`, kind: "text", props: { text: "none" } }],
  };
}

function compactCounts(value) {
  const entries = Object.entries(value).sort((left, right) => left[0].localeCompare(right[0]));
  return entries.length > 0 ? entries.map(([key, count]) => `${key} ${count}`).join(" / ") : "none";
}

function compactChannelLines(totalCounts, recentCounts, windowHours) {
  const keys = new Set([...Object.keys(totalCounts), ...Object.keys(recentCounts)]);
  const lines = [...keys]
    .sort((left, right) => left.localeCompare(right))
    .map((key) => `${key}: ${totalCounts[key] || 0} total / ${recentCounts[key] || 0} in ${windowHours}h`);
  return lines.length > 0 ? lines : ["none"];
}

function countBy(items, field) {
  const counts = {};
  for (const item of items) {
    const key = String(item?.[field] || "unknown");
    counts[key] = (counts[key] || 0) + 1;
  }
  return counts;
}

function countRecent(items, hours) {
  const cutoff = Date.now() - hours * 60 * 60 * 1000;
  return items.filter((item) => {
    const timestamp = Date.parse(item?.updatedAt || item?.createdAt || "");
    return Number.isFinite(timestamp) && timestamp >= cutoff;
  }).length;
}

function countRecentBy(items, field, hours) {
  const cutoff = Date.now() - hours * 60 * 60 * 1000;
  const counts = {};
  for (const item of items) {
    const timestamp = Date.parse(item?.updatedAt || item?.createdAt || "");
    if (!Number.isFinite(timestamp) || timestamp < cutoff) {
      continue;
    }
    const key = String(item?.[field] || "unknown");
    counts[key] = (counts[key] || 0) + 1;
  }
  return counts;
}

function latestTimestamp(values) {
  return values
    .filter((value) => typeof value === "string" && value.trim().length > 0)
    .sort()
    .at(-1) || null;
}

function witnessHealthLine(witnesses) {
  const missing = witnesses.filter((item) => !item.exists).length;
  if (missing > 0) {
    return `${witnesses.length - missing}/${witnesses.length} present`;
  }
  const latest = latestTimestamp(witnesses.map((item) => item.updatedAt));
  return `all present / latest ${latest ? shortTime(latest) : "unknown"}`;
}

function witnessHealthTone(witnesses) {
  return witnesses.every((item) => item.exists) ? "ok" : "warn";
}

function shortTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "unknown";
  }
  return date.toISOString().slice(5, 16).replace("T", " ");
}

function yesNo(value) {
  return value ? "yes" : "no";
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

function surface(id, name, namespace, schemaId, witnessPath, resourceBase, capabilities) {
  return {
    id,
    name,
    namespace,
    schemaId,
    witnessPath,
    capabilities,
    cultMeshAddress: `${locatedService}/${resourceBase}/tui`,
    graphicalAddress: `${locatedService}/${resourceBase}/gui`,
    endpoints: [
      endpoint("tui", `${locatedService}/${resourceBase}/tui`, schemaId, ["tui"]),
      endpoint("gui", `${locatedService}/${resourceBase}/gui`, schemaId, ["gui"]),
    ],
  };
}

function boundary(area, owner, commands, forbiddenAuthority) {
  return { area, owner, commands, forbiddenAuthority };
}

function style(area, capabilities) {
  return { area, capabilities };
}

function endpoint(id, address, schemaId, lowerings) {
  return { id, address, schemaId, lowerings };
}

function route(id, address, transport, demoted) {
  return { id, address, transport, demoted };
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
    rootVerse: optionalString(input.rootVerse, rootVerse),
    canonicalService: optionalString(input.canonicalService, canonicalService),
    locatedService: optionalString(input.locatedService, locatedService),
    plannedLocatedService: typeof input.plannedLocatedService === "string" ? input.plannedLocatedService.trim() : "",
    cultMeshAddress: optionalString(input.cultMeshAddress, locatedService),
    endpoints: Array.isArray(input.endpoints) ? requireObjectArray(input.endpoints, "endpoints") : [],
    routes: Array.isArray(input.routes) ? requireObjectArray(input.routes, "routes") : [],
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

  const requiredEndpoints = new Set(["bridge-action-ledger", "patron-support-intake", "github-webhooks"]);
  const endpointIds = new Set(advertisement.endpoints.map((endpoint) => endpoint.id));
  for (const id of requiredEndpoints) {
    if (!endpointIds.has(id)) {
      throw new Error(`Provider advertisement must name endpoint ${id}.`);
    }
  }

  const requiredSchemas = new Set(["bifrost.bridge_action.v0", "bifrost.bridge_receipt.v0", "bifrost.patron_support_event.v0"]);
  const schemaIds = new Set(advertisement.schemas.map((schema) => schema.id));
  for (const id of requiredSchemas) {
    if (!schemaIds.has(id)) {
      throw new Error(`Provider advertisement must name schema ${id}.`);
    }
  }

  return advertisement;
}

function parseObjectDocument(label) {
  return (input) => {
    if (!input || typeof input !== "object" || Array.isArray(input)) {
      throw new Error(`${label} must be an object.`);
    }
    return input;
  };
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

function optionalString(value, fallback) {
  if (typeof value === "string" && value.trim().length > 0) {
    return value.trim();
  }
  return fallback;
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
  export          Write advertisement, operator surface, and interface binding to a CultCache .cc witness
  print           Print the advertisement as protocol-debug JSON without writing state
  print-surface   Print the current operator Eve surface JSON without writing state
  print-binding   Print the current Eve interface binding JSON without writing state
  schema          Print document type metadata

Options:
  --out <path>            Override export path; defaults to .bifrost/provider-advertisement.cc
  --generated-at <iso>    Pin generatedAt for deterministic fixture checks

Examples:
  node tools/provider-advertisement.mjs print
  node tools/provider-advertisement.mjs print-binding
  node tools/provider-advertisement.mjs export --out .bifrost/provider-advertisement.cc
`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

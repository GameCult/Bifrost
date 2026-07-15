import { createRequire } from "node:module";
import { randomUUID } from "node:crypto";
import { existsSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const repoRoot = resolve(import.meta.dirname, "..");
const projectsRoot = resolve(repoRoot, "..");
const cultLibRoot = resolve(process.env.VOIDBOT_CULTLIB_ROOT || resolve(projectsRoot, "CultLib"));

export const crossingReceiptDocumentType = "bifrost.crossing_receipt";
export const crossingReceiptSchemaId = "bifrost.crossing_receipt.v1";
export const defaultCrossingReceiptStorePath = resolve(repoRoot, ".bifrost", "bridge-receipts.cc");
export const crossingReceiptStatuses = Object.freeze(["requested", "running", "completed", "failed", "cancelled"]);

const validStatuses = new Set(crossingReceiptStatuses);
let cachedRuntime;
let cachedDefinition;

export function crossingReceiptDefinition(defineDocumentTypeInput) {
  const defineDocumentType = defineDocumentTypeInput ?? loadCultCacheRuntime().defineDocumentType;
  cachedDefinition ??= defineDocumentType({
    type: crossingReceiptDocumentType,
    schemaId: crossingReceiptSchemaId,
    schemaName: crossingReceiptDocumentType,
    schemaVersion: crossingReceiptSchemaId,
    contentHash: crossingReceiptSchemaId,
    global: false,
    name: "receiptId",
    indexes: {
      commandId: "commandId",
      crossingKind: "crossingKind",
      status: "status",
    },
    schema: { parse: parseCrossingReceipt },
    members: [
      { slot: 1, memberName: "receiptId", typeName: "string", isName: true },
      { slot: 2, memberName: "commandId", typeName: "string", indexAlias: "commandId" },
      { slot: 3, memberName: "crossingKind", typeName: "string", indexAlias: "crossingKind" },
      { slot: 4, memberName: "status", typeName: "string", indexAlias: "status" },
      { slot: 5, memberName: "ok", typeName: "boolean" },
      { slot: 6, memberName: "requestedAt", typeName: "string" },
      { slot: 7, memberName: "startedAt", typeName: "string" },
      { slot: 8, memberName: "completedAt", typeName: "string" },
      { slot: 9, memberName: "actor", typeName: "object" },
      { slot: 10, memberName: "source", typeName: "object" },
      { slot: 11, memberName: "authority", typeName: "object" },
      { slot: 12, memberName: "epiphany", typeName: "object" },
      { slot: 13, memberName: "target", typeName: "object" },
      { slot: 14, memberName: "externalReceipt", typeName: "object" },
      { slot: 15, memberName: "error", typeName: "object" },
      { slot: 16, memberName: "supersedes", typeName: "string" },
      { slot: 17, memberName: "relatedReceiptIds", typeName: "string", isMany: true },
      { slot: 18, memberName: "surfaceReceipt", typeName: "object" },
    ],
  });
  return cachedDefinition;
}

export async function openCrossingReceiptStore(storePath = defaultCrossingReceiptStorePath) {
  const { CultCache, SingleFileMessagePackBackingStore } = loadCultCacheRuntime();
  await mkdir(dirname(storePath), { recursive: true });
  const cache = CultCache.builder()
    .withDocumentType(crossingReceiptDefinition())
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  await cache.pullAllBackingStores();
  return {
    storePath,
    definition: crossingReceiptDefinition(),
    cache,
    async put(receipt) {
      const parsed = buildCrossingReceipt(receipt);
      await cache.put(crossingReceiptDefinition(), parsed.receiptId, parsed);
      return parsed;
    },
    get(receiptId) {
      return cache.get(crossingReceiptDefinition(), receiptId);
    },
    getAll() {
      return cache.getAll(crossingReceiptDefinition());
    },
  };
}

export function resolveCrossingReceiptStorePath(options = {}) {
  return resolveOptionPath(
    options["receipt-store"] ??
      process.env.BIFROST_CROSSING_RECEIPT_STORE ??
      process.env.BIFROST_BRIDGE_RECEIPT_STORE ??
      defaultCrossingReceiptStorePath,
  );
}

export async function writeCrossingReceipt(receipt, options = {}) {
  const store = await openCrossingReceiptStore(resolveCrossingReceiptStorePath(options));
  return store.put(receipt);
}

export async function beginCrossingReceipt(options, input) {
  const requestedAt = input.requestedAt ?? new Date().toISOString();
  const commandId = requiredString(input.commandId ?? commandIdFromOptions(options), "commandId");
  const receiptId = input.receiptId ?? `crossing_${commandId}`;
  const base = buildCrossingReceipt({
    schemaName: crossingReceiptDocumentType,
    schemaVersion: crossingReceiptSchemaId,
    receiptId,
    commandId,
    crossingKind: requiredString(input.crossingKind, "crossingKind"),
    status: "requested",
    ok: false,
    requestedAt,
    startedAt: "",
    completedAt: "",
    actor: normalizeObject(input.actor),
    source: normalizeObject(input.source),
    authority: normalizeObject(input.authority),
    epiphany: normalizeObject(input.epiphany),
    target: normalizeObject(input.target),
    externalReceipt: {},
    error: {},
    supersedes: optionalString(input.supersedes) ?? "",
    relatedReceiptIds: normalizeStringArray(input.relatedReceiptIds),
    surfaceReceipt: normalizeObject(input.surfaceReceipt),
  });
  await writeCrossingReceipt(base, options);
  let current = base;

  return {
    receiptId,
    commandId,
    requested: base,
    async start(extra = {}) {
      current = await writeCrossingReceipt({
        ...current,
        ...extra,
        status: "running",
        ok: false,
        startedAt: extra.startedAt ?? new Date().toISOString(),
      }, options);
      return current;
    },
    async complete(extra = {}) {
      current = await writeCrossingReceipt({
        ...current,
        ...extra,
        status: "completed",
        ok: true,
        startedAt: extra.startedAt ?? current.startedAt,
        completedAt: extra.completedAt ?? new Date().toISOString(),
        externalReceipt: normalizeObject(extra.externalReceipt ?? current.externalReceipt),
        surfaceReceipt: normalizeObject(extra.surfaceReceipt ?? current.surfaceReceipt),
      }, options);
      return current;
    },
    async fail(error, extra = {}) {
      current = await writeCrossingReceipt({
        ...current,
        ...extra,
        status: "failed",
        ok: false,
        startedAt: extra.startedAt ?? current.startedAt,
        completedAt: extra.completedAt ?? new Date().toISOString(),
        error: normalizeError(error),
        externalReceipt: normalizeObject(extra.externalReceipt ?? current.externalReceipt),
        surfaceReceipt: normalizeObject(extra.surfaceReceipt ?? current.surfaceReceipt),
      }, options);
      return current;
    },
  };
}

export function buildCrossingReceipt(input) {
  return parseCrossingReceipt({
    schemaName: crossingReceiptDocumentType,
    schemaVersion: crossingReceiptSchemaId,
    ...input,
  });
}

export function crossingProvenanceFromOptions(options = {}) {
  const commandId = commandIdFromOptions(options);
  return {
    commandId,
    actor: {
      bifrostIdentity: optionalString(options.identity) ?? optionalString(process.env.BIFROST_IDENTITY) ?? "",
      kind: optionalString(options["actor-kind"]) ?? "",
      name: optionalString(options["actor-name"]) ?? optionalString(options.identity) ?? optionalString(process.env.BIFROST_IDENTITY) ?? "",
    },
    source: {
      kind: optionalString(options["source-kind"]) ?? optionalString(process.env.BIFROST_BRIDGE_SOURCE_KIND) ?? "cultmesh-command",
      id: optionalString(options["source-id"]) ?? optionalString(process.env.BIFROST_BRIDGE_SOURCE_ID) ?? commandId,
      topicId: optionalString(options["topic-id"]) ?? "",
      requestId: optionalString(options["request-id"]) ?? "",
      workItemId: optionalString(options["work-item-id"]) ?? "",
      motionId: optionalString(options["motion-id"]) ?? "",
      channelId: optionalString(options["source-channel-id"]) ?? "",
      messageId: optionalString(options["source-message-id"]) ?? "",
    },
    authority: {
      authorityRef: optionalString(options["authority-ref"]) ?? optionalString(process.env.BIFROST_BRIDGE_AUTHORITY_REF) ?? `cultmesh-command:${commandId}`,
      heimdallCapabilityRef:
        optionalString(options["heimdall-capability-ref"]) ??
        optionalString(process.env.HEIMDALL_CAPABILITY_REF) ??
        optionalString(process.env.BIFROST_HEIMDALL_CAPABILITY_REF) ??
        "",
      heimdallClaimJti: optionalString(options["heimdall-claim-jti"]) ?? optionalString(process.env.HEIMDALL_CLAIM_JTI) ?? "",
      heimdallAccountId: optionalString(options["heimdall-account-id"]) ?? optionalString(process.env.HEIMDALL_ACCOUNT_ID) ?? "",
      heimdallAccessRevision: optionalString(options["heimdall-access-revision"]) ?? optionalString(process.env.HEIMDALL_ACCESS_REVISION) ?? "",
      heimdallGrantRef: optionalString(options["heimdall-grant-ref"]) ?? optionalString(process.env.HEIMDALL_GRANT_REF) ?? "",
      heimdallClaimExpiresAt: optionalString(options["heimdall-claim-exp"]) ?? optionalString(process.env.HEIMDALL_CLAIM_EXP) ?? "",
      policyDecisionId: optionalString(options["policy-decision-id"]) ?? optionalString(process.env.BIFROST_POLICY_DECISION_ID) ?? "",
    },
    epiphany: {
      runId: optionalString(options["epiphany-run-id"]) ?? optionalString(process.env.EPIPHANY_RUN_ID) ?? "",
      laneId: optionalString(options["epiphany-lane-id"]) ?? optionalString(process.env.EPIPHANY_LANE_ID) ?? "",
      agentIdentity: optionalString(options["epiphany-agent-identity"]) ?? optionalString(process.env.EPIPHANY_AGENT_IDENTITY) ?? "",
    },
  };
}

export function commandIdFromOptions(options = {}) {
  return optionalString(options["cultmesh-command-id"]) ?? optionalString(process.env.BIFROST_CULTMESH_COMMAND_ID) ?? "";
}

function parseCrossingReceipt(input) {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    throw new Error("Bifrost crossing receipt must be an object.");
  }

  const receipt = {
    schemaName: optionalString(input.schemaName) ?? crossingReceiptDocumentType,
    schemaVersion: optionalString(input.schemaVersion) ?? crossingReceiptSchemaId,
    receiptId: requiredString(input.receiptId, "receiptId"),
    commandId: requiredString(input.commandId, "commandId"),
    crossingKind: requiredString(input.crossingKind, "crossingKind"),
    status: requiredString(input.status, "status"),
    ok: Boolean(input.ok),
    requestedAt: requiredString(input.requestedAt, "requestedAt"),
    startedAt: optionalString(input.startedAt) ?? "",
    completedAt: optionalString(input.completedAt) ?? "",
    actor: normalizeObject(input.actor),
    source: normalizeObject(input.source),
    authority: normalizeObject(input.authority),
    epiphany: normalizeObject(input.epiphany),
    target: normalizeObject(input.target),
    externalReceipt: normalizeObject(input.externalReceipt),
    error: normalizeObject(input.error),
    supersedes: optionalString(input.supersedes) ?? "",
    relatedReceiptIds: normalizeStringArray(input.relatedReceiptIds),
    surfaceReceipt: normalizeObject(input.surfaceReceipt),
  };

  if (!validStatuses.has(receipt.status)) {
    throw new Error(`Bifrost crossing receipt status "${receipt.status}" is not valid.`);
  }
  if (!receipt.source.kind && !receipt.source.id) {
    throw new Error("Bifrost crossing receipt requires source provenance.");
  }
  if (!receipt.authority.authorityRef && !receipt.authority.heimdallCapabilityRef && !receipt.authority.heimdallClaimJti) {
    throw new Error("Bifrost crossing receipt requires authority provenance.");
  }
  return receipt;
}

function loadCultCacheRuntime() {
  if (cachedRuntime) {
    return cachedRuntime;
  }
  const entryPoint = resolveFirstExisting("CultCache TypeScript runtime", [
    resolve(cultLibRoot, "packages", "cultcache-ts", "dist", "index.js"),
    resolve(projectsRoot, "CultCacheTS", "dist", "index.js"),
  ]);
  const requireCultCache = createRequire(entryPoint);
  cachedRuntime = requireCultCache(entryPoint);
  return cachedRuntime;
}

function resolveFirstExisting(label, candidates) {
  for (const candidate of candidates) {
    if (existsSync(candidate)) {
      return candidate;
    }
  }
  throw new Error(`${label} is unavailable. Tried: ${candidates.join(", ")}`);
}

function normalizeError(error) {
  if (!error) {
    return {};
  }
  if (error instanceof Error) {
    return {
      code: error.name || "Error",
      message: error.message,
    };
  }
  return {
    code: "Error",
    message: String(error),
  };
}

function normalizeObject(value) {
  return value && typeof value === "object" && !Array.isArray(value) ? value : {};
}

function normalizeStringArray(value) {
  if (value === undefined || value === null) {
    return [];
  }
  if (!Array.isArray(value)) {
    return [];
  }
  return value.map((item) => optionalString(item)).filter(Boolean);
}

function requiredString(value, field) {
  const normalized = optionalString(value);
  if (!normalized) {
    throw new Error(`Bifrost crossing receipt field "${field}" must be a non-empty string.`);
  }
  return normalized;
}

function optionalString(value) {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function resolveOptionPath(path) {
  return resolve(process.cwd(), path);
}

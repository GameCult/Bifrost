import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const repoRoot = resolve(import.meta.dirname, "..");
const projectsRoot = resolve(repoRoot, "..");
const cultLibRoot = resolve(process.env.VOIDBOT_CULTLIB_ROOT || resolve(projectsRoot, "CultLib"));

export const repositoryReleaseAuthorityDocumentType = "bifrost.repository_release_authority";
export const repositoryReleaseAuthoritySchemaId = "bifrost.repository_release_authority.v1";
export const defaultRepositoryReleaseAuthorityStorePath = resolve(repoRoot, ".bifrost", "repository-release-authority.cc");

let cachedRuntime;
let cachedDefinition;

export function repositoryReleaseAuthorityDefinition(defineDocumentTypeInput) {
  const defineDocumentType = defineDocumentTypeInput ?? loadCultCacheRuntime().defineDocumentType;
  cachedDefinition ??= defineDocumentType({
    type: repositoryReleaseAuthorityDocumentType,
    schemaId: repositoryReleaseAuthoritySchemaId,
    schemaName: repositoryReleaseAuthorityDocumentType,
    schemaVersion: repositoryReleaseAuthoritySchemaId,
    contentHash: repositoryReleaseAuthoritySchemaId,
    global: false,
    name: "authorityId",
    indexes: { repositoryFullName: "repositoryFullName", upstreamRef: "upstreamRef", commitSha: "commitSha", status: "status" },
    schema: { parse: parseRepositoryReleaseAuthority },
    members: [
      { slot: 1, memberName: "authorityId", typeName: "string", isName: true },
      { slot: 2, memberName: "commandId", typeName: "string" },
      { slot: 3, memberName: "crossingReceiptId", typeName: "string" },
      { slot: 4, memberName: "repositoryFullName", typeName: "string", indexAlias: "repositoryFullName" },
      { slot: 5, memberName: "upstreamRef", typeName: "string", indexAlias: "upstreamRef" },
      { slot: 6, memberName: "commitSha", typeName: "string", indexAlias: "commitSha" },
      { slot: 7, memberName: "decision", typeName: "string" },
      { slot: 8, memberName: "status", typeName: "string", indexAlias: "status" },
      { slot: 9, memberName: "policyDecisionId", typeName: "string" },
      { slot: 10, memberName: "authorityReference", typeName: "string" },
      { slot: 11, memberName: "actorIdentity", typeName: "string" },
      { slot: 12, memberName: "sourceKind", typeName: "string" },
      { slot: 13, memberName: "sourceId", typeName: "string" },
      { slot: 14, memberName: "epiphanyRunId", typeName: "string" },
      { slot: 15, memberName: "epiphanyLaneId", typeName: "string" },
      { slot: 16, memberName: "epiphanyAgentIdentity", typeName: "string" },
      { slot: 17, memberName: "externalReceiptUrl", typeName: "string" },
      { slot: 18, memberName: "externalReceiptId", typeName: "string" },
      { slot: 19, memberName: "authorizedAt", typeName: "string" },
      { slot: 20, memberName: "expiresAt", typeName: "string" },
      { slot: 21, memberName: "revokedAt", typeName: "string" },
      { slot: 22, memberName: "revocationReason", typeName: "string" },
    ],
  });
  return cachedDefinition;
}

export async function openRepositoryReleaseAuthorityStore(storePath = defaultRepositoryReleaseAuthorityStorePath) {
  const { CultCache, SingleFileMessagePackBackingStore } = loadCultCacheRuntime();
  await mkdir(dirname(storePath), { recursive: true });
  const cache = CultCache.builder().withDocumentType(repositoryReleaseAuthorityDefinition()).withGenericStore(new SingleFileMessagePackBackingStore(storePath)).build();
  await cache.pullAllBackingStores();
  return {
    storePath,
    async put(document) {
      const parsed = parseRepositoryReleaseAuthority(document);
      await cache.put(repositoryReleaseAuthorityDefinition(), parsed.authorityId, parsed);
      return parsed;
    },
    get(authorityId) { return cache.get(repositoryReleaseAuthorityDefinition(), authorityId); },
    getAll() { return cache.getAll(repositoryReleaseAuthorityDefinition()); },
  };
}

export function releaseAuthorityId(repositoryFullName, upstreamRef, commitSha) {
  return `release:${canonicalRepository(repositoryFullName)}:${canonicalRef(upstreamRef)}:${canonicalCommitSha(commitSha)}`;
}

export function buildAuthorizedRelease(input) {
  const repositoryFullName = canonicalRepository(input.repositoryFullName);
  const upstreamRef = canonicalRef(input.upstreamRef);
  const commitSha = canonicalCommitSha(input.commitSha);
  return parseRepositoryReleaseAuthority({
    schemaName: repositoryReleaseAuthorityDocumentType,
    schemaVersion: repositoryReleaseAuthoritySchemaId,
    authorityId: releaseAuthorityId(repositoryFullName, upstreamRef, commitSha),
    commandId: input.commandId,
    crossingReceiptId: input.crossingReceiptId,
    repositoryFullName,
    upstreamRef,
    commitSha,
    decision: "authorize",
    status: "authorized",
    policyDecisionId: input.policyDecisionId,
    authorityReference: input.authorityReference,
    actorIdentity: input.actorIdentity,
    sourceKind: input.sourceKind,
    sourceId: input.sourceId,
    epiphanyRunId: input.epiphanyRunId,
    epiphanyLaneId: input.epiphanyLaneId,
    epiphanyAgentIdentity: input.epiphanyAgentIdentity,
    externalReceiptUrl: input.externalReceiptUrl,
    externalReceiptId: input.externalReceiptId,
    authorizedAt: input.authorizedAt,
    expiresAt: input.expiresAt ?? "",
    revokedAt: "",
    revocationReason: "",
  });
}

export function revokeAuthorizedRelease(current, input) {
  const parsed = parseRepositoryReleaseAuthority(current);
  if (parsed.status !== "authorized") throw new Error(`Release authority ${parsed.authorityId} is already revoked.`);
  return parseRepositoryReleaseAuthority({ ...parsed, status: "revoked", revokedAt: requiredString(input.revokedAt, "revokedAt"), revocationReason: requiredString(input.reason, "revocationReason") });
}

export function canonicalRepository(value) {
  const repository = requiredString(value, "repositoryFullName").replace(/^https?:\/\/github\.com\//i, "").replace(/\.git$/i, "").replace(/^\/+|\/+$/g, "");
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error("repositoryFullName must be a canonical GitHub owner/name.");
  return repository;
}

export function canonicalRef(value) {
  const ref = requiredString(value, "upstreamRef");
  if (!/^refs\/(heads|tags)\/[A-Za-z0-9._\/-]+$/.test(ref) || ref.includes("..") || ref.endsWith("/")) throw new Error("upstreamRef must be a canonical refs/heads/... or refs/tags/... name.");
  return ref;
}

export function canonicalCommitSha(value) {
  const sha = requiredString(value, "commitSha").toLowerCase();
  if (!/^[0-9a-f]{40}$/.test(sha)) throw new Error("commitSha must be an exact 40-character hexadecimal Git commit SHA.");
  return sha;
}

function parseRepositoryReleaseAuthority(input) {
  if (!input || typeof input !== "object" || Array.isArray(input)) throw new Error("Bifrost repository release authority must be an object.");
  const repositoryFullName = canonicalRepository(input.repositoryFullName);
  const upstreamRef = canonicalRef(input.upstreamRef);
  const commitSha = canonicalCommitSha(input.commitSha);
  const document = {
    schemaName: repositoryReleaseAuthorityDocumentType,
    schemaVersion: repositoryReleaseAuthoritySchemaId,
    authorityId: requiredString(input.authorityId, "authorityId"), commandId: requiredString(input.commandId, "commandId"),
    crossingReceiptId: requiredString(input.crossingReceiptId, "crossingReceiptId"), repositoryFullName, upstreamRef, commitSha,
    decision: requiredString(input.decision, "decision"), status: requiredString(input.status, "status"), policyDecisionId: requiredString(input.policyDecisionId, "policyDecisionId"),
    authorityReference: requiredString(input.authorityReference, "authorityReference"), actorIdentity: requiredString(input.actorIdentity, "actorIdentity"),
    sourceKind: requiredString(input.sourceKind, "sourceKind"), sourceId: requiredString(input.sourceId, "sourceId"),
    epiphanyRunId: requiredString(input.epiphanyRunId, "epiphanyRunId"), epiphanyLaneId: requiredString(input.epiphanyLaneId, "epiphanyLaneId"), epiphanyAgentIdentity: requiredString(input.epiphanyAgentIdentity, "epiphanyAgentIdentity"),
    externalReceiptUrl: requiredString(input.externalReceiptUrl, "externalReceiptUrl"), externalReceiptId: requiredString(input.externalReceiptId, "externalReceiptId"),
    authorizedAt: requiredString(input.authorizedAt, "authorizedAt"), expiresAt: optionalString(input.expiresAt) ?? "", revokedAt: optionalString(input.revokedAt) ?? "", revocationReason: optionalString(input.revocationReason) ?? "",
  };
  if (document.authorityId !== releaseAuthorityId(repositoryFullName, upstreamRef, commitSha)) throw new Error("authorityId must be derived from repositoryFullName, upstreamRef, and commitSha.");
  if (document.decision !== "authorize") throw new Error("Repository release authority decision must be authorize.");
  if (!new Set(["authorized", "revoked"]).has(document.status)) throw new Error(`Repository release authority status "${document.status}" is not valid.`);
  if (document.status === "authorized" && (document.revokedAt || document.revocationReason)) throw new Error("Authorized release authority cannot carry revocation state.");
  if (document.status === "revoked" && (!document.revokedAt || !document.revocationReason)) throw new Error("Revoked release authority requires revocation time and reason.");
  return document;
}

function loadCultCacheRuntime() {
  if (cachedRuntime) return cachedRuntime;
  const candidates = [resolve(cultLibRoot, "packages", "cultcache-ts", "dist", "index.js"), resolve(projectsRoot, "CultCacheTS", "dist", "index.js")];
  const entryPoint = candidates.find(existsSync);
  if (!entryPoint) throw new Error(`CultCache TypeScript runtime is unavailable. Tried: ${candidates.join(", ")}`);
  cachedRuntime = createRequire(entryPoint)(entryPoint);
  return cachedRuntime;
}

function requiredString(value, field) { const result = optionalString(value); if (!result) throw new Error(`Bifrost repository release authority field "${field}" must be a non-empty string.`); return result; }
function optionalString(value) { if (typeof value !== "string") return undefined; const trimmed = value.trim(); return trimmed || undefined; }

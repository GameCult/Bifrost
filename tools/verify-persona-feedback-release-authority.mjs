import { openRepositoryReleaseAuthorityStore } from "./bifrost-repository-release-authority.mjs";

const storePath = process.argv[2];
const authorityId = requiredEnvironment("EXPECTED_AUTHORITY_ID");
const commitSha = requiredEnvironment("EXPECTED_COMMIT");
if (!storePath) throw new Error("Pass the repository release-authority CultCache path.");

const store = await openRepositoryReleaseAuthorityStore(storePath);
const authority = store.get(authorityId);
if (!authority) throw new Error(`Bifrost release authority ${authorityId} is absent.`);
if (authority.repositoryFullName !== "GameCult/Bifrost") throw new Error("Release authority does not own GameCult/Bifrost.");
if (authority.upstreamRef !== "refs/heads/main") throw new Error("Release authority does not own refs/heads/main.");
if (authority.commitSha !== commitSha) throw new Error("Release authority does not bind the frozen Bifrost commit.");
if (authority.status !== "authorized" || authority.decision !== "authorize") throw new Error("Release authority is not authorized.");
if (authority.expiresAt && Date.parse(authority.expiresAt) <= Date.now()) throw new Error("Release authority has expired.");

process.stdout.write(`${authority.authorityId}\n`);

function requiredEnvironment(name) {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Missing ${name}.`);
  return value;
}

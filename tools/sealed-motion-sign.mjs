// Signing helper for a Mind's sealed-motion verbs. Reads the Mind's Ed25519 key file (32-byte seed, mode 0400/0600, owned by
// the running user: loadSignedIdentity checks all three), prints a signed envelope on stdout, and posts nothing.
// The output of `commit` also holds the ballot (candidate, reason, salt): keep it in the Mind's private state, never in a room.
//   pubkey  --key <file>
//   commit  --key <file> --persona <id> --motion <uuid> --voter <member account uuid> --candidate <name> --reason <one line> [--round 1] [--salt <hex>]
//   reveal  --key <file> --persona <id> --motion <uuid> --ballot '<json from commit>'
// Post an envelope with: curl -H 'Content-Type: application/json' -d @envelope.json https://<bifrost>/api/sealed-motions/<motion>/mind
import { createHash, randomBytes, sign } from "node:crypto";
import { pathToFileURL } from "node:url";
import { loadSignedIdentity } from "./bifrost-signed-identity.mjs";

const DOMAIN = "gamecult.sealed_intake.v0";
const BALLOT_SCHEMA = "gamecult.sealed_ballot.v0";

export function commitmentOf({ motion, voter, persona, round, candidate, reason, salt }) {
  return createHash("sha256").update(`${BALLOT_SCHEMA}\n${motion}\n${voter}\n${persona}\n${round}\n${candidate}\n${reason}\n${salt}`, "utf8").digest("hex");
}

export function signEnvelope(identity, e) {
  const text = [DOMAIN, e.persona, e.verb, e.motionId, e.nonce, e.issuedAt, e.commitment ?? "", e.round ?? 0, e.candidate ?? "", e.reason ?? "", e.salt ?? ""].join("\n");
  return { ...e, signature: sign(null, Buffer.from(text, "utf8"), identity.key).toString("base64") };
}

function args(argv) {
  const out = {};
  for (let i = 1; i < argv.length; i += 2) out[argv[i].replace(/^--/, "")] = argv[i + 1];
  return out;
}

async function main(argv) {
  const [cmd] = argv, a = args(argv);
  const identity = await loadSignedIdentity(a.key, "Mind sealed-motion key");
  if (cmd === "pubkey") return console.log(Buffer.from(identity.publicKey).toString("base64"));
  const nonce = randomBytes(16).toString("base64url"), issuedAt = Math.floor(Date.now() / 1000);
  if (cmd === "commit") {
    const ballot = { round: Number(a.round ?? 1), candidate: a.candidate, reason: a.reason, salt: a.salt ?? randomBytes(16).toString("hex") };
    const commitment = commitmentOf({ motion: a.motion, voter: a.voter, persona: a.persona, ...ballot });
    const envelope = signEnvelope(identity, { persona: a.persona, verb: "commit", motionId: a.motion, nonce, issuedAt, commitment, round: ballot.round });
    return console.log(JSON.stringify({ envelope, ballot }));
  }
  if (cmd === "reveal") {
    const b = JSON.parse(a.ballot).ballot ?? JSON.parse(a.ballot);
    return console.log(JSON.stringify(signEnvelope(identity, { persona: a.persona, verb: "reveal", motionId: a.motion, nonce, issuedAt, round: b.round, candidate: b.candidate, reason: b.reason, salt: b.salt })));
  }
  throw new Error("usage: pubkey | commit | reveal (see the header of this file)");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main(process.argv.slice(2)).catch(e => { console.error(e.message); process.exit(1); });

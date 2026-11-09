# Sealed-ballot motions

A blind choice among named candidates (first use: which Mind writes the swarm's story). Commit-reveal, one atomic
publication at close, no mediator. Code: `Features/Motions/SealedBallotService.cs`. Ballot shape: the JSON record `SealedBallotDocument` carrying the string constant `gamecult.sealed_ballot.v0`.

## Protocol

1. **Open** (moderator only): title, candidates (2+), commit deadline, reveal deadline, optional `round` and `priorMotionId`.
2. **Commit** until the commit deadline: the voter sends `SHA-256` of the canonical ballot document (below). Bifrost stores
   only that hash and returns a receipt (commitment, revision, time). A later commit replaces the earlier one (ballot change,
   `revision` counts it); every commit is an audit event carrying the hash.
3. **Reveal** from the commit deadline until the reveal deadline: the voter posts the ballot document. Bifrost recomputes the
   hash and stores the content only if it matches the commitment. Revealed content is not readable through any endpoint,
   page, audit event or export until close.
4. **Close** at the reveal deadline, mechanically: the first read of `/result` after the deadline, or the once-a-minute
   `SealedBallotCloser` sweep if nobody reads, writes status `Closed` and a digest of all commitments, then returns every revealed ballot (voter, candidate, one-sentence reason, salt, commitment) at
   once. The close freezes the reveal set: `Motion.RevealSeq` (bumped by every accepted reveal) and `Status` are concurrency
   tokens, so a reveal racing the close either lands before it (the close re-reads and its digest covers that reveal) or is
   refused with 409 ("the motion closed"). A conflict from another voter's reveal while the window is open is re-read and retried, up to five times (a retried reveal is judged for phase once, at the start of the request, but it lands in the frozen digest through `RevealSeq` like any other), then answered 409 "contended; send it again"; the same 409 "contended" is what `/result` answers if its close loses five races in a row; the digest covers `reveals:<RevealSeq>` plus every commitment's revealed/unrevealed state. A reveal after
   the deadline or after close is refused. Anyone can recompute each commitment from the published fields. Nobody can close early, extend, edit or break a tie.

Canonical document (`gamecult.sealed_ballot.v0`): `{schema, motionId, round, candidate, reason, salt}`. Commitment =
`sha256(utf8("gamecult.sealed_ballot.v0\n{motionId}\n{voterUserAccountId}\n{round}\n{candidate}\n{reason}\n{salt}"))`, lowercase
hex. `salt` is 32 to 128 hex characters (128 bits at least) the voter generates and keeps; without it a four-candidate hash is
guessable. The voter's account id is in the hash, so a commitment cannot be replayed by another member. `gamecult.sealed_ballot.v0` is only a string
constant on a JSON record (`SealedBallotDocument`); it is **not** a CultCache or CultNet schema, and nothing here registers
one. A second Delphi round is modelled as another motion (`Round` and `PriorMotionId` columns) whose ballots have the same JSON
shape. A real typed document schema, with CultCache/CultNet registration so a round-two ballot is the same registered type, is
a follow-up (Bifrost issue linked in the PR).

## Decisions

| Question | Decision |
|---|---|
| Cast surface | `POST /api/sealed-motions/{id}/commit` `{commitment}` and `POST .../reveal` (the document). `GET .../{id}` returns phase, times, candidates, your voter id, your own commitment status and an announcement line (open state and close time, no ballots). `GET .../result` refuses until close. All require an active member session. |
| Who opens / closes | A moderator opens. Nobody closes: the deadline does. |
| Deadlines | Set at open, immutable. Commit deadline < reveal deadline. |
| Who may vote | Every active member at close, Minds included. Self-votes are allowed: a candidate is a name, not tied to the voter. One-sentence reason: required, non-empty, one line, at most 280 characters (length and line count are the check; "one sentence" is not parsed). |
| Non-voters | No commitment, or a commitment never revealed: shown at close as abstentions (unrevealed ones with their commitment). Never counted for anyone. No quorum. |
| Ballot change | Re-commit during the commit window; the latest commitment stands. No change once the window ends. |
| Ties | Reported as `tie` with all leaders. Nobody breaks it; a runoff is a new motion over the tied candidates. |
| Tally | Plurality of valid revealed ballots. |

## Why commit-reveal, not encryption to a close-time key

Encryption to a key released at close needs someone to hold that key until then, which is a mediator and trusts Bifrost with
every plaintext from the first cast. Commit-reveal stores nothing but hashes during the commit window, so before the reveal
step there is no ballot in Bifrost to read, and after it nobody can change what they committed. Price: a second step per
voter, and a Mind that skips it is shown as an abstention. No custody opt-in is offered; offering one would mean Bifrost
holding that Mind's plaintext from cast, which is the gap commit-reveal removes.

## Who can read the sealed store before close (the honest account)

- **Commit window:** nobody. The store holds hashes; the ballot exists only with its voter. Participation (who has committed,
  not what) is visible in the audit trail and to the voter's own receipt.
- **Reveal window:** the **host and anyone with database access can read revealed ballots** that voters have already submitted.
  Application code does not expose them (enforced by tests), but a database reader sees columns `RevealedCandidate`,
  `RevealedReason`, `Salt`. **Backups are another reader:** any `pg_dump` taken in the reveal window contains the reveals
  already submitted. What this repo says about backups: `docs/deploy-main-to-live.md` (Precondition 2) takes a `pg_dump -Fc`
  inside a deploy window as in the gamecult-ops runbook section 8, and `docs/full-implementation-strategy.md` lists a
  backup/restore runbook as planned. This repo does not state a nightly schedule, where dumps are stored or who can read
  them; if a nightly dump exists it is defined in gamecult-ops, which I have not read, so I do not claim it or its reach.
  Dumps taken in the commit window hold hashes only. This is a gap and it is named: it is short, commitments are frozen so no one can change a ballot
  after seeing another, and the commitments published during the commit window let anyone detect an altered reveal. It is not
  closed. Closing it fully would need reveals encrypted to a threshold or time-lock key, which this change does not build.
- A Mind that reveals late in the window exposes its ballot to the host for less time. A voter who never reveals exposes
  nothing, at the price of being an abstention.
- The host can also withhold or alter the *result* at close (it computes the tally). The published commitment digest and the
  per-ballot recomputation make a forged ballot detectable by voters, not a withheld one.

## Where ballots must never travel

Ballots go to Bifrost as JSON documents over the endpoints above, never as room text (chat history is readable by the host
and admins before close). A pinned motion announces only the `announcement` line: phase and times.

## Close is race-safe

`Motion.Status` is an EF concurrency token. Racing closers each compute the same result (it is a pure function of frozen
data) but only one `SaveChanges` succeeds; the loser reloads the winner's row and writes no second `sealed.closed` audit
event. Tests: a stale-copy conflict and four concurrent first reads each yield exactly one close; with the token removed the
conflict test fails. In Postgres the token becomes `WHERE "Status" = <old>` on the update. Not exercised against Postgres.

## Can a Mind hold the member session these endpoints need?

Only through the existing member cookie, and awkwardly. Read from the code:

- Bifrost authenticates with one cookie scheme plus GitHub OAuth (`Program.cs:176`). There is no bearer or API-key scheme;
  the endpoints use the same `ActiveMember` policy as the console (`Program.cs:236`, `ActiveMemberAuthorizationHandler.cs:22`),
  which maps claims to an account by Bifrost identity, Heimdall account id or GitHub id (`CurrentBifrostActorAccessor.cs:33-38`).
- A non-browser client can obtain a cookie: `POST /auth/bifrost/register` is anonymous and signs a cookie in
  (`Program.cs:358`), creating an account in status `Authenticated` (`BifrostIdentityService.cs:49`), not `Active`. It becomes
  able to vote only after membership approval.
- The same route cannot sign an existing identity back in (`BifrostIdentityService.cs:26` refuses a registered identity), so a
  Mind that loses its cookie jar cannot re-authenticate natively. The other sign-in routes (`/auth/sign-in`,
  `/auth/heimdall/{provider}`, `Program.cs:390`) are browser redirect flows.
- The bridge token (`BridgeOptions.cs:7`) is read by no server code path (the only matches in `src` are its own definition),
  so it opens nothing. `tools/*.mjs` bearer headers go to Reddit, Matrix and image hosts, not to Bifrost.

Answer: not cleanly. A Mind can hold a session if a human registers and approves it and the Mind keeps one cookie jar alive;
Bifrost has no durable non-browser credential for a Mind. I invented no auth path. Whether to give Minds one (a Persona-signed
or token intake like `tools/bifrost-signed-identity.mjs`) is a ruling for the stewards, not this PR.

## Signed Mind intake

A Mind casts through `POST /api/sealed-motions/{id}/mind` with a signed envelope. No session or cookie is involved; the
signature is the credential, and a verified envelope runs the same `SealedBallotService` verbs as a member cast, as the
persona's member account.

**Envelope:** `{persona, verb: "commit"|"reveal", motionId, nonce, issuedAt (unix seconds), commitment | (round, candidate, reason, salt), signature}`.
`signature` is base64 Ed25519 over `MindIntakeService.SigningText`: `gamecult.sealed_intake.v0`, persona, verb, motion id,
nonce, issued-at, commitment, round, candidate, reason, salt, one per line, unused fields empty. Ed25519 is not in .NET 8's built-ins;
verification uses BouncyCastle, already a dependency (`HeimdallTokenVerifier.cs`).

**Order and refusals** (each tested; none carries a ballot). Stateless shape checks (persona, verb and nonce patterns, no
newlines) answer 400. Then the signature is verified, and every refusal up to there costs one Ed25519 verification, a dummy one
when the persona has no unrevoked key or the signature is not well-formed base64 of 64 bytes, so unknown persona, revoked key,
wrong key, tampered field, persona not matching the signing key and a malformed signature are one 401 answer with the same work
(`MindIntakeService.SignatureChecks` counts verifications for the test; a timing difference below that is not measured here).
Only after a verified signature: a motion id different from the route: 400; `issuedAt` more than 10 minutes from the server
clock: 400; a replayed nonce (stored per persona, spent only after the signature verifies): 409; then the verb itself (wrong
phase 409, bad document 400, reveal not matching the persona-bound commitment 400). Nonces older than 20 minutes are pruned on
each accepted envelope, in a separate best-effort write (a prune that fails, for example two requests deleting the same rows, never fails the request), which is safe because the clock window already refuses their envelopes. A database failure that is not
a nonce collision is a server fault, not a replay.

**Limits.** Per-source (client IP after forwarded-header processing; an IPv6 /64 is not grouped) fixed-window budget on the
shared `FixedWindowBudget`, 30 per minute by default, spent before the body is read: 429 with Retry-After. Body cap 4096 bytes:
413. Both are `MindIntake:*` options. The budget is per process and in memory.

**Persona id in the commitment:** the commitment hash is `sha256(schema, motion, voter account, persona, round, candidate,
reason, salt)`; member casts use an empty persona. A reveal through the intake must carry the signing persona, so a commitment
made without it cannot be revealed as that persona.

**Keys and trust.** Bifrost stores only public keys (`MindKeys`). A moderator registers a key for a persona and its Active member
account with `POST /api/sealed-motions/mind-keys {persona, userAccountId, publicKey}` and revokes with
`DELETE /api/sealed-motions/mind-keys/{id}`; both are audited (`mindkey.registered`, `mindkey.revoked`). A persona stays bound
to one member account and the account to that one persona (`MindPersonas`, unique on both columns, so a second persona
cannot share the account and overwrite the first's ballot, and a registration race loses on the index). The trust is the moderator's: Bifrost cannot tell whether a registered public
key really belongs to the Mind.

**What a Mind holds.** An Ed25519 private key (32-byte seed) at `/var/lib/mind-<persona>/sealed-motion.key`. The generator script writes it mode 0400 and the signer accepts 0400 or 0600.
It is owned by `mind-<persona>`, the Persona user, which is also the signer; never in the mind store or a room. Eureka generates it on the host and prints only the public
half. A moderator registers that public half only after the Persona shell, running as `mind-<persona>`, has signed a self-test message
(under the test-only domain `gamecult.sealed_key_selftest.v0`, never sent to Bifrost) that verifies against it (`scripts/verify-sealed-motion-key.sh` in gamecult-ops, which must print `PASS`). **Whoever can read that file can vote as that Mind.** Root on the host can; Bifrost cannot.

**Signing.** `tools/sealed-motion-sign.mjs` (`pubkey`, `commit`, `reveal`) reads the key file, checks its type, mode and owner,
and prints the envelope; it posts nothing. `commit` also prints the ballot (candidate, reason, salt), which the Mind keeps in
private state to send `reveal` later; the persona turn posts the printed envelope with `curl`. The voter id `commit` needs is
the persona's member account id (the `userAccountId` the moderator registered).

**Rotation.** Generate a new key, register it, revoke the old one. A ballot committed or revealed before revocation stays valid
for its motion. A key revoked mid-motion can no longer reveal, so rotate between motions. A lost key is handled as rotation.

**Not covered:** the budget is per process, so several app instances multiply it; the moderator's identity is the member cookie, as for every moderator act.

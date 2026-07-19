# Epiphany operator boundary

## Authority map

- Owner: Bifrost alone authenticates a Discord operator request, resolves the
  persisted Discord-to-Bifrost actor link, and signs an expiring Epiphany
  admission. Epiphany alone decides and applies the named command.
- Inputs: one strict `voidbot.discord.epiphany_operator_request.v1`, the exact
  configured Discord owner, a persisted Bifrost actor link, the deployment's
  operator signing identity, and the configured Epiphany runtime/host trust
  anchor.
- Outputs: one immutable `bifrost.operator_command.delivery.v1`, one verified
  `epiphany.operator_command.sealed_result.v1`, and one bounded
  `bifrost.discord.epiphany_operator_delivery.v1` projection.
- Derived state: Discord delivery fields are notification-only projections of
  the signed result. Reviews contain at most ten candidate identity/status
  summaries. Proposal bodies and private Mind state never cross this boundary.
- Forbidden writers: ordinary Discord conversation, Persona feedback, argv,
  shell commands, `/queue-codex`, provider-advertisement metadata, and the
  Discord outbox cannot create an Epiphany consequence.
- Shared path: Status, Sleep, Wake, Directive, Reviews, and Review all use the
  same request admission, signature, RUDP execution, sealed-result verification,
  durable ledger, and Discord projection path.
- Cut line: v0 admissions and sealed results exist only so already-durable
  four-command work can drain or replay. Bifrost never mints v0, and v0 cannot
  carry Reviews or Review.

## Review command

Review binds the exact current Mind candidate with `mindRequestId`,
`candidateId`, bare 64-hex `candidateSha256`, `expectedModelRevision`, bare
64-hex `expectedModelHash`, and one decision: `adopt`, `refuse`, or `hold`.
Bifrost does not infer a decision from conversation and cannot substitute a
candidate after signing.

## Deployment identity

Provider discovery reads `BIFROST_ROOT_VERSE` and `BIFROST_MACHINE_ID` from the
deployment environment. The canonical Yggdrasil example config supplies
`asgard` and `yggdrasil`; the provider code has no Starfire or machine fallback.

## Verification layer

The Rust-generated fixture owns cross-runtime schema versions, signing
purposes, command vocabulary, bounded review projection, admission bytes, and
sealed-result bytes. Bifrost tests consume that fixture, then run hostile
schema, identity, digest, binding, replay, and tamper cases against the same
ports used by the worker.

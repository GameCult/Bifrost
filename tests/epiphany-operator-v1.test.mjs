import test from "node:test";
import assert from "node:assert/strict";

import {
  commandTupleForInterop,
  parseOperatorAdmission,
  parseOperatorRequest,
  parseOperatorResult,
  wireResultTuple,
} from "../tools/epiphany-operator-command-documents.mjs";

const hex = "a".repeat(64);
const review = {
  kind: "review",
  mindRequestId: "mind-1",
  candidateId: "candidate-1",
  candidateSha256: hex,
  expectedModelRevision: 41,
  expectedModelHash: "b".repeat(64),
  decision: "adopt",
};

function request(command) {
  return {
    schemaName: "voidbot.discord.epiphany_operator_request",
    schemaVersion: "voidbot.discord.epiphany_operator_request.v0",
    requestId: "request-1",
    commandId: "command-1",
    nonce: "nonce-1",
    sourceEventId: "event-1",
    sourceActorDiscordId: "owner",
    discordGuildId: "guild",
    discordChannelId: "ops",
    discordMessageId: "message-1",
    targetRuntimeId: "epiphany-yggdrasil",
    issuedAt: "2026-07-19T12:00:00Z",
    expiresAt: "2026-07-19T12:01:00Z",
    producerId: "voidbot",
    producerRuntimeId: "voidbot-yggdrasil",
    authorityClass: "operator_request_only",
    status: "pending",
    command,
  };
}

test("six-command v1 accepts bounded Reviews and exact Review identity only", () => {
  assert.equal(parseOperatorRequest(request({ kind: "reviews" })).command.kind, "reviews");
  assert.deepEqual(commandTupleForInterop(parseOperatorRequest(request(review)).command), [
    "review",
    ["mind-1", "candidate-1", hex, 41, "b".repeat(64), "adopt"],
  ]);
  for (const hostile of [
    { ...review, candidateSha256: `sha256-${hex}` },
    { ...review, decision: "approve" },
    { ...review, expectedModelRevision: -1 },
    { ...review, proposalText: "private" },
  ]) assert.throws(() => parseOperatorRequest(request(hostile)));
});

test("v0 admission is drain-only and cannot acquire review authority", () => {
  const packet = {
    commandId: "command-1", nonce: "nonce-1", sourceEventId: "event-1",
    sourceActorId: "actor", discordGuildId: "guild", discordChannelId: "ops",
    discordMessageId: "message-1", targetRuntimeId: "epiphany-yggdrasil",
    issuedAt: "2026-07-19T12:00:00Z", expiresAt: "2026-07-19T12:01:00Z",
    command: { kind: "reviews" },
  };
  const admission = {
    schemaName: "bifrost.operator_command.delivery",
    schemaVersion: "bifrost.operator_command.delivery.v0",
    admissionId: "request-1", packet, packetSha256: `sha256-${hex}`,
    sourceObserverId: "voidbot", sourceObserverRuntimeId: "voidbot-yggdrasil",
    provider: "bifrost", bifrostAdmissionReceiptId: "request-1",
    authority: "exact_operator_command_only", providerIdentityId: "bifrost",
    providerSignature: new Uint8Array(64),
  };
  assert.throws(() => parseOperatorAdmission(admission, { allowLegacy: true }), /cannot carry Mind review/);
  assert.throws(() => parseOperatorAdmission(admission), /current admission schema/);
});

test("v1 result exposes only bounded review identities and status", () => {
  const result = {
    schemaVersion: "epiphany.operator_command.result.v1", resultId: "result-1",
    commandId: "command-1", packetSha256: `sha256-${hex}`,
    targetRuntimeId: "epiphany-yggdrasil", disposition: "observed",
    consequenceKind: "mind-review-candidates", consequenceRef: "",
    completedAt: "2026-07-19T12:00:01Z", privateStateExposed: false,
    operatorStatus: "", stateStatus: "", coordinatorAction: "", brakeStatus: "",
    detail: "1 pending Mind review candidate(s)",
    reviews: [{ mindRequestId: "mind-1", candidateId: "candidate-1",
      candidateSha256: hex, modelRevision: 41,
      modelHash: "b".repeat(64), frontierItemId: "frontier-1",
      requestedAt: "2026-07-19T11:59:00Z" }],
    reviewCandidateId: "", reviewDecision: "",
  };
  assert.equal(wireResultTuple(parseOperatorResult(result)).length, 18);
  assert.throws(() => parseOperatorResult({ ...result, reviews: [...result.reviews, ...Array(10).fill(result.reviews[0])] }), /bounded/);
  assert.throws(() => parseOperatorResult({ ...result, proposalText: "private" }));
});

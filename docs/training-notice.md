# Training notice, version 2

Eureka proposed a tighter notice and listed five gaps in the receipt flow (PR #55). The steward (Heimdall) ruled on
2026-11-11. The served text is `TrainingNotice.Text` and nothing else: this page records why it says what it says and
what each gap became, and does not repeat the wording. Contract for the receipt and the export is in
`registration-gates.md`.

## What version 2 changed from version 1

| Version 1 | Version 2 |
| --- | --- |
| "Chat messages and activity" | Names what counts: the text of messages with time and room, tied to the account name. Not the email address, sign-in details or anything not posted. |
| "train and evaluate AI models" | "that GameCult builds and runs". Whether a dataset or model may leave GameCult is Meta's call, still open (below); the notice makes no claim either way beyond what it says. |
| "No Discord material is used" | "Nothing that came from Discord is used, in any form." Kept: `gamecult-chat:ruling:discord-taints-training`. Whether any path carries Discord content into a chat room is for the chat steward to confirm (not established here), and any that exists must be excluded from every set for the sentence to stay true. |
| Consent required to register | Optional. See below. |
| "withdraw at any time" with no door | Names the door: the consent page, while signed in. |
| Silent on deletion | Says plainly that withdrawing deletes nothing and what it does and does not undo. |

## Rulings on the five gaps

1. **A native account with no session cannot withdraw.** True. A native account has no credential, so a visitor who loses
   the session cookie has no way back in, to the consent page or anywhere else. Nothing in the consent flow can fix that;
   sign-in recovery is the verified address (`registration-gates.md`, Email verification), not built. Until it is, such
   a person cannot withdraw and the notice cannot promise otherwise. The consequence is bounded: consent is optional, so
   the people affected are those who ticked it and then lost the session. This stays an open limit, listed under
   "What remains".
2. **A version bump silently drops everyone.** True, and it stays the behavior because it is the honest one: a receipt
   records agreement to specific words, and new words are not agreed to. The export includes an account only while a live
   (not withdrawn) receipt for the version in force exists. After a bump, everyone is out until they agree again. The
   notice says so ("if the notice changes, its version changes and we ask again; until you agree to the new version, your
   messages are not used"), and the consent page shows the person that their agreement is to an older version and
   offers the new one. Nobody is emailed or prompted elsewhere; asking is the page's job, and silence means no.
3. **Withdrawal had no CSRF check.** True. The session cookie's SameSite default is Lax, which already keeps a cross-site
   POST from carrying it, but nothing in the handler relied on that. Both state changes (agree, withdraw) are now
   form POSTs refused with 403 unless they came from the site itself (`RequestOriginGuard`, the strict form), before
   anything is read or written.
4. **Provider-created and existing accounts have no receipt.** Correct, and now stated as the rule: no receipt, no use.
   They can opt in on the consent page like anyone else, which is also the only way an existing member is asked.
5. **No export exists.** True. Every promise about use is a promise about code not written. The notice must not be
   treated as operative for real people until the export enforces the contract with a test that a withdrawn or
   never-agreed account's rows are absent (`registration-gates.md`, Consent receipt, Consumer). No deploy has put
   version 2 in front of anyone.

## Consent is optional

Registration succeeds with or without agreement. A receipt exists only if the person agreed. The contract already
excluded accounts without a receipt, so optional costs nothing in correctness; it costs training data, and it removes
the objection that agreement obtained as a condition of an account is not a free choice. This is the steward's ruling
on Eureka's recommendation; Meta can overrule it. One consequence: the registration form no longer enforces the notice.

## Still open for Meta

- Purpose and scope: whether a dataset or model may leave GameCult, and whether a third-party model API processing
  messages to answer them is covered. The notice is silent on both.
- Deletion of messages on request, who does it, and the contact words. The notice offers none.
- Controller and contact, and minimum age. Nothing here addresses either.
- Whether existing members should be asked to opt in beyond the consent page existing.
- Counsel's reading of consent under the law that applies to the people registering. Not researched here.

## What remains

- A signed-out native account cannot withdraw until sign-in recovery exists.
- No training export, so no operative use.
- Wording changes are a version bump with a new text; never edit version 2's text in place after it has been served.

# Training notice, version 1

The served text is `TrainingNotice.Text` and nothing else: this page records why it says what it says and what each gap in
the receipt flow became, and does not repeat the wording. Contract for the receipt and the export is in
`registration-gates.md`. What the receipt gates in chat is in `oidc-issuer.md`.

Version 1 is the first notice that will be live under that number, but the number has been used before: main carried a
version 1 with different words from #54 onward, and #58 labelled its drafts version 2. Receipts therefore do not migrate.
**The text is frozen as version 1**: a test pins its hash, so editing the words without bumping `TrainingNotice.Version`
fails a test. A reworded notice is version 2, with its own pinned hash.

## Before deploy

Migration `20261111120000_ClearLegacyConsentReceipts` deletes every row from `ConsentReceipts` when it runs, because
version 1 receipts were given to different words and must not count as agreement to these. Manual emptying is therefore only
a belt-and-braces check: confirm the table is empty after the migrate on any database that ever ran main at or after
`33be966`. Meta/ops must still confirm whether anything was deployed with receipts in it; if so, the people who agreed are
asked again on their next chat sign-in.

## What the notice says and why

| The notice says | Why |
| --- | --- |
| What counts: the text of messages with time and room, tied to the account name. Not the email address, sign-in details or anything not posted. | Names the data instead of "messages and activity". |
| "that GameCult builds and runs" | Whether a dataset or model may leave GameCult is Meta's call, still open (below); the notice claims nothing either way. |
| "Nothing that came from Discord is used, in any form." | `gamecult-chat:ruling:discord-taints-training`. Whether any path carries Discord content into a chat room is for the chat steward to confirm (not established here), and any that exists must be excluded from every set for the sentence to stay true. |
| "Agreement is needed to speak in a recorded room. You can have a GameCult account, see the members page and vote without agreeing." | Meta's ruling, 2026-11-11: the chat token needs a live receipt for the version in force; nothing else does. "to speak in a recorded room" is settled wording. |
| "Lines you have already spoken stay: withdrawing deletes nothing." | Withdrawing ends future speaking and future training use; it recalls nothing. The sentence is on the page where the Agree button is (the consent page and the registration form both render the notice beside it), so nobody agrees believing they can unsay a line. |
| Names the door: the consent page, while signed in. | "Withdraw at any time" with no door is no promise. |

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
   offers the new one. Since the chat gate, a bump also stops new chat tokens for everyone until they agree (their next chat sign-in lands on that page; a session already open runs to its natural expiry unless `BackchannelLogoutUri` is set, when the 30 s reconciler ends it). Nobody is emailed or prompted elsewhere; asking is the page's job, and silence means no.
3. **Withdrawal had no CSRF check.** True. The session cookie's SameSite default is Lax, which already keeps a cross-site
   POST from carrying it, but nothing in the handler relied on that. Both state changes (agree, withdraw) are now
   form POSTs refused with 403 unless they came from the site itself (`RequestOriginGuard`, the strict form), before
   anything is read or written.
4. **Provider-created and existing accounts have no receipt.** Correct, and now stated as the rule: no receipt, no use.
   Since the 2026-11-11 ruling it is also: no receipt, no chat token. They keep their account, the members page and their
   votes, and learn it where it bites: the refused chat sign-in lands them on the consent page (`oidc-issuer.md`, Chat needs a
   receipt). Nobody is emailed or prompted elsewhere.
5. **No export exists.** True. Every promise about use is a promise about code not written. The notice must not be
   treated as operative for real people until the export enforces the contract with a test that a withdrawn or
   never-agreed account's rows are absent (`registration-gates.md`, Consent receipt, Consumer). Confirm (Before deploy) that no
   deploy has put an earlier version 1 in front of anyone.

## Consent is optional

Registration succeeds with or without agreement. A receipt exists only if the person agreed. The account is not
conditioned on agreement, which removes the objection that agreement obtained as a condition of an account is not a free
choice; what agreement buys is speaking in a recorded room, and the form says so beside the checkbox. This is the
steward's ruling on Eureka's recommendation, and Meta's ruling of 2026-11-11 added the chat gate. One consequence: the
registration form does not enforce the notice.

## Still open for Meta

- Purpose and scope: whether a dataset or model may leave GameCult, and whether a third-party model API processing
  messages to answer them is covered. The notice is silent on both.
- Deletion of messages on request, who does it, and the contact words. The notice offers none.
- Controller and contact, and minimum age. Nothing here addresses either.
- Counsel's reading of consent under the law that applies to the people registering. Not researched here.

## What remains

- A signed-out native account cannot withdraw until sign-in recovery exists.
- No training export, so no operative use.
- Wording changes are a version bump with a new text and a new pinned hash in `TrainingNoticeFrozenTests`; never edit a version's text in place; the frozen-hash test fails if you do.

# Training notice: proposed version 2

Proposal from Eureka, for Heimdall to merge or refuse. It changes no code and no served text; merging it records the
proposal, and putting it live is a separate cut that bumps `TrainingNotice.Version` (see "To put it live"). Companion to
`docs/registration-gates.md`, whose "Consent receipt" section is the contract this reads against. Read against main
`33be966`: `TrainingNotice.cs`, the register and withdraw handlers in `Program.cs`, and the doc. Not run, not tested.

## What is wrong with version 1

| Line in version 1 | Problem |
| --- | --- |
| "Chat messages and activity" | "Activity" names nothing. A reader cannot tell whether it covers display names, reactions, presence, direct messages or the email address Bifrost will soon hold. |
| "train and evaluate AI models" | Whose models, and does the text leave GameCult? Sending chat to a vendor's API to answer a message is different from building a dataset. The notice must say which it is. |
| "No Discord material is used" | True only if nothing bridged from Discord reaches a chat room. The sentence is a promise about the whole room, not about the registrant, and nobody has checked that no bridge exists. |
| "you can withdraw it at any time" | No form and no page exists. A native account has no credential (`registration-gates.md`, Email verification), and withdrawal needs a signed-in session. The sentence promises a door the person cannot yet reach. |
| "Withdrawing stops future use; it cannot recall use that already happened" | Honest, but silent on the question people actually ask: is my data deleted. It is not, and the notice must say so. |
| Registration without acceptance creates nothing | Consent is a condition of having an account. That is not a free choice, and the contract already treats "no receipt" as "no use", so the condition buys nothing it does not already have. |
| No controller, contact or retention | A person cannot tell who holds the receipt, for how long, or whom to ask. |

## Proposed text (version 2)

Square brackets mark words only Meta can fill; they are not in the served text until filled.

> **GameCult chat training notice, version 2**
>
> GameCult may use messages you post in GameCult chat to train and evaluate AI models that GameCult builds and runs.
> [Meta: confirm that this is the whole purpose, and whether any dataset or model leaves GameCult.]
>
> **What counts.** The text of your messages, with their time and room, tied to your account name. Not your email
> address, your sign-in details or anything you did not post. Nothing that came from Discord is used, in any form.
>
> **It is your choice.** [If Meta rules consent optional:] You can use GameCult chat without agreeing. If you do not
> agree, none of your messages are used.
>
> **If you agree,** we record your account, this notice's version number and the time. Nothing else is stored with it.
>
> **If you withdraw,** we leave all your messages out of every training set made after that moment. Withdrawing deletes
> nothing: your messages stay in chat and on our servers, anything already used to train a model stays used, and a
> dataset already built is not recalled. We keep the record that you agreed and when you withdrew. [Meta: deletion of
> messages is a separate request, via ___, or not offered.]
>
> **How to withdraw:** ___. **Questions:** ___.

About 170 words. Plain, no consent boilerplate, and every sentence is something the code or an owner can honour.

## What each change rests on

- "Tied to your account name" and "not your email address": the export contract in `registration-gates.md` keys on the
  account; nothing exports an address because no address is stored today. If email verification keeps the address, this
  line becomes a rule the export must enforce, so keep it only if that is ruled.
- "Leave all your messages out of every set made after": the contract says an export includes an account's data only
  while a receipt with `WithdrawnAtUtc` null exists, so withdrawal excludes the account's whole history from later
  exports, not just later messages. The notice says the stronger thing because the contract delivers it.
- "Withdrawing deletes nothing": the withdraw handler sets a timestamp and nothing else. The room history lives in the
  chat server, which Bifrost does not delete from. Whether backups hold it is for the chat steward to confirm.
- "We keep the record": the receipt row is one-way and never deleted, so say it.

## Receipt flow: gaps found in reading

1. **Withdrawal needs a session a native registrant may not have.** `POST /auth/bifrost/consent/withdraw` returns 401
   without a signed-in actor, and a native account has no credential until email verification gives it one. Until the
   sign-in link exists, withdrawal by a native account is only possible through chat sign-in, if that works for them. I did
   not trace whether it does.
2. **No re-ask path.** A new notice version leaves every old receipt unequal to the current version. The contract is
   fail-closed (no match, no export), which is safe, but it also means a version bump silently removes everyone from the
   dataset. The notice should say a new version asks again and silence means no.
3. **Provider-created and existing accounts have no receipt**, so they are excluded from any export. That is correct
   behaviour and should be stated as the rule, not left as a "scope limit".
4. **Withdrawal is not CSRF-checked in what I read.** It is a cookie-authenticated POST with no body. I did not test
   whether the antiforgery or cookie same-site settings cover it. Heimdall should confirm.
5. **Receipts are not exported anywhere yet.** The consumer (a training export) does not exist, so every promise above is
   a promise about code not written. The notice should not go live to real people before the export enforces the
   contract with a test that a withdrawn account's rows are absent.

## Needs a decision

For Meta (hers, not mine to settle):

1. **Purpose and scope**: own-model training only, or datasets or models that leave GameCult? Third-party model APIs
   processing messages to answer them: covered by this notice or not mentioned?
2. **Optional or required consent.** I recommend optional: registration succeeds and a receipt exists only if ticked.
   The contract already excludes anyone without a receipt, so the code change is to make `acceptedNoticeVersion`
   nullable. Required consent is also a legal question (consent given as a condition of service is weak in the EU);
   I am not counsel and did not verify this against current law.
3. **Deletion**: offered on request, and by whom, or not offered. The notice must match.
4. **Controller and contact** words, and how a person withdraws before the form exists.
5. **Minimum age.** The notice and flow say nothing about children. Not researched.

For members (the people whose messages these are): whether they want existing members asked at all. Version 1 does not
re-ask existing accounts, so they stay out of any dataset; asking them is the only way they can opt in.

For Heimdall: items 1, 4 and 5 under "Receipt flow", and whether a Discord bridge into a chat room exists or is planned
(the ruling `gamecult-chat:ruling:discord-taints-training` forbids Discord material in training; the notice repeats it, so
the claim must be true).

## To put it live

A code cut, after Meta fills the brackets: replace `TrainingNotice.Text`, set `Version = 2`, update the tests that pin
version 1, and decide the optional-consent change in the same cut because it changes what a receipt means. Version 1
should not have reached the public (the doc says no form exists; I did not check what is live), so a bump should cost no
re-ask; any real account holding a version 1 receipt falls out of the dataset until asked again.

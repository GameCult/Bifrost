# Provider Patronage Intake

Patreon and PayPal collection are Heimdall-to-Bifrost crossings, not a Bifrost
payment stack.

## Authority

- PayPal owns money movement, captures, subscription status, refunds, reversals, and chargebacks.
- Patreon owns membership status, charge status, pledge amount, and tier
  assignment.
- Heimdall owns provider app credentials, webhook receipt where used, provider
  signature verification, provider account linking, provider-event
  normalization, and the linked Patreon membership reader already used for
  Repixelizer access.
- Bifrost owns patronage meaning: support events, point derivation, tier snapshots, voting weight, and audit records.

## Intake Endpoint

Heimdall POSTs verified support facts to:

`POST /heimdall/patron-support/events`

The raw request body must be signed with `HMACSHA256(Heimdall:PatronSupportIntakeSecret, body)` and sent as:

`X-Heimdall-Signature-256: sha256=<hex>`

Payload:

```json
{
  "heimdallAccountId": "heimdall-account-id",
  "provider": "PayPal",
  "providerEventId": "paypal-webhook-event-id",
  "kind": "OneTimeDonation",
  "amount": 125.00,
  "currencyCode": "USD",
  "externalSupportId": "PAYMENT.CAPTURE.COMPLETED:CAPTURE-ID",
  "supportedAtUtc": "2026-06-09T15:00:00Z",
  "isCurrentRecurringSupport": false,
  "providerPayerId": "PAYPAL-PAYER-ID",
  "providerSubscriptionId": "",
  "notes": "Verified PayPal checkout capture from Heimdall."
}
```

Valid `provider` values are `PayPal` and `Patreon`. `Manual` is rejected for this endpoint.

Valid `kind` values:

- `OneTimeDonation`: positive completed one-time support.
- `RecurringSupportSnapshot`: positive current recurring support; set `isCurrentRecurringSupport` to `true` when it is the current support amount.
- `SupportAdjustment`: negative refund, reversal, chargeback, or correction.

`providerEventId` is the idempotency key inside a provider. Bifrost stores one support event per `(provider, providerEventId)`.

## PayPal Event Mapping

- Checkout/order donations: Heimdall records `OneTimeDonation` only after PayPal reports a completed capture.
- Subscription activation/current paid support: Heimdall records `RecurringSupportSnapshot` after verified active paid support.
- Subscription cancellation, suspension, expiration, or failed payment: Heimdall should send a new recurring snapshot that clears or reduces current support when paid support is no longer current.
- Refunds, reversals, and chargebacks: Heimdall records `SupportAdjustment` with a negative amount.

Unlinked PayPal support must stay pending in Heimdall. Bifrost will not create voting power from a PayPal payer id alone.

## Patreon Event Mapping

Patreon recurring support uses Heimdall's existing linked Patreon identity and
membership substrate. The same membership reader that evaluates Repixelizer and
Bifrost tier access now feeds Bifrost support sync.

Heimdall exposes an app-authenticated backend route:

`POST /v1/apps/bifrost/patron-support/sync`

Bifrost or an operator job supplies the linked Heimdall account id and required
tier title. Heimdall refreshes the stored Patreon credential if needed, reads
the Patreon identity profile with memberships and currently entitled tiers,
finds an active paid member record for the requested tier, and posts a signed
`RecurringSupportSnapshot` to Bifrost.

The Patreon support fact uses:

- `provider = Patreon`
- `kind = RecurringSupportSnapshot`
- `amount = currently_entitled_amount_cents / 100`
- `currencyCode = Patreon campaign currency when available`
- `providerPayerId = Patreon user id`
- `providerSubscriptionId = Patreon member id`
- `isCurrentRecurringSupport = true`

Bifrost does not store Patreon access tokens and does not parse Patreon profile
JSON. It verifies Heimdall's HMAC, resolves the Heimdall account id, records the
support event, and derives points locally.

# PayPal Patronage Intake

PayPal collection is a Heimdall-to-Bifrost crossing, not a Bifrost payment stack.

## Authority

- PayPal owns money movement, captures, subscription status, refunds, reversals, and chargebacks.
- Heimdall owns PayPal app credentials, webhook receipt, PayPal signature verification, provider account linking, and provider-event normalization.
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

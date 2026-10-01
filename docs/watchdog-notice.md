# Watchdog notice reader

`tools/watchdog-notice.mjs` is a one-shot reader. It turns Idunn's operator
incidents into Discord DMs to the owner. Run it from a timer.

```
node tools/watchdog-notice.mjs process --incident-store P --journal-store J --receipt-store R [--bridge-cli B]
node tools/watchdog-notice.mjs status --journal-store J
```

## Requirements

- **CultLib at `8cb3b728` or later.** The reader loads `@gamecult/cultcache-ts`,
  the name CultLib gave the package in `8cb3b728` (2026-09-04), from
  `$VOIDBOT_CULTLIB_ROOT` or the sibling `../CultLib`. An older CultLib fails at
  import with `MODULE_NOT_FOUND`; the reader reports `cultlib-unavailable`. The
  same floor applies to every Bifrost tool that loads CultCache
  (`tools/persona-feedback.mjs` and the others). A deployment pairs a Bifrost
  release with a CultLib commit at or after this one.
- `DISCORD_OWNER_ID` in the environment: the only source of the recipient. The
  reader exits non-zero before writing the journal if it is missing.
- `BIFROST_DISCORD_BOT_TOKEN` in the environment, read only by
  `bifrost-bridge.mjs`, which the reader spawns.

## Retry

A notice whose send failed (bridge exit 75) is never dropped. It is eligible
again once `min(2^(attempts-1), 60)` minutes have passed since its last attempt
in the journal: 1, 2, 4, 8, 16, 32, then every 60 minutes. A run that finds it
inside its delay skips it without spawning the bridge. An entry stamped ahead of the clock (a host clock stepped forward, then corrected) is eligible now, and its next attempt rewrites the stamp. Every attempt carries the
same nonce, so a Discord outage of any length costs a late notice, not a lost
one, and a retry that succeeds is the one post.

## Behaviour

- P is Idunn's `idunn.operator_incident.v1` store. The reader reads it without a
  lock and never writes it. An absent P is a clean no-op.
- One notice when an incident opens and one when it closes, each at most once.
  The closing notice is sent only after the opening notice completed.
- J is Bifrost's journal, `bifrost.watchdog_notice_execution.v1`, keyed
  `<incident_key>#opened` or `<incident_key>#closed`. A `running` entry found at
  start becomes `unknown` and is never sent again. A `failed` entry is retried on
  later runs with the same nonce, never given up on (see Retry). The journal is never
  pruned: what Bifrost has sent is Bifrost's memory, whatever P holds.
- The bridge (`bifrost-bridge.mjs discord-dm`) exits 75 for argument or token
  errors, a failure opening the DM channel, or any non-2xx answer to the message
  POST. A non-2xx is retried, not proven unsent: the retry carries the same nonce
  and relies on Discord's `enforce_nonce` inside its window, so a 5xx that Discord
  answered after creating the message can be delivered twice if the retry falls
  outside that window. A 5xx stays retryable because parking it as `unknown`
  would lose the notice on every routine Discord outage. The reader records
  `failed` for exit 75 only. Any other non-success without a message id (another exit code,
  a signal, the spawn timeout, unreadable output) is `unknown`: never sent again,
  counted by the exit status.
- The exit status is non-zero while any entry is `unknown` or has failed three or
  more times, or while P holds a record of this schema that breaks its contract.
  systemd shows that; `status` lists the entries, with `attempts` and, for a
  `failed` entry, `nextEligibleAt`. The non-zero status is the signal only: the
  entry keeps being retried.
- Notice text is built from `condition`, `subject` and ISO UTC times only. The
  subject follows Idunn's `require_id` (1-256 bytes of `[A-Za-z0-9-_.:/]`) and
  is placed in inline code. A time outside the ECMAScript Date range refuses the
  record.

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

## Behaviour

- P is Idunn's `idunn.operator_incident.v1` store. The reader reads it without a
  lock and never writes it. An absent P is a clean no-op.
- One notice when an incident opens and one when it closes, each at most once.
  The closing notice is sent only after the opening notice completed.
- J is Bifrost's journal, `bifrost.watchdog_notice_execution.v1`, keyed
  `<incident_key>#opened` or `<incident_key>#closed`. A `running` entry found at
  start becomes `unknown` and is never sent again. A `failed` entry is retried on
  later runs with the same nonce, up to five attempts. Terminal entries are
  deleted once Idunn no longer lists the incident.
- The exit status is non-zero while any entry is `unknown` or out of attempts, or
  while P holds a record of this schema that breaks its contract. systemd shows
  that; `status` lists the entries.
- Notice text is built from `condition`, `subject` and ISO UTC times only.

# Reddit Interface

`r/GameCultOrg` is a public organizing surface for Bifrost-scoped work and governance. It can host proposal threads, patron-facing discussion prompts, work-priority arguments, public receipts, and Persona-authored posts.

It is not the canonical vote ledger. Bifrost motions, votes, priority signals, patron support events, tier snapshots, and ledgers remain the authority.

## Authority Map

- Owner: Bifrost owns Reddit crossings when Reddit is used for GameCult work, governance, patron discussion, public receipts, or Persona-authored organizing posts.
- Inputs: Bifrost bridge policy, source topic/work item/motion, Heimdall-linked actor claims when available, Persona identity, subreddit target, post title/body, and flair id/text.
- Outputs: a Reddit thread URL and thing id recorded as a bridge receipt against the Bifrost source object.
- Derived state: Reddit upvotes, comment counts, flair labels, and thread visibility are discussion evidence only.
- Forbidden writers: subreddit reactions, ad hoc Persona scripts, Reddit account state, or flair text must not decide voting weight.
- Shared path: Reddit, Discord, web, and future public surfaces must all commit votes or priority signals through the same Bifrost motion/priority primitive.
- Cut line: no separate Reddit poll, karma, or comment-count rule may become patron voting power.

## Patron Voting Power

Patron voting power should be prepared from Bifrost-owned account and support state:

- Heimdall links external provider accounts to a GameCult account.
- Bifrost records patron support events and current recurring support state.
- Bifrost derives effective patron points and patron tier snapshots through `PatronageService`; scheduled decay jobs remain future work.
- Motions snapshot the effective voting weight when a linked actor votes.

Reddit can invite, explain, and collect discussion. It does not grant weight by itself. When a Reddit user should cast a vote or priority signal, the actor must be linked and the vote must be written into Bifrost.

## Persona Flair

Personas may post through the Bifrost Reddit app. The Reddit account is the bridge actor, and the custom post flair identifies the speaking Persona.

Use `--persona-name` for the displayed Persona name. If the subreddit has a fixed flair template, pass `--persona-flair-id`. If the template allows custom flair text, pass `--persona-flair-text`; otherwise the bridge uses `--persona-name` as the flair text.

```powershell
node .\tools\bifrost-bridge.mjs reddit-post `
  --title "Nibu: Reset-loop continuity" `
  --persona-name Nibu `
  --persona-flair-text Nibu `
  --content-file E:\tmp\nibu-thread.md
```

Dry-run before posting:

```powershell
node .\tools\bifrost-bridge.mjs reddit-post `
  --title "Patron vote discussion: June priorities" `
  --persona-name Bifrost `
  --content "Canonical vote will happen in Bifrost; this thread is for discussion." `
  --dry-run true
```

## Credentials

The local bridge uses a Reddit OAuth refresh token for the Bifrost Reddit app:

- `BIFROST_REDDIT_CLIENT_ID`
- `BIFROST_REDDIT_REFRESH_TOKEN`
- `BIFROST_REDDIT_CLIENT_SECRET`, when the app uses one
- `BIFROST_REDDIT_SUBREDDIT`, optional, defaults to `GameCultOrg`
- `BIFROST_REDDIT_USER_AGENT`, optional

Heimdall should eventually own Reddit account linking, grant evaluation, and app credential custody. Until then, `tools/bifrost-bridge.mjs reddit-post` is the local Bifrost-owned actuator.

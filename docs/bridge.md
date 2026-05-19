# Bifrost Bridge

Bifrost is the bridge for GameCult agent work that needs to cross into GitHub, Discord, or another governed external surface.

That means Bifrost owns the operational meaning of the crossing:

- what action is being requested
- which repo, channel, PR, or discussion surface it targets
- which agent or account is acting
- what permission or review state made the action acceptable
- what receipt proves the crossing happened

It does not mean Bifrost should own OAuth sludge. Heimdall owns OAuth, linked identities, token custody, grants, and signed account claims. Bifrost consumes Heimdall identity and permission facts, then decides whether a bridge action is allowed in the Bifrost domain.

## Ownership

- Heimdall owns provider OAuth and account-linked credentials.
- Bifrost owns bridge action policy, routing, execution receipts, and governance transport.
- VoidBot observes Discord, packages conversation, and speaks through registered personas. It should not be the durable authority that mutates GitHub.
- CultCache stores lightweight agent transport packets when the bridge needs file-native state.
- CultNet carries those packets between runtimes.

## Local Bridge CLI

`tools/bifrost-bridge.mjs` is the current local bridge executor.

It intentionally uses boring local credentials while Heimdall's managed GitHub credential runtime is still unfinished:

- GitHub actions use the local `gh` authenticated account.
- Discord posts use `BIFROST_DISCORD_BOT_TOKEN` or `DISCORD_BOT_TOKEN`.

The CLI is not the final permission system. It is the working bridge actuator Bifrost owns now, so VoidBot and repo Faces can stop carrying this machinery themselves.

### GitHub Draft PR

```powershell
node .\tools\bifrost-bridge.mjs github-draft-pr `
  --repo-root E:\Projects\AetheriaLore `
  --identity nibu `
  --title "Nibu: Glitchcraft and reset-loop continuity" `
  --path Aetheria/Articles/Nibu/glitchcraft-and-reset-loops.md `
  --content-file E:\tmp\article.md `
  --body "Drafted from recent Aquarium consensus." `
  --base main
```

The command:

1. checks the target repo is clean unless `--allow-dirty` is set
2. creates a branch
3. writes the target file
4. commits and pushes
5. opens a draft PR through `gh pr create`
6. restores the original branch
7. prints a JSON receipt

Use `--dry-run` to print the planned action without writing or posting.

### Discord Post

```powershell
node .\tools\bifrost-bridge.mjs discord-post `
  --channel-id 1501196543150264332 `
  --content "Nibu drafted the article and put it in a PR: https://github.com/..."
```

The command posts through Discord's REST API using the configured bot token and prints a JSON receipt.

## Heimdall Integration Target

The correct future credential path is:

1. Heimdall implements GitHub OAuth callback/runtime and managed credential custody.
2. Heimdall issues Bifrost-verifiable claims for account identity and bridge capabilities.
3. Bifrost records a bridge action request and checks the Heimdall-derived permission facts.
4. Bifrost executes or queues the action through the appropriate bridge executor.
5. Bifrost records the GitHub/Discord receipt and exposes it back to the requesting agent/runtime.

In that shape, Bifrost is the shiny bridge. Heimdall is the gatehouse with the keys. VoidBot is not hiding a bolt cutter in its coat.

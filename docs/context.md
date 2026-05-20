# Context

## Why Bifrost Exists

GameCult needs a platform that can connect public projects, contributor participation, governance, agent work requests, public transport receipts, and internal accounting without relying on chat memory, disconnected tools, or hand-maintained spreadsheets of dubious destiny. Bifrost is that connective layer.

Bifrost's bridge role follows from that purpose. GitHub proposals, Discord dispatch receipts, CultNet/CultCache intake packets, and future collaboration interfaces are not side channels when they carry GameCult work. They are public-process crossings, so Bifrost owns their request shape, routing, policy, audit, and receipt semantics. The detailed boundary map lives in `docs/jurisdiction-map.md`.

## Relationship To Other Workspaces

- `E:\Projects\gamecult-site`
  - public-facing Quartz site for the studio, projects, and docs
  - source of the Labor Platform concept and public framing
  - Bifrost should stay aligned with `GameCult\Docs\labor-platform.md` for points, tiers, decay, revenue share, and workflow shape
- `E:\Projects\gamecult-ops`
  - source of deployment and infrastructure conventions
  - documents Yggdrasil, nginx, systemd, and local-Postgres patterns
- `E:\Projects\Heimdall`
  - owns OAuth, linked accounts, token custody, signed claims, grants, consent, and revocation
  - Bifrost consumes Heimdall identity/capability facts instead of becoming the credential vault
- `E:\Projects\VoidBot`
  - observes Discord, packages consensus, and runs repo Face heartbeats
  - should call Bifrost for governed GitHub/Discord/intake crossings instead of becoming hidden transport authority
- `E:\Projects\CultLib`
  - stewards CultCache, CultNet, CultMesh, typed persistence, schemas, and reusable sync/storage libraries
  - Bifrost defines the meaning of agent transport packets while CultLib-provided primitives store and move them
- `Yggdrasil`
  - GameCult infrastructure host
  - intended deployment home for `bifrost.gamecult.org`

## Immediate Next Target

After bootstrap, the next session should start implementing the first vertical slice:

- GitHub OAuth sign-in
- membership gating
- core project/member data model
- work items from GitHub and internal sources
- motions
- ledger entries

## Strategy Notes

- Bifrost now has the first application slice, but it still needs deploy-foundation work before it should go live on Yggdrasil.
- The full implementation roadmap, including contribution points, patron points, decay, and revenue share, is documented in `docs/full-implementation-strategy.md`.

## Product Defaults

- member alpha first
- invite-only onboarding
- GitHub as the only auth provider in v1
- GitHub issues and internal tasks both supported
- app-native governance and voting
- internal ledgers only
- no crypto, no wallets, no payout automation

## Glossary

- `Member`
  - an approved participant with a GitHub-linked identity and an active membership state
- `Work Item`
  - a unit of work tracked by the platform, sourced from GitHub or created internally
- `Motion`
  - a formal proposal that affects either project work or GameCult policy/structure
- `Ledger Entry`
  - an immutable record of patron points, contributor points, nominal compensation, or adjustments
- `Payout Proposal Batch`
  - an admin-reviewed grouping of payout recommendations derived from approved ledger data

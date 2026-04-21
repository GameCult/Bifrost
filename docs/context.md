# Context

## Why Bifrost Exists

GameCult needs a platform that can connect public projects, contributor participation, governance, and internal accounting without relying on chat memory, disconnected tools, or hand-maintained spreadsheets of dubious destiny. Bifrost is that connective layer.

## Relationship To Other Workspaces

- `E:\Projects\gamecult-site`
  - public-facing Quartz site for the studio, projects, and docs
  - source of the Labor Platform concept and public framing
- `E:\Projects\gamecult-ops`
  - source of deployment and infrastructure conventions
  - documents Yggdrasil, nginx, systemd, and local-Postgres patterns
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

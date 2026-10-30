# Discord-like roles on Matrix: prior art

Served: the operator question whether Discord-like roles need a Matrix fork (2026-10-30), eureka-body:follow_up:matrix-roles-from-bifrost-estates, and the chat-client fork. Eyes pass, facts only; [U] marks unverified.

# Eyes: Matrix roles prior art (2026-10-30). Facts only. [U] = unverified.
Statuses read via `gh api repos/matrix-org/matrix-spec-proposals/pulls/N` (state, merged, labels), 2026-10-30.

## Q1 Spec
MSCs (all github.com/matrix-org/matrix-spec-proposals/pull/N):
- 2812 [WIP] Role-based power/permissions (turt2live): OPEN, unmerged, last update 2025-08-12, labels needs-implementation, requires-room-version.
- 4056 Role-Based Access Control (mk II) (turt2live, created 2023-09-15): OPEN, last update 2025-04-12. PR body is only a rendered link (branch travis/msc/rbac-pl); text not read [U].
- 3949 Power Level Tags (ajbura): OPEN, update 2026-10-11, needs-implementation. Defines state event `m.room.power_level_tags`, state_key "", content = map of power-level integer -> {name, color (hex), icon{key emoji|mxc, info}}; its text says tags would show in timeline and member list. Implementation listed: Cinny. Text: raw.githubusercontent.com/ajbura/matrix-spec-proposals/power-level-tag/proposals/3949-power-level-tag.md
- 4522 Username colors (nushea, created 2026-07-28): OPEN. `m.color_preference` {on_dark,on_light}: per-account in extended profile (MSC4133), per-room in `m.room.member` content, per-message in `m.per_message_profile`. Implementation: Sable PR 1526 (setting + rendering).
- 4527 State event power levels (created 2026-08-09): OPEN. Impl links: complement#940, ruma#2640, Continuwuity PR 2393.
- 3915 Owner power level (0-150): CLOSED unmerged 2026-01-18, label finished-final-comment-period.
- 4289 Privilege creators: MERGED (closed 2025-07-22; labels merged, disposition-merge, A-Room Spec).
- 3991 Power level up!: OPEN, last update 2023-04-14.
- 3219 [WIP] Space Flair (turt2live): OPEN, last update 2022-10-21.
- 4121 m.role.moderator in /.well-known/matrix/support: OPEN; homeserver support records, not room roles.
- 2212 Third party user power levels: OPEN (2022).

Space-wide power levels:
- 2962 Managing power levels via Spaces (ara4n): OPEN, last update 2022-05-05. Template power_levels in parent space inherited by children (per search summary of its text [U]).
- 3216 Synchronized access control for Spaces (joepie91): OPEN, 2022-05-05. Adds `m.space.power_levels`; comment: a non-spaces-aware client's edits get overwritten on next inherited change [U, search summary].
- No merged MSC found giving space-wide power levels. Both are OPEN.

Custom state events:
- Spec: complete event MUST NOT exceed 65536 bytes (spec.matrix.org/latest/client-server-api/).
- Per-event-type send level via `m.room.power_levels` `events` map, else `state_default` [spec knowledge, not re-fetched, U].
- State arrives via /sync as room state; whether a client reads an unknown type is client-specific. Cinny reads `m.room.power_level_tags` (Q3).

## Q2 Element Web
- Releases: v1.12.33 (2026-10-27), v1.12.32 (2026-10-16), v1.12.31 (2026-10-13), with rc builds in between (gh api repos/element-hq/element-web/releases). Repo is a pnpm/nx monorepo: packages/{module-api, shared-components, shared-types, shared-utils, ...}, modules/{banner, restricted-guests, widget-lifecycle, widget-toggles}.
- License: AGPL-3.0 (GitHub spdx); repo also contains LICENSE-GPL-3.0 and LICENSE-COMMERCIAL ("Licensees holding a valid commercial license with Element may use this software in accordance with the terms contained in a written agreement").
- AGPL duties for a hosted modified fork: not sourced this pass [U]. (General knowledge: section 13 network-use source offer.)
- config.json (docs/config.md): `default_theme`, custom themes, `branding`, `embedded_pages`, `setting_defaults`, `features` (labs), UI-feature toggles, and `modules` (array of URLs/paths dynamically imported at startup, ~lines 597-609). Doc: "doesn't support complete re-branding/private labeling".
- Module API: `@element-hq/element-web-module-api` 2.3.0 (packages/module-api/package.json). Module = class with static `moduleApiVersion` semver and `load()`. Surfaces in packages/module-api/src/api/: auth, builtins, client, composer, config, custom-components, customisations, dialog, extras, i18n, navigation, profile, settings, stores, urlpreview, widget, widget-lifecycle, x509.
  - custom-components.ts (@alpha, "Subject to change"): custom message renderers with render hints, including a hint documented as "Should the sender's profile be rendered for this event" (default true); room preview bar renderer. Overriding render means editing is unavailable.
  - customisations.ts: `registerShouldShowComponent` only shows/hides listed UI components (invite, create room/space, directory, widgets, explore, room options).
  - extras.ts (@alpha): space-panel item, room-header buttons.
  - profile.ts: read-only profile of the logged-in user.
  - No sender-name decorator or member-list hook found in the file listing; stores.ts, builtins.ts, client.ts not read in detail [U].
- Theme details (custom theme JSON/CSS variables): not read [U].
- Maintained-fork cost (merge pain, cadence of known forks): not gathered [U].

## Q3 Other clients and prior art
- Cinny (AGPL-3.0, pushed 2026-10-28): src/app/hooks/usePowerLevelTags.ts has DEFAULT_TAGS {9001 Goku, 150 Manager, 101 Founder, 100 Admin, 50 Moderator, 0 Member}, each with name+color; reads a state event (type in src/types/matrix/room.ts); src/app/hooks/useMemberPowerTag.ts resolves a member's tag from power level, with a room-creators tag, and applies `accessibleColor` per ThemeKind. This is the MSC3949 implementation.
- Sable (AGPL-3.0; repo now "Sable-Legacy", v2 at git.sable.moe): implements MSC4522 colours (SableClient/Sable#1526, per MSC text).
- Commet (AGPL-3.0, pushed 2026-10-29): room permissions settings page defines `MatrixRoomRoleEntry` {name, powerlevel} (commet/lib/ui/pages/settings/categories/room/permissions/matrix/matrix_room_permissions_page.dart). Timeline colour/badge display not checked [U].
- FluffyChat, Nheko, SchildiChat: issue searches returned nothing; no evidence either way [U].
- Rocket.Chat-on-Matrix, Conduit/Continuwuity communities, Polycule: not researched [U]. Continuwuity has an MSC4527 implementation PR (forgejo.ellis.link/continuwuation/continuwuity/pulls/2393).
- mautrix-discord (github.com/mautrix/discord): stores Discord roles in a DB `role` table (dc_guild_id, dcid, name, icon, mentionable, managed, hoist, color, position, permissions), synced from GuildRoleCreate/Update/Delete (user.go:684-688, 978+; database/role.go). ROADMAP.md lists "Power level" and "Role permissions" unchecked. portal.go:1734-1739 uses room power levels only to gate @room mentions. No evidence found of writing Discord roles into Matrix state or power levels.

## Q4 Body
- Chat map: F:\Projects\gamecult-ops\docs\gamecult-chat-map.md. Element Web at chat.gamecult.org is static `root /srv/element-web/current` (symlink to /srv/element-web/releases/<version>), 404 until campaign cut C5 (lines 71, 101); runbooks/gamecult-chat-host-yggdrasil.md:6 says Element Web's release belongs to C5. No deployed Element release found in the docs. Homeserver matrix.gamecult.org -> Synapse 127.0.0.1:8834; federation and admin paths 404; apex /.well-known/matrix/client base_url https://matrix.gamecult.org (lines 93-102).
- Synapse: ygg-verify probe ran matrixdotorg/synapse 1.163.0 on postgres:16 (chat map line 27, P5). Upstream latest Synapse v1.164.1 (2026-10-28), MAS v1.28.0 (2026-10-27). Deployed versions on Yggdrasil not checked (no ssh) [U].
- Idunn targets `synapse`, `matrix-auth`; C3 `synapse-mas-under-idunn` packages them.
- Bifrost origin/main docs/oidc-issuer.md: OpenIddict 7.7.1; scopes `openid profile`; claims only `sub`, `preferred_username` (never changes), `name`; "No e-mail, no Heimdall id, no GitHub id, and no membership detail leaves Bifrost". MAS is the OIDC client.
- Bifrost role code: only membership roles in its own DB (`MemberRole` StandardMember|Producer|Maintainer|LedgerReviewer|PlatformAdmin, RoleAssignment; src/Bifrost.Web/Data/BifrostDbContext.cs:113-114; docs/research/customer-onboarding-body-facts.md:16). Grep of origin/main docs and src for power_level / m.room.power found nothing. docs/bridge.md:199: Personas speak through `tools/persona-matrix-mouth.mjs` with per-persona Matrix access tokens (the only Matrix writer found).

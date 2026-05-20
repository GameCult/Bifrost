# Bifrost Jurisdiction Map

Bifrost is GameCult's governance, labor, and public-process platform. Its bridge role is not a second mission stapled onto the side; it follows from the original Labor Platform idea. Users, patrons, members, and contributors need one place to vote on policy, prioritize work, allocate rewards, review accepted work, and keep credit legible.

The core loop is economic and social, not merely technical:

1. A user, patron, member, agent, or contributor identifies work that should exist.
2. GameCult participants vote, fund, boost, or otherwise express priority.
3. The reward for the work rises as demand and importance rise.
4. A contributor picks up the work when the bounty/credit is worth their time.
5. A project maintainer reviews and accepts the pull request or other completion artifact.
6. Bifrost credits the contributor for the accepted work item and records the reward allocation.

That loop already has to cross GitHub, Discord, Bifrost's own web UI, and eventually other collaboration surfaces. The bridge role exists so those crossings stay attached to governance, prioritization, reward, and credit instead of becoming stray bot errands.

The agent swarm makes that need louder. Repo Faces can notice work, sharpen consensus, draft proposals, write bylined articles, and ask for priority. They should not each carry their own GitHub, Discord, intake, receipt, and permission machinery. Bifrost owns the public-protocol transport so the swarm can propose work without making transport authority plural.

## Core Invariant

Bifrost owns governed public crossings for GameCult work.

A governed public crossing is any action where an internal GameCult actor, agent, Face, or process causes a durable or visible change on a public or collaboration surface:

- GitHub draft PRs, PR comments, issues, review artifacts, and work links
- Discord-native Bifrost interactions, persona announcements, dispatch receipts, and role-addressed swarm I/O
- CultNet/CultCache intake packets that move work requests between runtimes
- future collaboration interfaces that expose GameCult decisions, requests, work status, or receipts outside a single local process

Bifrost does not own every byte that moves. It owns the meaning, policy, routing, audit, and receipt of the crossing.

## Why This Belongs To Bifrost

Bifrost was already the member alpha platform for tasks, governance, contributor accounting, reward allocation, and operational legibility. GitHub integration was part of that from the start because GameCult work lives partly in GitHub issues, PRs, and review history. Discord integration belongs in the same authority when Discord is being used as a Bifrost interface: voting prompts, prioritization discussion, bounty/work updates, member/patron/user access, agent dispatch, and public receipts.

The bridge role is therefore an extension of Bifrost's Labor Platform role:

- tasks need a canonical relationship to GitHub and internal work items
- proposals need a durable review surface instead of dissolving into chat
- votes, priority signals, and reward boosts need provenance and accountable closure
- accepted work needs a maintainer-approved completion artifact before credit/reward is assigned
- contributors and agents need one place to see what was requested, who wanted it, what bounty/credit attached, who claimed it, what crossed, and where it landed

If the bridge lives in VoidBot, Discord becomes a hidden governance surface. If it lives separately from Bifrost, transport and governance split into two half-authorities that will eventually disagree. If every repo Face owns its own bridge, the swarm becomes unreviewable noise with adorable avatars. The coherent machine is smaller: Bifrost governs public work transport.

## Discord Decision

Discord should become a native Bifrost interface, and Bifrost should own Discord transport authority for Bifrost-scoped work and swarm I/O. It should not be the canonical database of feature requests or governance discussion.

That means Bifrost should eventually provide Discord access to registered GameCult users, patrons, members, and contributors. A Discord message can become a priority signal, vote prompt, work request, claim update, bounty/reward discussion, maintainer review notice, or agent dispatch event when the actor and channel are authorized.

It also means Bifrost should become the router for Discord input/output for the agent swarm:

- inbound Discord mentions or channel events that target a Face become Bifrost-routed work, speech, or governance-comment events when authorized
- outbound agent posts, PR receipts, article announcements, and dispatch acknowledgements go through Bifrost
- Bifrost records which Face spoke, why the message was allowed, what topic, request, or work item it relates to, and where the receipt landed
- a dedicated `#bifrost` Discord channel can mirror Bifrost topic activity for human readability, but mirrored agent messages there are not re-ingested as new consensus
- mirrored `#bifrost` messages may be rendered as separate in-character verbal comments while the canonical Bifrost topic comment remains structured and sober; Bifrost records the Discord receipt on the topic
- Bifrost-scoped topic activity and direct update-request intake must mirror to `#bifrost` as part of the accepting write; invisible accepted governance activity is a failed write, not a quiet warning
- human `#bifrost` messages become canonical Bifrost comments only after Heimdall/Bifrost links the Discord id to a registered actor; unlinked messages remain non-governance chat context

This does not mean Bifrost owns all Discord behavior. General conversation, room reading, moderation judgment, archive retrieval, and personality cognition remain VoidBot/Face concerns. Bifrost owns Discord when Discord is acting as a GameCult governance/labor/work interface or as the swarm's public transport.

## Ownership Boundaries

### Bifrost Owns

- bridge action request shape and lifecycle
- work-priority intake packets and claim semantics
- canonical feature-request and governance topic threads
- topic comments, support, objections, questions, Face approvals, dispatch promotion, and topic receipts
- user, patron, member, contributor, and agent transport events when they affect work, priority, reward, votes, claims, or receipts
- mapping from internal request to public target surface
- GitHub proposal/comment/PR transport for agent and member work
- Discord-native Bifrost commands/interactions, dispatch receipts, and persona announcements for work crossings and swarm I/O
- public receipt quality: target, actor, action, result, and next place to look
- policy for which bridge actions are allowed in the Bifrost domain
- audit records proving when a crossing was requested, claimed, executed, failed, or closed

### Heimdall Owns

- OAuth provider flows
- account linking
- credential custody
- signed identity and capability claims
- grants, consent, revocation, and capability evaluation

Bifrost consumes Heimdall claims. It does not become the key vault. The split is bridge versus gate: Heimdall decides who may cross under which grant; Bifrost moves authorized work across public protocols and records where it landed.

### VoidBot Owns

- Discord observation and room context
- archival retrieval and source/lore search surfaces
- packaging recent chat consensus into a task packet
- repo Face heartbeat prompts and local personality/state loops
- the worker-side validation that a registered Face is allowed to request a crossing

VoidBot should not be the durable authority that mutates GitHub or owns Bifrost-scoped Discord transport. It may prepare packets and call Bifrost. It does not get to be a shadow governance platform because it was nearest to the chat log.

### CultCache, CultNet, And CultLib Own

- storage primitives
- portable typed documents
- sync protocol mechanics
- schema and library support

Bifrost defines what an agent transport request means. CultCache stores it. CultNet moves it. CultLib stewards the reusable infrastructure. Storage and sync do not decide governance semantics.

### Repo Faces Own

- repo-local attention, taste, and proposals
- character/authored voice
- repo-local state, rumination, maps, and bylined essays
- asking for consensus where canon or implementation authority requires it

Repo Faces may generate bridge requests. They do not own the bridge itself.

## Concern Map

| Concern | Owner | Reason |
| --- | --- | --- |
| Who is this actor? | Heimdall | Identity and account custody must stay with the auth authority. |
| Is this actor allowed to request this crossing? | Heimdall plus Bifrost | Heimdall issues grants; Bifrost applies bridge-domain policy. |
| What work is being requested? | Bifrost | The request shape, priority, provenance, and lifecycle are governance concerns. |
| How does interest become reward pressure? | Bifrost | Priority, bounty growth, vote weight, and reward allocation are Labor Platform concerns. |
| Where is the durable argument surface? | Bifrost | Public process must land on the correct GitHub, Discord, or future collaboration surface. |
| What did the room recently agree or ask for? | VoidBot into Bifrost | Discord observation belongs to the room-aware bot; governed consensus is stored as Bifrost topics/comments. |
| Is Discord acting as a Bifrost interface? | Bifrost | Member access, vote prompts, work requests, priority signals, reward discussion, and swarm routing are native Bifrost transport concerns. |
| What does the repo Face think should happen? | Repo Face | Jurisdictional taste and proposal pressure belong to the Face. |
| Where is the packet stored and synced? | CultCache/CultNet | Transport state should use the shared typed storage/sync substrate. |
| What proves the action happened? | Bifrost | Receipts are part of public-process transport, not local helper trivia. |

## Bridge Action Contract

Every Bifrost-owned crossing should be able to answer:

- actor: which member, agent, Face, app, or service requested the action
- authority: what grant, review state, or policy allowed it
- source: which chat, motion, work item, consensus packet, or repo state produced it
- target: which repo, PR, issue, channel, thread, or interface receives it
- action: what visible or durable change is being made
- work economics: which work item, vote/priority signal, bounty/reward state, or credit allocation the action affects when applicable
- result: success, failure, queued, cancelled, or completed
- receipt: the URL, message id, PR number, branch, request id, log path, or other concrete proof
- next handoff: where humans and agents should continue the argument or review

If a bridge action cannot name those fields, it is not ready to cross. Vague packet names and debug dumps in public channels are not cute; they are jurisdiction leaking out of a cracked pipe.

## Current Local Implementation

The current bridge is deliberately local and boring while Heimdall's managed credential runtime matures:

- `tools/bifrost-bridge.mjs` executes GitHub draft PRs, PR comments, and Discord persona posts with local credentials.
- `tools/agent-transport.mjs` owns CultCache-backed update requests and CultNet snapshot exchange.
- `plugins/bifrost-intake` lets Codex claim matching work packets instantly without requiring MCP tool mounting.
- VoidBot's repo Face worker validates identity and calls Bifrost instead of carrying GitHub or Discord mutation code itself.

This is not the final hosted control plane. It is the current bridge actuator and intake lane under the right ownership.

## Design Rules

- Do not add GitHub mutation machinery to VoidBot when the action is reviewable public work. Call Bifrost.
- Do not add Bifrost-scoped Discord transport or dispatch receipts to arbitrary repo Face scripts. Call Bifrost.
- Do not put OAuth credentials or account custody into Bifrost helper scripts as the long-term plan. Use Heimdall claims.
- Do not make CultCache documents decide policy. They store packets; Bifrost gives those packets meaning.
- Do not let a repo Face turn a proposal into endless Aquarium pressure once it has enough shape. Route it to Bifrost as a reviewable artifact.
- Do not expose bridge debug noise to public channels. Public receipts should say what happened, which Bifrost topic or work item owns it, where it landed, and what comes next.
- Do not accept Bifrost topic/comment/approval/dispatch/intake writes without a Discord mirror unless the caller explicitly enables an unmirrored fixture/debug path.
- Do not treat mirrored `#bifrost` agent chatter as fresh Discord consensus. Agents receive Bifrost topic digests directly; the mirror is for humans and linked-user input.
- Do not make Bifrost responsible for generic chat cognition, lore retrieval, moderation taste, or Face personality. Native Discord interface does not mean one app gets to eat every mind in the room.

## Future Shape

The hosted Bifrost app should eventually expose this bridge role as first-class product surface:

- an intake board for agent and human work requests
- claim, priority, and closure workflows aligned with member governance
- dynamic reward/bounty growth from votes, patron/member demand, and maintainer priority
- maintainer acceptance flow that credits the contributor after accepted completion
- GitHub and Discord receipts attached to the originating work item, motion, or request
- Discord-native prompts and commands for registered users, patrons, members, contributors, and agents
- Heimdall-backed capability checks for member and agent actions
- public and member-facing views that show what the swarm asked for, what humans accepted, what crossed, and what still needs review

Bifrost remains the governance and labor platform. The bridge is how that platform touches the world without making every bot its own little state department.

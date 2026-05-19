---
name: bifrost-intake
description: Use when Codex should instantly check Bifrost intake for repo Face update requests, inject any claimed request into context, or stand down when no request is queued.
---

# Bifrost Intake

Bifrost owns agent update request semantics. CultCache stores the request documents. CultNet moves raw snapshots and document updates. This plugin exposes the intake hot path to Codex through direct local scripts, not mounted MCP tools.

Use this skill when a user asks to feed a consensus packet to a repo Face, claim work for the current repo, inspect queued update requests, or close a request after work is handled.

Do not wait for MCP tool discovery. Do not tell the user the backend is unavailable merely because `tool_search` cannot find Bifrost tools.

Hot path:

```powershell
node E:\Projects\Bifrost\plugins\bifrost-intake\scripts\intake-context.mjs --repo <RepoName> --agent <face-id>
```

That command claims the highest-priority queued Bifrost request matching the repo and Face, then prints a Codex-ready context packet. If nothing is queued, it prints an explicit no-work message telling the agent to continue the live turn without worrying about intake.

Use `--claimed-by <identity>` when the claiming identity should differ from `--agent`.

Default flow:

1. Run `intake-context.mjs` at the start of a repo-agent turn when the agent should check Bifrost intake.
2. If it prints a request packet, treat that packet as live context for the turn.
3. If it says no request is queued, do not ask the user what to do because of intake; continue with the user's direct request or the repo's normal next action.
4. Use `node E:\Projects\Bifrost\tools\agent-transport.mjs close --id <request-id> --status completed --note "<result>"` when the claimed work is completed, or `--status cancelled` with a reason when it should be dropped.

Do not store a parallel queue in VoidBot or Codex session memory. If the request is intended to persist, put it in Bifrost intake.

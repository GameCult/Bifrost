---
name: bifrost-intake
description: Use when Codex should enqueue, list, claim, close, or package Bifrost agent update requests for repo Faces through the Bifrost intake MCP tools.
---

# Bifrost Intake

Bifrost owns agent update request semantics. CultCache stores the request documents. CultNet moves raw snapshots and document updates. This plugin exposes that lane to Codex through MCP tools.

Use these tools when a user asks to feed a consensus packet to a repo Face, claim work for the current repo, inspect queued update requests, or close a request after work is handled.

Default flow:

1. Use `get_intake_context` at the start of a repo-agent turn when the agent should check Bifrost intake.
2. If it returns a request packet, treat that packet as live context for the turn.
3. If it says no request is queued, do not ask the user what to do because of intake; continue with the user's direct request or the repo's normal next action.
4. Use `close_update_request` when the claimed work is completed or cancelled.

Do not store a parallel queue in VoidBot or Codex session memory. If the request is intended to persist, put it in Bifrost intake.

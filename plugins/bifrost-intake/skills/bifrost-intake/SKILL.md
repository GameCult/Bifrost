---
name: bifrost-intake
description: Use when Codex should enqueue, list, claim, close, or package Bifrost agent update requests for repo Faces through the Bifrost intake MCP tools.
---

# Bifrost Intake

Bifrost owns agent update request semantics. CultCache stores the request documents. CultNet moves raw snapshots and document updates. This plugin exposes that lane to Codex through MCP tools.

Use these tools when a user asks to feed a consensus packet to a repo Face, claim work for the current repo, inspect queued update requests, or close a request after work is handled.

Default flow:

1. Use `list_update_requests` to inspect queued work for a repo or Face.
2. Use `claim_update_request` with the repo name before injecting the request into Codex.
3. Use `format_claimed_request` when you need a compact prompt packet for a claimed request.
4. Use `close_update_request` when the work is completed or cancelled.

Do not store a parallel queue in VoidBot or Codex session memory. If the request is intended to persist, put it in Bifrost intake.

# Agent guidance rollout for code-gate plus den-publish (#1434)

> **Historical/specialized posture (May 2026):** this document records the
> rollout era when code-gate / `den-publish` was being made prominent as a
> default cross-machine promotion path. Current ordinary trusted Runner work uses
> review-first direct non-force git promotion after Den review. Start with
> `docs/current-posture.md`, `_global/agent-code-promotion-policy`, and
> `den-core/den-post-split-forward-path-vs-history-2026-05` before treating this
> runbook as active guidance.

## Purpose

This rollout originally made the cross-machine promotion path prominent enough
that future runner/orchestrator agents did not fall back to the then-stale Den
Core Trusted Publisher tools, worker-local checkouts, `/data/dev`,
reviewed-bundle shims, or hand-authored direct API payloads. Preserve it as
historical/specialized code-gate evidence, not as the current default for normal
trusted local Runner work.

## Global guidance

New required global policy:

```text
_global/agent-code-promotion-policy
```

Guidance entry:

- importance: required
- audience: all
- historical rollout path: `worker -> den-code-gate -> Den review -> Den Core den-publish facade -> den-publish /promotion/dry-run -> approval-gated /promotion/publish`
- current ordinary trusted Runner default: local task branch -> tests/build -> Den review `looks_good` -> direct non-force git promotion -> Den evidence packet

The shared policy index also points at this document.

## Project guidance

Repo docs/templates in this branch reinforce the same rule:

- `docs/agent-workflow-ux.md`
- `templates/agent-workflow/agent-context-packet.template.md`
- `examples/agent-workflow/den-channels-dry-run-context.example.md`
- `scripts/render-agent-context-packet.py`
- `scripts/validate-agent-workflow-ux.py`

## Legacy compatibility line

Agents should treat Den Core `publish_reviewed_branch` as legacy/compatibility for this class of work, not as the default promotion path for new cross-machine development. `publish_worker_branch` is likewise legacy/compatibility for new code-gate work.

Legacy mechanisms such as `/data/dev`, `/mnt/den-srv/dev`, reviewed-bundle imports, and worker-local checkout paths are diagnostic/compatibility only and must not be introduced as per-project shims.

## Required synchronization line

All promotion-aware packets should include:

```text
submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id or pending> target=<target_branch>
```

The line helps humans and agents compare the coder packet, reviewer packet, Den review round, and orchestrator decision without opening raw JSON.

## Direct API casing boundary

`DenPublish.Api` direct `/promotion/*` payloads use camelCase fields such as:

- `workspacePath`
- `allowedPathPrefixes`
- `decision.validateOnly`
- `decision.expectedHeadCommit`
- `submission.headCommit`
- `submission.ingressRef`

Den-native messages may use snake_case when they are not sent directly to the API. Normal orchestrators should prefer the Den Core field-based facade so Core constructs the camelCase service payload internally.

## Fresh agent-context packet proof

Generate a packet from current metadata:

```bash
python3 scripts/render-agent-context-packet.py \
  --project den-channels \
  --task-id 1416 \
  --run-id run-example-1434 \
  --submission-id sub_example_1434 \
  --head 1111111111111111111111111111111111111111 \
  --base 0000000000000000000000000000000000000000 \
  --review-round pending \
  --target-branch task/1416-example
```

The generated example is stored at:

```text
examples/agent-workflow/den-channels-dry-run-context.example.md
```

It includes the standard workflow, metadata drift-check command, exact immutable ingress ref, sync line, reviewer instructions, and orchestrator instruction to use the Den Core dry-run facade.

## Validation

Run:

```bash
python3 scripts/validate-agent-workflow-ux.py
python3 scripts/check-promotion-metadata-drift.py --project den-channels
python3 scripts/render-agent-context-packet.py --project den-channels --task-id 1416 >/tmp/agent-context.md
python3 - <<'PY'
from pathlib import Path
text = Path('/tmp/agent-context.md').read_text()
for term in ['submission=', 'ingress_ref=', 'Den Core den-publish facade', 'camelCase']:
    assert term in text, term
PY
dotnet test DenPublish.slnx
```

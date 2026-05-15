# Agent context packet: code-gate -> den-publish dry-run

Use this packet when assigning or waking a coder/reviewer/orchestrator for a promotion-aware Den task. It is generated from the promotion project inventory, then filled with task-local submission/review values.

## Promotion policy

- Default workflow: `worker -> den-code-gate -> Den review -> Den Core den-publish facade -> den-publish /promotion/dry-run`.
- Legacy Den Core `publish_reviewed_branch` / `publish_worker_branch` are compatibility only for this class of work.
- Do not use `/data/dev`, `/mnt/den-srv/dev`, reviewed-bundle imports, or worker-local checkout paths as the standard promotion route.
- Do not give coder/reviewer workers canonical push credentials.
- Direct `DenPublish.Api` JSON uses camelCase only; Den-native packets may use snake_case. Prefer the Den Core field-based facade when available.

## Project readiness

- project_id: `$project_id`
- metadata_status: `$status`
- canonical_remote_url: `$canonical_remote_url`
- code_gate_remote_url: `$code_gate_remote_url`
- target_remote_name: `$target_remote_name`
- default_base_branch: `$default_base_branch`
- allowed_path_prefixes: `$allowed_path_prefixes`

Run before dry-run if readiness is uncertain:

```bash
python3 scripts/check-promotion-metadata-drift.py --project $project_id
```

## Task-local synchronization line

```text
$sync_line
```

## Submission fields to fill

- task_id: `$task_id`
- submission_id: `$submission_id`
- worker_run_id: `$run_id`
- ingress_ref: `$ingress_ref`
- convenience_ref: `$convenience_ref`
- base_commit: `$base_commit`
- head_commit: `$head_commit`
- target_branch: `$target_branch`
- review_round_id: `$review_round`

## Reviewer instruction

Fetch only `ingress_ref` from `code_gate_remote_url`, verify the fetched commit equals `head_commit`, review against `base_commit`, then post Den findings/verdict bound to that exact submission/head.

## Orchestrator instruction

After a matching `looks_good` review round exists, call the Den Core `den-publish` dry-run facade with field-level parameters. Do not hand-author raw `DenPublish.Api` JSON unless the facade is unavailable and the task explicitly allows direct service-boundary diagnostics.

Live `/promotion/publish` remains disabled unless a separate scoped approval window is granted.

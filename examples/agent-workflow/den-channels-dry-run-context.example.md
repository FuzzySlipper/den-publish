# Agent context packet: code-gate -> den-publish dry-run

Use this packet when assigning or waking a coder/reviewer/orchestrator for a promotion-aware Den task. It is generated from the promotion project inventory, then filled with task-local submission/review values.

## Promotion policy

- Default workflow: `worker -> den-code-gate -> Den review -> Den Core den-publish facade -> den-publish /promotion/dry-run`.
- Legacy Den Core `publish_reviewed_branch` / `publish_worker_branch` are compatibility only for this class of work.
- Do not use `/data/dev`, `/mnt/den-srv/dev`, reviewed-bundle imports, or worker-local checkout paths as the standard promotion route.
- Do not give coder/reviewer workers canonical push credentials.
- Direct `DenPublish.Api` JSON uses camelCase only; Den-native packets may use snake_case. Prefer the Den Core field-based facade when available.

## Project readiness

- project_id: `den-channels`
- metadata_status: `dry_run_ready`
- canonical_remote_url: `git@github.com:FuzzySlipper/den-channels.git`
- code_gate_remote_url: `ssh://git@192.168.1.10:3022/den-channels/den-channels.git`
- target_remote_name: `canonical`
- default_base_branch: `main`
- allowed_path_prefixes: `<none configured>`

Run before dry-run if readiness is uncertain:

```bash
python3 scripts/check-promotion-metadata-drift.py --project den-channels
```

## Task-local synchronization line

```text
submission=sub_example_1434 ingress_ref=refs/heads/submissions/den-channels/tasks/1416/runs/run-example-1434/attempt-001 head=1111111111111111111111111111111111111111 base=0000000000000000000000000000000000000000 review_round=pending target=task/1416-example
```

## Submission fields to fill

- task_id: `1416`
- submission_id: `sub_example_1434`
- worker_run_id: `run-example-1434`
- ingress_ref: `refs/heads/submissions/den-channels/tasks/1416/runs/run-example-1434/attempt-001`
- convenience_ref: `refs/heads/submissions/den-channels/tasks/1416/current`
- base_commit: `0000000000000000000000000000000000000000`
- head_commit: `1111111111111111111111111111111111111111`
- target_branch: `task/1416-example`
- review_round_id: `pending`

## Reviewer instruction

Fetch only `ingress_ref` from `code_gate_remote_url`, verify the fetched commit equals `head_commit`, review against `base_commit`, then post Den findings/verdict bound to that exact submission/head.

## Orchestrator instruction

After a matching `looks_good` review round exists, call the Den Core `den-publish` dry-run facade with field-level parameters. Do not hand-author raw `DenPublish.Api` JSON unless the facade is unavailable and the task explicitly allows direct service-boundary diagnostics.

Live `/promotion/publish` remains disabled unless a separate scoped approval window is granted.


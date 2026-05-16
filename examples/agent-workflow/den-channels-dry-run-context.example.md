# Agent context packet: code-gate -> den-publish dry-run

Use this packet when assigning or waking a coder/reviewer/orchestrator for a promotion-aware Den task. It is generated from the promotion project inventory, then filled with task-local submission/review values. Packet generation is fail-closed: missing, duplicate, or not-onboarded project metadata must be fixed before a worker starts.

## Promotion policy

- Default workflow: `worker -> den-code-gate -> Den review -> Den Core den-publish facade -> den-publish /promotion/dry-run`.
- Legacy Den Core `publish_reviewed_branch` / `publish_worker_branch` are compatibility only for this class of work.
- Do not use `/data/dev`, `/mnt/den-srv/dev`, reviewed-bundle imports, or worker-local checkout paths as the standard promotion route.
- Do not give coder/reviewer workers canonical push credentials.
- Den-native packets use snake_case; Direct `DenPublish.Api` JSON uses camelCase only. Prefer the Den Core field-based facade when available.

## Project readiness

- project_id: `den-channels`
- metadata_status: `dry_run_ready`
- canonical_remote_url: `git@github.com:FuzzySlipper/den-channels.git`
- code_gate_instance: `den-code-gate`
- code_gate_repo: `den-channels/den-channels.git`
- code_gate_remote_url: `ssh://git@192.168.1.10:3022/den-channels/den-channels.git`
- target_remote_name: `canonical`
- default_base_branch: `main`
- allowed_path_prefixes: `<none configured>`

Run before dry-run if readiness is uncertain:

```bash
python3 scripts/check-promotion-metadata-drift.py --project den-channels
python3 scripts/check-project-promotion-readiness.py --project den-channels --json
```

## Task-local synchronization line

```text
submission=sub_example_1434 ingress_ref=refs/heads/submissions/den-channels/tasks/1416/runs/run-example-1434/attempt-001 head=1111111111111111111111111111111111111111 base=0000000000000000000000000000000000000000 review_round=pending target=task/1416-example
```

Every coder, reviewer, and orchestrator packet/comment must preserve this exact line or an updated line with the same fields after each retry:

```text
submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id or pending> target=<target_branch>
```

## Submission fields to fill

- task_id: `1416`
- submission_id: `sub_example_1434`
- worker_run_id: `run-example-1434`
- attempt_ordinal: `001`
- ingress_ref: `refs/heads/submissions/den-channels/tasks/1416/runs/run-example-1434/attempt-001`
- convenience_ref: `refs/heads/submissions/den-channels/tasks/1416/current`
- base_commit: `0000000000000000000000000000000000000000`
- head_commit: `1111111111111111111111111111111111111111`
- target_branch: `task/1416-example`
- review_round_id: `pending`

## Coder instruction

1. Push only to the code-gate remote/ref convention above: `code_gate_remote_url` -> `ingress_ref`.
2. Do not push to the canonical remote or request canonical credentials.
3. Base work on `base_commit`; report the final `head_commit` after pushing `ingress_ref`.
4. Include `target_branch` so the orchestrator can validate the intended canonical branch without guessing.
5. Include `changed_files_claim` as a JSON array of repository-relative paths touched by the task.
6. Include `tests_run` as a JSON array of `<command>: <result>` evidence.
7. Include the task-local synchronization line in the coder completion packet.

## Reviewer instruction

1. Fetch only `ingress_ref` from `code_gate_remote_url`.
2. Fetch `ingress_ref`, then verify the fetched commit equals `head_commit` before reviewing.
3. Review the fetched object against `base_commit`; do not review `current` refs or worker-local checkouts as authority.
4. Compare observed changed files with `changed_files_claim`.
5. Post Den findings/verdict bound to this exact `submission_id`, `review_round_id`, and `head_commit`.
6. Include the task-local synchronization line in the reviewer findings packet.

## Orchestrator instruction

After a matching `looks_good` review round exists, call Den Core `request_den_publish_dry_run` through the MCP/Core facade with field-level Den-native parameters. Do not hand-author raw `DenPublish.Api` JSON unless the facade is unavailable and the task explicitly allows direct service-boundary diagnostics.

Facade fields are Den-native snake_case, including `project_id`, `task_id`, `submission_id`, `ingress_ref`, `head_commit`, `base_commit`, `target_branch`, and `review_round_id`. Direct `DenPublish.Api` examples remain camelCase at that service boundary.

Live `/promotion/publish` remains disabled unless a separate scoped approval window is granted.


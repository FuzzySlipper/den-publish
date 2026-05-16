# Agent context packet: code-gate -> den-publish dry-run

Use this packet when assigning or waking a coder/reviewer/orchestrator for a promotion-aware Den task. It is generated from the promotion project inventory, then filled with task-local submission/review values. Packet generation is fail-closed: missing, duplicate, or not-onboarded project metadata must be fixed before a worker starts.

## Promotion policy

- Default workflow: `worker -> den-code-gate -> Den review -> Den Core den-publish facade -> den-publish /promotion/dry-run`.
- Legacy Den Core `publish_reviewed_branch` / `publish_worker_branch` are compatibility only for this class of work.
- Do not use `/data/dev`, `/mnt/den-srv/dev`, reviewed-bundle imports, or worker-local checkout paths as the standard promotion route.
- Do not give coder/reviewer workers canonical push credentials.
- Den-native packets use snake_case; Direct `DenPublish.Api` JSON uses camelCase only. Prefer the Den Core field-based facade when available.

## Project readiness

- project_id: `$project_id`
- metadata_status: `$status`
- canonical_remote_url: `$canonical_remote_url`
- code_gate_instance: `$code_gate_instance`
- code_gate_repo: `$code_gate_repo`
- code_gate_remote_url: `$code_gate_remote_url`
- target_remote_name: `$target_remote_name`
- default_base_branch: `$default_base_branch`
- allowed_path_prefixes: `$allowed_path_prefixes`

Run before dry-run if readiness is uncertain:

```bash
python3 scripts/check-promotion-metadata-drift.py --project $project_id
python3 scripts/check-project-promotion-readiness.py --project $project_id --json
```

## Task-local synchronization line

```text
$sync_line
```

Every coder, reviewer, and orchestrator packet/comment must preserve this exact line or an updated line with the same fields after each retry:

```text
submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id or pending> target=<target_branch>
```

## Submission fields to fill

- task_id: `$task_id`
- submission_id: `$submission_id`
- worker_run_id: `$run_id`
- attempt_ordinal: `$attempt_ordinal`
- ingress_ref: `$ingress_ref`
- convenience_ref: `$convenience_ref`
- base_commit: `$base_commit`
- head_commit: `$head_commit`
- target_branch: `$target_branch`
- review_round_id: `$review_round`

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

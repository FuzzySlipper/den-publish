# Agent workflow UX for code-gate submissions

This document is the operator/agent runbook for Den task #1425. It turns the #1420/#1423/#1424 contracts into packet shapes that coder, reviewer, and orchestrator agents can follow without raw promotion Git commands.

Source-of-truth documents:

- Den workflow contract: `den-publish/den-code-gate-den-publish-workflow-contract-1420`
- Code-gate deployment result: `den-publish/den-code-gate-forgejo-deployment-result-1422`
- Worker substrate policy: `_global/agent-worker-substrate-policy`
- Review loop policy: `_global/agent-review-loop-policy`
- Code promotion policy: `_global/agent-code-promotion-policy`
- Project metadata inventory: `den-publish/promotion-project-metadata-1433`

## Safety boundary

- Coder workers may push candidate commits to `den-code-gate` only.
- Reviewer workers fetch exact immutable submission refs from `den-code-gate` and never need canonical/GitHub push credentials.
- Orchestrators approve high-level Den decisions; they do not type or execute raw `git push` promotion commands.
- Orchestrators should use the Den Core field-based `den-publish` dry-run facade when available; they should not hand-author raw `DenPublish.Api` JSON for normal workflow execution.
- `den-publish` is the only component that validates and performs canonical promotion.
- Live publishing remains an explicit operator gate. The real repo/task prep flow should use `/promotion/dry-run` until a concrete publish approval says otherwise.

## Real repo/task test readiness checklist

Before selecting a real project task for #1427 or later E2E smoke, confirm:

1. The project has a canonical remote URL accepted by `den-publish` target policy.
2. The project has secret-free promotion metadata in `config/promotion-projects.json`; run `python3 scripts/check-promotion-metadata-drift.py --project <project_id>` before dry-run if readiness is uncertain.
3. `den-code-gate` has or can create a repository for the project.
4. The coder can report a complete `den_code_submission` packet with exact `base_commit`, `head_commit`, immutable `ingress_ref`, tests, and changed-file claim.
5. The reviewer context packet names the same `submission_id`, `ingress_ref`, `head_commit`, and `base_commit`.
6. The Den review round records or references the exact reviewed `head_commit`.
7. The orchestrator decision packet names the exact reviewed `head_commit`, `review_round_id`, and structured `scope_overrides[]` when any blocking finding is intentionally covered.
8. `/promotion/dry-run` succeeds before `/promotion/publish` is considered.
9. Live `/promotion/publish` is enabled only for the scoped smoke window and disabled afterward.

## Coder completion packet

Template: `templates/agent-workflow/coder-completion-packet.template.json`

A coder completion packet must include the code-gate location, immutable ingress ref, base/head commits, tests, changed-file claim, and intended canonical target. It should be posted to Den as the implementation evidence for the task or as the payload to a future first-class Den submission tool.

The coder packet is not publish authority. It is a claim that Den/reviewer/`den-publish` must validate.

Important rules:

- `ingress_ref` must be an immutable attempt ref like `refs/heads/submissions/{project_id}/tasks/{task_id}/runs/{worker_run_id}/attempt-001`.
- Follow-up work must create a new attempt ordinal and a new `submission_id`.
- `convenience_ref` may be updated for browsing but must not be used as publish authority.
- `changed_files_claim` must list repository-relative paths only.

## Reviewer context packet

Template: `templates/agent-workflow/reviewer-context-packet.template.md`

Promotion-aware wake/context packet template: `templates/agent-workflow/agent-context-packet.template.md`

Generate a project-specific packet with:

```bash
python3 scripts/render-agent-context-packet.py --project den-channels --task-id <task-id>
```

The reviewer packet must tell the reviewer to fetch the exact immutable `ingress_ref` and verify the fetched SHA equals `head_commit` before reviewing the diff. A reviewer verdict is only applicable to the named `submission_id` and `head_commit`.

Reviewer output must include:

- `review_round_id`
- reviewed `submission_id`
- reviewed `head_commit`
- verdict (`looks_good`, `changes_requested`, `follow_up_needed`, or `blocked_by_dependency`)
- structured findings with blocking/resolved status and optional `override_id`

## Rework / rereview loop

A follow-up commit after review comments is a new immutable attempt:

1. Coder creates `sub_<task>_<attempt>` with `attempt_ordinal = previous + 1`.
2. `parent_submission_id` points at the prior submission.
3. Prior positive reviews do not carry forward automatically.
4. Orchestrator requests a new review round bound to the new `head_commit`.
5. Superseded submissions should not be used for publish decisions.

Template: `templates/agent-workflow/rework-rereview-packet.template.md`

## Orchestrator decision packet

Template: `templates/agent-workflow/orchestrator-publish-decision.template.json`

The orchestrator decision template is the live `DenPublish.Api` request payload, so it must use camelCase JSON keys even when it was assembled from Den/human packets that use snake_case. It must bind exactly:

- `submissionId`
- `expectedHeadCommit`
- `reviewRoundId`
- `targetBranch`
- `scopeOverrideIds[]`
- `scopeOverrides[]` entries with `overrideId`, `reason`, and `approvedBy`

For prep and first real repo/task tests, set `validateOnly: true` and call `/promotion/dry-run`. Only use `validateOnly: false` with `/promotion/publish` after an explicit live-gate approval.

## Human-readable synchronization line

Every task-thread status packet should include a compact line like:

```text
submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id or pending> target=<target_branch>
```

This line keeps humans, agents, and Den state synchronized without requiring them to inspect raw JSON.

## Failure handling

Fail closed and request rework or operator intervention when:

- any packet omits `submission_id`, `ingress_ref`, `base_commit`, or `head_commit`;
- reviewer fetched head does not match packet `head_commit`;
- decision head does not match reviewed head;
- review verdict is absent or not `looks_good` for publish;
- blocking findings are unresolved and not covered by a structured override reason;
- `/promotion/dry-run` reports any validation failure;
- live publish is enabled outside a scoped, documented approval window.

## Global guidance rollout (#1434)

The authoritative global promotion guidance is `_global/agent-code-promotion-policy`. Agents should treat Den Core `publish_reviewed_branch` and `publish_worker_branch` as legacy/compatibility for new cross-machine development. Normal new work should produce code-gate submission packets, review exact immutable refs, then use the Den Core field-based den-publish dry-run facade before any approval-gated live publish.

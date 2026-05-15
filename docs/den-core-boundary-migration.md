# Den Core to den-publish publisher boundary migration

This document records the task #1426 compatibility and migration plan. It deliberately does **not** remove Den Core code yet. The next implementation steps should preserve the reviewed #1417 Den Core behavior until the #1427 real repo/task dry-run proves the new `den-code-gate` -> `den-publish` path end-to-end.

## Intent

Make publishing work from any approved dev/worker machine without project-local shims by moving Git promotion execution to `den-publish`, while keeping Den Core as the coordination and source-of-truth layer.

The desired steady state is:

- workers push candidate commits only to `den-code-gate`;
- reviewers fetch immutable code-gate refs and approve exact SHAs in Den;
- orchestrators create structured publish decisions in Den;
- `den-publish` validates/fetches/promotes the exact approved submission from its own service-owned workspace;
- Den Core exposes state, authorization, MCP/operator facade, and audit records without owning low-level Git workspace/push mechanics.

## Current Den Core Trusted Publisher inventory

Current Den Core implementation lives in `/home/dev/den-core`:

- MCP tools: `src/DenMcp.Server/Tools/TrustedPublisherTools.cs`
  - `publish_worker_branch`
  - `publish_reviewed_branch`
- implementation: `src/DenMcp.Core/Services/TrustedPublisherService.cs`
- tests: `tests/DenMcp.Server.Tests/TrustedPublisherServiceTests.cs`
- default config: `DenMcp:TrustedPublisher` in `src/DenMcp.Server/appsettings.json`

### Responsibilities Den Core currently performs

| Area | Current behavior | Steady-state owner |
|---|---|---|
| MCP facade | Exposes `publish_worker_branch` and `publish_reviewed_branch` tools. | Den Core remains the facade, but tools should become compatibility wrappers / decision submitters. |
| Request authorization | Checks `AllowedOrchestrators`, safe task branches, target branch allowlist, safe remote name. | Split: Den Core authorizes Den caller/identity; `den-publish` validates target policy. |
| Den review state | Resolves review round, reviewed branch/head/base, verdict `looks_good`, unresolved blocking findings. | Den Core remains source of truth and should materialize a `den_publish_decision` + `den_code_submission` packet. |
| Worker provenance | `publish_worker_branch` resolves Pi session/completion packet and local worker workspace. | Deprecated for new work. New path uses code-gate submissions and spawned-Hermes/Den packets instead of worker filesystem paths. |
| Project root resolution | Uses Den project root plus `ProjectRootSearchPaths`, auto-creates `/data/services/den-core/git/<root>` workspaces, falls back to `/data/dev` and `/mnt/den-srv/dev`. | Moves to `den-publish` workspace derivation under `DenPublish:WorkspaceRoot`; Den project root remains metadata only. |
| Reviewed object import | Fetches reviewed branch from canonical remote, imports a reviewed git bundle from configured roots, or materializes an existing commit object. | Moves to code-gate fetch in `den-publish` (`submission.ingress_ref` -> managed local ref). Git bundle path should not be the normal new workflow. |
| Canonical remote comparison | Resolves/normalizes remote URL from project root or request. | Den Core supplies canonical URL from project metadata/decision; `den-publish` enforces `TargetPolicy.CanonicalRemoteUrl` / selected repo policy. |
| Scope validation | Worker mode can diff changed files against allowed prefixes. | `den-publish` owns changed-file validation from fetched code-gate ref + `ChangedFileScopePolicy`. |
| Fast-forward safety | Verifies remote base descends from reviewed base before `fast_forward_main`. | `den-publish` should own final ancestry/fast-forward checks before push. |
| Git push | Executes `git push` from Den Core service context. | Moves to `den-publish` only, with explicit credential policy and no ambient credentials. |
| Audit | Posts `trusted_publisher_audit` task messages. | Den Core remains Den audit/source-of-truth. `den-publish` writes service audit JSONL; Den Core records summarized promotion decision/result messages. |

## Current den-publish boundary inventory

Current `den-publish` implementation lives in `/home/dev/den-publish`:

- API endpoints: `src/DenPublish.Api/PromotionValidationEndpoints.cs`
  - `POST /promotion/validate`
  - `POST /promotion/dry-run`
  - `POST /promotion/publish`
- validation pipeline: `src/DenPublish.Core/PromotionValidationWorkflow.cs`
- preflight contract checks: `src/DenPublish.Core/PublishValidationEngine.cs`
- target policy wrapper: `PublishPolicyValidationEngine`
- code-gate ref validation/fetch: `CodeGateRefVerifier`, `GitSubmissionFetcher`
- changed-file/ancestry validation: `GitChangedFileScopeValidator`, `GitSubmissionAncestryValidator`
- dry-run publisher: `DryRunPromotionPublisher`
- live publisher: `GitPromotionPublisher`
- workspace derivation: `src/DenPublish.Api/WorkspacePathResolver.cs`
- runtime status: `/config/status`

### Responsibilities den-publish should own

| Area | Required behavior |
|---|---|
| Managed workspace | Derive `<WorkspaceRoot>/<ProjectId>/tasks/<TaskId>/submissions/<SubmissionId>` and ignore caller-provided paths when `WorkspaceRoot` is configured. |
| Code-gate source | Fetch only the declared immutable `submission.ingress_ref` and require fetched SHA to equal `submission.head_commit` / decision `expected_head_commit`. |
| Target policy | Enforce canonical remote URL, logical target remote name, allowed push branch prefixes, and allowed fast-forward branches. |
| Scope validation | Diff changed files against allowed policy and structured overrides. |
| Ancestry validation | Confirm fetched submission is consistent with the declared base/canonical target before promotion. |
| Dry-run | Run all validation and simulated publish without credentials or canonical push. |
| Live push | Require `/promotion/publish`, `decision.validate_only=false`, enabled service config, and explicit credential policy. Use only child-process environment (`GIT_SSH_COMMAND`, `GIT_TERMINAL_PROMPT=0`). |
| Fail-closed diagnostics | Return structured failures for missing submission, review mismatch, code-gate fetch mismatch, target policy mismatch, credential unavailable, and Git push failures. |
| Service audit | Append JSONL audit records for validation and publish attempts without leaking secrets. |

## Compatibility bridge

Until Den Core has first-class `den-publish` MCP tools, the compatibility bridge should be explicit and fail-closed:

1. Keep the old Den Core `publish_reviewed_branch` available for legacy reviewed branches, but treat it as deprecated for new agent work.
2. Do **not** unblock old `den-channels` tasks by asking them to retry the Den Core publish path.
3. For new work, require a code-gate submission packet with:
   - `submission_id`
   - immutable `ingress_ref`
   - `base_commit`
   - `head_commit`
   - canonical remote URL
   - changed-file claim/tests
4. Require a Den review round or review packet bound to the same `submission_id` and `head_commit`.
5. Materialize a `den_publish_decision` packet from Den state and call `den-publish /promotion/dry-run` first.
6. Only call `/promotion/publish` after a separate live-gate approval names repo, branch, expected head, credential mode, rollback, and audit expectations.
7. Den Core records the decision/result in Den task messages; `den-publish` records service JSONL audit.

## Mapping old tasks to the new architecture

### #1417 — managed service-owned workspaces across projects

Status: implemented in Den Core and still useful as a compatibility safety net, but the strategic owner changes.

| #1417 acceptance criterion | New mapping |
|---|---|
| Den Core owns workspace lifecycle under `/data/services/den-core/git`. | Superseded for new flow by `den-publish` `DenPublish:WorkspaceRoot` under `/home/agents/runtime/den-publish/workspaces`. Den Core may keep its managed workspace for legacy compatibility only. |
| No manual per-project mkdir/clone/bundle staging. | Satisfied by `den-publish` workspace derivation from decision fields and code-gate fetch; no `/data/dev` checkout required. |
| Work from another machine can publish if Den can obtain exact reviewed commit/object. | Satisfied by immutable code-gate refs (`ingress_ref`) instead of local worker paths or reviewed bundles. |
| Fail closed on provenance/remote/branch/review ambiguity. | Split: Den Core validates Den review/identity; `den-publish` validates submission, code-gate ref, scope, target policy, and push readiness. |
| Tests for workspace/create/import/push paths. | Retain Den Core tests for legacy compatibility; new required tests live in `den-publish` endpoint/workflow/publisher suites. |

Decision: do not remove #1417 code before #1427 succeeds. After #1427, open a cleanup task to either (a) convert Den Core tools into wrappers around `den-publish`, or (b) mark the Den Core tool names legacy and add first-class `den_publish_*` tools.

### #1418 — service-side GitHub push credentials for Den Core

Status: blocked/deferred by the `den-publish` architecture.

New mapping:

- Do not provision GitHub push credentials for Den Core.
- Credential work belongs to `den-publish` live publisher only.
- Use the #1424/#1425 runbook pattern: `DenPublish:Publishing:Enabled`, `CredentialMode=ssh_command`, `GitSshCommand=<redacted>`, hardened with `ssh -F /dev/null`, `IdentitiesOnly=yes`, `BatchMode=yes`, pinned known_hosts, and `GIT_TERMINAL_PROMPT=0`.
- Keep live publishing disabled by default and scoped to an operator-approved smoke or release window.

Decision: #1418 should remain blocked/deferred and later be superseded by a narrower `den-publish` credential-ops task if persistent live publishing becomes necessary.

## Migration phases

### Phase 0 — current safe state

- `den-publish.service` active on `127.0.0.1:5090`.
- `DenPublish:WorkspaceRoot=/home/agents/runtime/den-publish/workspaces`.
- live publishing disabled.
- Den Core #1417 branch still under review/legacy compatibility.
- New work should not use Den Core `publish_reviewed_branch` for canonical promotion.

### Phase 1 — #1427 dry-run rehearsal

Run a real low-risk task through the new packet flow:

1. create/update code on a task branch;
2. push candidate to `den-code-gate` immutable submission ref;
3. post coder submission packet in Den;
4. reviewer fetches exact code-gate ref and records verdict/head;
5. orchestrator builds decision packet with `validate_only=true`;
6. call `POST /promotion/dry-run`;
7. document exact repo/ref/SHA/audit result.

Success here is enough to tell blocked project agents to use the new structure on fresh work. It is **not** yet approval for `/promotion/publish`.

### Phase 2 — Den Core facade integration

Add a Den Core compatibility layer that transforms Den task/review/submission state into `den-publish` API calls:

- new preferred MCP tools, for example:
  - `prepare_den_publish_decision`
  - `validate_den_publish_decision`
  - `publish_den_publish_decision`
- or a compatibility mode in existing tools that routes to `den-publish` when a `submission_id` is present.

Required Den Core behavior:

- read Den task/review/submission state;
- reject stale/mismatched heads before calling `den-publish`;
- call `/promotion/dry-run` for validate-only;
- require separate approval metadata before `/promotion/publish`;
- record Den audit messages with `den_publish_decision_id`, endpoint, status, and service audit pointer.

### Phase 3 — legacy deprecation

After the new facade works for at least one real project:

- mark `publish_worker_branch` legacy/unsupported for new work;
- mark direct Den Core `git push` path legacy;
- keep old tools validate-only for a short transition if needed;
- remove or quarantine Den Core Git push credentials if any were introduced;
- document final runbook in `den-network`.

## #1427 real task prep instructions

For the first real repo/task test:

- Prefer a new low-risk task rather than unblocking old `den-channels` tasks #1411/#1412.
- If using `den-channels`, create a fresh task specifically for the code-gate flow and link the old tasks as historical blockers/superseded context.
- Require `validate_only=true` and `/promotion/dry-run` for the first pass.
- Do not use worker-local paths, `/data/dev` shims, or reviewed bundle paths unless explicitly testing legacy compatibility.
- Keep live publishing disabled before and after the dry run.

Minimum decision packet fields:

```json
{
  "decision": {
    "project_id": "<project>",
    "task_id": 1427,
    "submission_id": "<submission-id>",
    "requested_by": "sysadmin",
    "operation": "push_branch",
    "target_remote": "canonical",
    "target_branch": "task/1427-<slug>",
    "expected_head_commit": "<40-char-sha>",
    "expected_base_branch": "main",
    "review_round_id": 0,
    "scope_override_ids": [],
    "scope_overrides": [],
    "validate_only": true,
    "created_at": "<iso8601>"
  },
  "submission": {
    "submission_id": "<submission-id>",
    "project_id": "<project>",
    "task_id": 1427,
    "ingress_ref": "refs/heads/submissions/<project>/tasks/1427/runs/<run-id>/attempt-001",
    "base_commit": "<40-char-sha>",
    "head_commit": "<40-char-sha>",
    "canonical_remote_url": "git@github.com:FuzzySlipper/<repo>.git",
    "status": "approved"
  }
}
```

## Validation checklist for this plan

- `den-publish` docs mention service-owned workspaces and live-disabled publish gate.
- `den-publish` tests pass.
- Den Core Trusted Publisher tests still pass while legacy compatibility remains.
- `curl http://127.0.0.1:5090/config/status` reports live publishing disabled before #1427.
- No credential files, service env, or canonical remote refs are changed by #1426.

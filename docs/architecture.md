# den-publish architecture notes

This repo implements the service side of the Den `den-code-gate` publishing contract.

Authoritative design: Den doc `den-code-gate-den-publish-workflow-contract-1420`.

Initial service boundaries:

- Den owns workflow state and audit records.
- `den-code-gate` Forgejo owns candidate Git object storage.
- Reviewers are agents/humans that fetch exact code-gate refs.
- `den-publish` owns final Git validation/fetch/push mechanics and credentials.

The first scaffold intentionally contains only health endpoints and core contract primitives. Promotion engine work belongs to task #1424 after code-gate and Den submission contracts are in place.

## Den submission contract

A `CodeSubmission` is the Den-side source-of-truth record for one immutable worker code submission. It is intentionally richer than a Git ref so that orchestrators and reviewers can make publish decisions without raw Git credentials or shell access.

Required fields captured in `DenPublish.Core`:

- `SubmissionId`, `ProjectId`, `TaskId`, `WorkerRunId`, `SubmittedBy`, `Role`, and `AttemptOrdinal` identify the Den actor/run that produced the candidate work.
- `ParentSubmissionId` links retry/fix submissions back to the prior candidate when applicable.
- `CodeGateInstance`, `CodeGateRepo`, `CodeGateRemoteUrl`, `IngressRef`, and `ConvenienceRef` identify where the candidate objects live in Forgejo/code-gate.
- `BaseBranch`, `BaseCommit`, `HeadCommit`, `CanonicalRemoteUrl`, and `TargetBranch` bind the candidate to an exact reviewed SHA and intended canonical destination.
- `ChangedFilesClaim` and `TestsRun` preserve worker-provided review evidence for validation/audit; they are claims, not trusted proof by themselves.
- `Status` tracks the workflow state (`Submitted`, `ReviewRequested`, `ChangesRequested`, `Superseded`, `Approved`, `PublishRequested`, `Published`, `Rejected`, `Failed`).
- `CreatedAt` records when Den accepted the submission.

The publish service must treat `HeadCommit` as immutable and fail closed if a publish request names a different commit from the approved submission/review round.

## Publish decision contract

A `PublishDecision` is the orchestrator/operator decision that authorizes `den-publish` to validate and, when not in dry-run mode, promote a specific submission.

Required fields captured in `DenPublish.Core`:

- `DecisionId`, `ProjectId`, `TaskId`, and `SubmissionId` bind the decision to a Den task and submission.
- `RequestedBy` identifies the orchestrator/operator creating the decision.
- `Operation` records the requested promotion shape (`PushBranch` or `FastForwardMain`).
- `TargetRemote`, `TargetBranch`, `ExpectedHeadCommit`, and `ExpectedBaseBranch` define exactly what may be updated.
- `ReviewRoundId` links the decision to the Den review round that approved the exact head.
- `ScopeOverrideIds` names explicit Den override records when the normal changed-file/scope policy is intentionally bypassed.
- `ValidateOnly` lets orchestrators ask `den-publish` to perform all checks without pushing.
- `CreatedAt` records when Den accepted the decision.

The promotion engine in task #1424 accepts only Den-provided submission/decision records and returns structured validation failures using the existing `PublishValidationResult` taxonomy.

## Initial promotion validation engine

`IPublishEngine.Validate(PublishDecision decision, CodeSubmission? submission)` is the initial pure validation boundary. It deliberately performs no Git fetch, no Git push, and no credential lookup. Those mechanics can be layered behind later infrastructure-specific adapters after the Den contract is already proven.

The current validator fails closed when:

- the decision references no available submission (`MissingSubmission`);
- the decision's project/task/submission identity does not match the supplied submission (`InvalidRequest`);
- the submission is superseded (`StaleSubmission`);
- the submission has not reached `Approved` or `PublishRequested` (`ReviewNotApproved`);
- the decision's expected head SHA does not match the submission's immutable head SHA (`CodeGateHeadMismatch`).

A matching `Approved` submission with a validate-only decision is accepted and reported as publishable, but the validate-only flag is recorded as a decision note so callers know no push should be attempted by higher layers.

## Code-gate ref verification slice

The next #1424 slice layers `CodeGatePublishValidationEngine` over the pure Den contract validator. It still performs no push and does not require canonical GitHub credentials. Its additional responsibility is to resolve the immutable code-gate `IngressRef` and prove that the remote ref still points at the exact reviewed `HeadCommit` before any later promotion step can run.

The Git-facing boundary is intentionally narrow:

- `ICodeGateRefResolver` resolves a `CodeSubmission` to a `CodeGateRefResolution`.
- `GitLsRemoteCodeGateRefResolver` uses `git ls-remote --exit-code <CodeGateRemoteUrl> <IngressRef>` so it checks the immutable submission ref, not the mutable `ConvenienceRef`.
- `CodeGatePublishValidationEngine` fails with `CodeGateFetchFailed` if the code-gate ref cannot be resolved.
- `CodeGatePublishValidationEngine` rejects with `CodeGateHeadMismatch` if the resolved remote head differs from either the Den submission head or the publish decision's expected head.

Later slices can add workspace object fetch, scope diff validation, fast-forward checks, audit persistence, and the final credential-backed push path behind the same fail-closed high-level boundary.

## Target policy validation slice

`PublishPolicyValidationEngine` adds configured target policy checks after the Den contract validator and before any code-gate/canonical Git side effects. It is still pure validation: no workspace mutation, no push, and no credential lookup.

The policy boundary is:

- `PublishTargetPolicy.TargetRemoteName` and `CanonicalRemoteUrl` define the global/default canonical target accepted by the service.
- `DenPublish:Projects:<projectId>` entries can override target remote name, canonical remote URL, push-branch prefixes, fast-forward branches, code-gate remote URL, and service-side code-gate SSH command for each project.
- `PushBranchPrefixes` allows safe task-branch publication such as `task/...` for `PushBranch` decisions.
- `FastForwardBranches` explicitly lists protected branches such as `main` that may be fast-forwarded by `FastForwardMain` decisions.
- Target branch names must pass a conservative Git branch safety check before they are used in service-owned argv-based Git commands.

Policy failures are reported as `CanonicalRemoteMismatch`, `InvalidRequest`, or `ScopeViolation` so orchestration can distinguish configuration drift, malformed decisions, and disallowed promotion scope.

## Managed workspace submission fetch slice

`GitSubmissionFetcher` implements the first workspace-mutating Git step, but it still does not contact the canonical remote or use publish credentials. It imports the exact immutable code-gate submission ref into a service-owned workspace and verifies the resulting local object id before later diff/push checks can run.

Fetch behavior:

- The local ref is `refs/den-publish/submissions/{submission_id}`.
- `submission_id` is validated as a safe ref token before command construction.
- The fetch command is argv-based: `git -C <workspace> fetch --no-tags <CodeGateRemoteUrl> +<IngressRef>:<local-ref>`.
- The verifier then runs `git -C <workspace> rev-parse <local-ref>^{commit}`.
- If fetch or rev-parse fails, the result uses `CodeGateFetchFailed`.
- If the local object id differs from the Den-reviewed `HeadCommit`, the result uses `CodeGateHeadMismatch`.

## Changed-file scope validation slice

`GitChangedFileScopeValidator` performs the next non-credentialed validation step after an exact code-gate submission has been fetched into a managed local ref. It compares the Den-recorded `BaseCommit` to the fetched local submission ref and proves the observed repository changes stay within configured policy before any publish operation can be prepared.

Scope behavior:

- The diff command is argv-based: `git -C <workspace> diff --name-only --diff-filter=ACDMRTUXB <BaseCommit> <local-ref> --`.
- `local-ref` must stay under `refs/den-publish/submissions/` and pass conservative ref safety checks before Git is invoked.
- Observed changed paths must be relative repository paths; absolute paths, traversal, and backslashes are rejected.
- Every observed changed path must match an allowed path prefix from `ChangedFileScopePolicy`.
- Observed changed paths must exactly match `CodeSubmission.ChangedFilesClaim`; worker claims remain untrusted until this diff validates them.
- Diff failures use `MissingRequiredValidation`; out-of-scope or mismatched files use `ScopeViolation`.

This slice still performs no canonical remote fetch/push and requires no GitHub publishing credentials.

## Submission ancestry validation slice

`GitSubmissionAncestryValidator` verifies that a fetched submission head is a fast-forward descendant of the Den-recorded `BaseCommit` before publish preparation continues. This prevents promotion of a candidate whose reviewed head is unrelated to, or rewound behind, the base that Den and reviewers approved.

Ancestry behavior:

- The check is argv-based: `git -C <workspace> merge-base --is-ancestor <BaseCommit> <local-ref>`.
- `local-ref` must stay under `refs/den-publish/submissions/` and pass conservative ref safety checks before Git is invoked.
- Exit code `0` accepts the ancestry relationship.
- Exit code `1` rejects with `NonFastForward`.
- Other Git failures use `MissingRequiredValidation` so orchestration fails closed rather than treating an unreadable workspace as publishable.

This slice still performs no canonical remote update. Later fast-forward-to-target checks can layer a target branch/ref fetch on top of the same `merge-base --is-ancestor` primitive.

## Promotion validation workflow slice

`PromotionValidationWorkflow` composes the previously independent validation/fetch primitives into one high-level validate-only workflow. It is the first orchestration boundary suitable for API/service wiring, but it still does not publish and does not require canonical push credentials.

Workflow order:

1. Run the configured `IPublishEngine` preflight chain. In production composition this should include Den submission contract validation, code-gate immutable ref verification, and target policy validation.
2. Fetch the exact immutable code-gate submission into the managed local ref through `ISubmissionFetcher`.
3. Validate changed-file scope through `IChangedFileScopeValidator`.
4. Validate submission base/head ancestry through `ISubmissionAncestryValidator`.
5. Return a `PromotionValidationWorkflowResult` containing the aggregate `PublishValidationResult`, the managed local ref, and the fetched head SHA.

The workflow stops at the first non-publishable stage and returns that structured failure rather than continuing into later Git steps. Fetch failures are mapped back into `PublishValidationResult` so API callers can use one result shape for both pure validation and workspace validation failures.

This workflow intentionally performs no canonical remote update. A later publisher abstraction can consume the same validated local ref/head only after audit persistence and credential-gated publish policy are in place.

## Promotion audit persistence slice

`AuditedPromotionValidationWorkflow` wraps the validate-only workflow with fail-closed audit persistence. Every workflow result is converted into a `PromotionAuditRecord` containing the Den decision identity, submission identity, validation status, summary, decisions, failures, managed local ref, fetched head, and audit timestamp.

Audit behavior:

- `IPromotionAuditStore` is the storage abstraction for workflow audit records.
- `FilePromotionAuditStore` appends newline-delimited JSON records and creates the parent directory when needed.
- If audit append succeeds, the audited workflow returns the inner workflow result unchanged.
- If audit append fails, the audited workflow returns `AuditFailed` and preserves the local ref/fetched head context so operators can investigate without treating an unaudited validation as publishable.

This means validate-only workflow results are not considered publishable unless the result was durably recorded. The current file-backed store is suitable for local service smoke/testing; production wiring can replace or wrap it with Den/Core-backed audit persistence without changing the high-level workflow contract.

## Validate-only API endpoint slice

`DenPublish.Api` exposes the first validate-only promotion endpoint at `POST /promotion/validate`. The endpoint maps a JSON request into the Core `PromotionValidationRequest`, invokes the registered `IPromotionValidationWorkflow`, and returns a stable API response containing publishability, validation status, summary, decisions, failures, managed local ref, and fetched head SHA.

API/DI behavior:

- Request parsing rejects malformed Git SHAs, unsupported publish operations, and unsupported submission statuses before invoking the workflow.
- The endpoint returns `200 OK` for publishable validate-only results and `400 Bad Request` for rejected/failed validation results.
- `AddDenPublishValidation` wires the Core workflow using process-backed Git primitives, global policy from `DenPublish:TargetPolicy`, project overrides from `DenPublish:Projects`, project-aware code-gate access routing, and a file-backed audit store at `DenPublish:AuditFilePath`.
- Target policy defaults fail closed for real canonical promotion unless either `DenPublish:TargetPolicy:CanonicalRemoteUrl` or the matching `DenPublish:Projects:<projectId>:CanonicalRemoteUrl` is configured.

This slice is endpoint/service wiring only. It does not deploy or restart a service, and it does not add canonical push credentials or perform live publishing.

## Dry-run publisher boundary slice

`DryRunPromotionPublisher` introduces the high-level publish boundary without introducing canonical push credentials or performing a live push. It consumes a `PublishDecision` plus a publishable `PromotionValidationWorkflowResult` and produces a `PromotionPublishResult`.

Publisher behavior:

- Refuses to plan promotion unless validation is publishable, a managed local ref is present, and fetched head equals the decision expected head.
- Revalidates the managed local ref namespace and target branch safety before planning any Git command shape.
- For `ValidateOnly=true`, returns a dry-run plan such as `git push canonical refs/den-publish/submissions/<id>:refs/heads/<target>` without invoking Git.
- For `ValidateOnly=false`, fails closed with `CredentialUnavailable` until a separately approved credential-backed publisher is installed.

This gives orchestrators and future API endpoints a safe command-planning surface for validate-only smoke tests while preserving the credential boundary. Live publishing remains intentionally unavailable in this implementation slice.

## Validate-and-dry-run API endpoint slice

`DenPublish.Api` now exposes `POST /promotion/dry-run` as a safe orchestration surface for end-to-end validate-only smoke tests. The endpoint maps the same request contract used by `/promotion/validate`, runs the audited validation workflow, and only invokes `IPromotionPublisher` when validation is publishable.

Endpoint behavior:

- Malformed requests return a rejected response without invoking workflow or publisher dependencies.
- Rejected/failed validation results return the validation response without invoking the publisher.
- Publishable validation results are passed to the registered publisher as `PromotionPublishRequest`.
- The default registered publisher is `DryRunPromotionPublisher`, so successful calls return planned push command shapes without invoking Git or requiring canonical push credentials.
- `ValidateOnly=false` still fails closed in the publisher with `CredentialUnavailable` until an explicitly approved credential-backed publisher is installed.

This endpoint is intended for local/service validate-only smoke before live publisher credentials exist. It is not a deployment or service configuration change by itself.

## Audit idempotency and decision replay protection slice

`AuditedPromotionValidationWorkflow` now checks audit storage for an existing `DecisionId` before re-running validation. If the decision id has already been audited with matching project/task/submission/head metadata, the workflow replays the stored audit result without calling the inner Git-backed validation pipeline or appending another JSONL record. If the same decision id is replayed with conflicting project, task, submission, or expected head metadata, the workflow rejects the request with `InvalidRequest` and does not re-run Git validation.

This is intentionally conservative. The audit record remains the durable idempotency source for validate-only promotion decisions, preventing accidental double appends from client retries while failing closed on suspicious decision-id reuse.

## Service-owned workspace root slice

`DenPublish.Api` now supports a configured service-owned workspace boundary through `DenPublish:WorkspaceRoot`. When this setting is present, API endpoints derive the validation workspace from Den-owned identity fields instead of trusting the caller-provided `WorkspacePath` field:

```text
<WorkspaceRoot>/<ProjectId>/tasks/<TaskId>/submissions/<SubmissionId>
```

The configured resolver rejects unsafe path components such as `..`, `/`, or `\` in project/submission identifiers. This keeps remote workers and cross-machine callers at the Den decision/submission layer: they provide code-gate metadata and exact SHAs, while `den-publish` controls where Git objects are fetched and verified locally.

For developer/test use without `DenPublish:WorkspaceRoot`, the API still supports the request-provided workspace path. Production/user-service deployment should set `DenPublish:WorkspaceRoot` to an agent-owned durable path and should not rely on caller-provided filesystem paths.

## Managed workspace initialization slice

`GitSubmissionFetcher` now prepares service-owned workspaces itself before importing a code-gate ref. It creates the configured workspace directory and runs `git -C <workspace> init` before fetching the exact ingress ref into `refs/den-publish/submissions/<submission_id>`.

This removes the last known per-project/per-submission filesystem shim from validate-only operation: callers provide Den/code-gate metadata; `den-publish` creates and owns its local Git workspace state. Directory preparation and `git init` failures fail closed as `CodeGateFetchFailed` before any promotion planning.



## Dry-run endpoint live-decision guard

`/promotion/dry-run` is a validate-only endpoint. It now rejects requests where `decision.validateOnly` is `false` before resolving a workspace, calling the validation workflow, fetching code-gate refs, appending audit records, or invoking a publisher. This preserves the endpoint contract if a future credential-backed publisher is registered: dry-run requests cannot accidentally become live pushes.

Live promotion must use a separate explicit endpoint (for example `/promotion/publish`) with its own credential, policy, audit, and rollback gate.


## Explicit live publish endpoint slice

Live promotion is now separated from dry-run planning. `/promotion/publish` requires `decision.validateOnly=false`; `/promotion/dry-run` requires `decision.validateOnly=true`. The service registers a disabled live publisher by default unless `DenPublish__Publishing__Enabled=true` is explicitly configured.

When enabled, the Git-backed live publisher uses argv-only `git -C <managed-workspace> push <canonical-remote-url> <managed-local-ref>:refs/heads/<target-branch>`. It revalidates the publish-ready invariants at the publisher boundary: non-validate-only decision, publishable workflow result, safe managed local ref, fetched head matching the decision expected head, safe target branch, managed workspace path, and target remote URL. Failures return structured `PublishFailureCode` values and no shell interpolation is used.

The persistent den-k8plus service remains deployed without `DenPublish__Publishing__Enabled`, so live publish requests fail before workspace resolution, validation, fetch, audit, or publisher invocation.


## Runtime configuration status surface

`den-publish` exposes `GET /config/status` as a machine-readable, redacted runtime configuration contract for Den inventory and future operator panels. The endpoint reports the effective configuration keys that control managed workspace storage, audit persistence, canonical remote policy, and live-publishing enablement.

Sensitive or credential-bearing values must not be returned raw. Canonical remote URLs are reported with `value="[redacted]"`, a short fingerprint for drift detection, and a display string with userinfo removed where parsing permits it. Live publishing remains explicit and visible through the `DenPublish:Publishing:Enabled` status.

This endpoint is intentionally local-service telemetry, not a configuration writer. Centralized Den configuration should eventually collect and compare these service-reported contracts rather than relying on scattered shell/env inspection.


## Den review-state validation

Promotion validation now requires explicit Den review state on each submission. A submission being marked `approved` or `publish_requested` is necessary but not sufficient: the submission must also carry the review round referenced by the publish decision, the review verdict must be `looks_good`, and blocking review findings must either be resolved or covered by a decision-scoped override id.

This keeps the trusted publisher from promoting work based only on stale status labels. Override ids are decision-local audit inputs: an unresolved blocking finding with an override id is publishable only when that same override id appears in `decision.scopeOverrideIds`; otherwise validation fails closed with `unresolved_blocking_findings`.


## Explicit credential policy for live publish

Live Git publishing no longer accepts ambient Git or SSH credentials from the service account. Even when `DenPublish:Publishing:Enabled=true`, the live publisher is constructed only when an explicit credential policy is configured.

The first supported production policy is `DenPublish:Publishing:CredentialMode=ssh_command` plus a redacted `DenPublish:Publishing:GitSshCommand`. The command is passed only to the child `git push` process as `GIT_SSH_COMMAND`; `GIT_TERMINAL_PROMPT=0` is also set so the service cannot hang or fall back to interactive prompts. `/config/status` reports only the mode and a fingerprint, never the command value.

This keeps future credential placement deliberate and prevents accidental use of random `agent` account SSH configuration.


## Scope override reasons and audit

Scope overrides are now structured publish-decision data, not bare ids. A decision can only cover an unresolved blocking review finding when both conditions hold:

- the finding declares an override id and that id appears in `decision.scopeOverrideIds`;
- the decision also includes a matching structured `scopeOverrides[]` entry with non-empty `reason` and `approvedBy`.

Validation records a human-readable decision trace containing the override reason. Audit JSONL records the used overrides as `scope_overrides[]` entries with `override_id`, `finding_id`, `reason`, and `approved_by`, so replay and later operator review can explain why the blocker was allowed through. Bare override ids without reasons fail closed as unresolved blocking findings.


## Project-aware runtime policy and code-gate read routing

`den-publish` can now keep one persistent service boundary while validating multiple projects. The project registry is supplied through configuration under `DenPublish:Projects:<projectId>`:

- `CanonicalRemoteUrl` — expected canonical repository for decisions/submissions in that project.
- `TargetRemoteName` — optional remote alias, defaulting to the global policy value such as `canonical`.
- `PushBranchPrefixes` and `FastForwardBranches` — optional per-project promotion branch policy.
- `CodeGateRemoteUrl` — expected code-gate remote URL for immutable submission refs.
- `CodeGateGitSshCommand` — optional service-side read credential command passed only to child `git ls-remote` / `git fetch` calls as `GIT_SSH_COMMAND`; the service also sets `GIT_TERMINAL_PROMPT=0`.

If a project policy defines `CodeGateRemoteUrl`, submissions claiming a different code-gate URL fail closed before fetch. The runtime config status endpoint reports project policy and credential fingerprints without returning raw SSH command values. This removes the need for per-project foreground service processes while keeping workers away from credential material.

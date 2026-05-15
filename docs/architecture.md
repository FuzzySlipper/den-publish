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

- `PublishTargetPolicy.TargetRemoteName` and `CanonicalRemoteUrl` define the only configured canonical target accepted for this project.
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

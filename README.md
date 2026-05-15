# den-publish

Standalone Den promotion service for the `den-code-gate` workflow.

`den-publish` is intended to become the credential and Git-promotion boundary. Coder/reviewer agents submit candidate commits to `den-code-gate` Forgejo and Den records exact submission/review/publish decisions. This service validates Den state and mechanically promotes exact reviewed commits to canonical remotes.

## Current scaffold

- `src/DenPublish.Core`: core submission/ref/validation contract primitives.
- `src/DenPublish.Api`: minimal HTTP service shell with `/healthz`, `/readyz`, and contract example endpoint.
- `tests/DenPublish.Core.Tests`: deterministic unit tests for contract primitives.

## Local verification

```bash
dotnet restore DenPublish.slnx
dotnet test DenPublish.slnx
dotnet run --project src/DenPublish.Api
```

## Design source of truth

Den document: `den-publish/den-code-gate-den-publish-workflow-contract-1420`.

## Service-owned workspace configuration

For production-style runs, configure `DenPublish:WorkspaceRoot` so `den-publish` derives its own managed Git workspace path instead of accepting a worker-provided filesystem path:

```bash
DenPublish__WorkspaceRoot=/home/agents/runtime/den-publish/workspaces DenPublish__AuditFilePath=/home/agents/runtime/den-publish/audit/promotion-validation.jsonl DenPublish__TargetPolicy__CanonicalRemoteUrl=git@github.com:FuzzySlipper/<repo>.git ASPNETCORE_URLS=http://127.0.0.1:5090 dotnet run --project src/DenPublish.Api --no-launch-profile
```

When `WorkspaceRoot` is set, request `WorkspacePath` values are ignored and the service derives:

```text
<WorkspaceRoot>/<ProjectId>/tasks/<TaskId>/submissions/<SubmissionId>
```

Do not add canonical push credentials or enable persistent service deployment until validate-only smoke and deployment approval have completed.


## Agent workflow UX

For real repo/task rehearsals, use `docs/agent-workflow-ux.md` plus the packet templates under `templates/agent-workflow/`. These artifacts keep coder, reviewer, and orchestrator agents synchronized on exact code-gate refs and reviewed SHAs without requiring orchestrators to type promotion Git commands.

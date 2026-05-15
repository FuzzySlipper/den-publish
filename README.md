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

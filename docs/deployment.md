# den-publish deployment readiness

This document describes the local-only deployment shape for `den-publish`. It is an operator plan and example configuration, not an instruction to install the service without approval.

## Operating boundary

`den-publish` is the credential and Git-promotion boundary for Den Trusted Publisher. Workers submit candidate commits to `den-code-gate`; Den records exact submission/review/publish decisions; `den-publish` validates exact refs and plans or performs promotion.

Production deployment must keep the service local-only until a separate exposure decision is approved:

- bind `ASPNETCORE_URLS` to `http://127.0.0.1:5090`;
- run as the `agent` user;
- use an agent-owned runtime root such as `/home/agents/runtime/den-publish`;
- configure `DenPublish__WorkspaceRoot` so callers do not supply host filesystem paths;
- keep canonical push credentials out of worker sandboxes;
- do not configure live canonical push credentials until the validate-only path is approved.

## Repo publish

```bash
sudo -n -u agent -- bash -lc 'cd /home/dev/den-publish && ./scripts/publish-local.sh'
```

Default publish output:

```text
/home/dev/den-publish/artifacts/publish/DenPublish.Api
```

Override with:

```bash
DEN_PUBLISH_OUTPUT=/some/path CONFIGURATION=Release ./scripts/publish-local.sh
```

## Runtime directory plan

Approval-gated persistent deployment should create these as `agent:agents` with narrow group-friendly modes:

```text
/home/agents/runtime/den-publish/
├── audit/promotion-validation.jsonl
├── env/den-publish.env              # optional canonical location for operator env
├── logs/                            # if file logs are added later
├── ssh/                             # future canonical deploy key location; not used for validate-only
└── workspaces/<project>/tasks/<task>/submissions/<submission>
```

The current example unit uses `%h/.config/den-publish/den-publish.env` as the `EnvironmentFile` because user services expand `%h` reliably. Operators may instead symlink that file to `/home/agents/runtime/den-publish/env/den-publish.env` after reviewing permissions.

## Validate-only environment example

No secrets are required for validate-only code-gate fetches when the code-gate repo supplies temporary/read-only access or when `GIT_SSH_COMMAND` references an approved key.

```bash
DenPublish__WorkspaceRoot=/home/agents/runtime/den-publish/workspaces
DenPublish__AuditFilePath=/home/agents/runtime/den-publish/audit/promotion-validation.jsonl
DenPublish__TargetPolicy__CanonicalRemoteUrl=git@github.com:FuzzySlipper/den-publish.git
```

`DenPublish__WorkspaceRoot` is required for production. When configured, `den-publish` derives:

```text
<WorkspaceRoot>/<ProjectId>/tasks/<TaskId>/submissions/<SubmissionId>
```

Caller-provided `WorkspacePath` is ignored in configured mode.

The fetcher creates and initializes this workspace automatically before fetching the exact code-gate ingress ref, so operators do not need per-project or per-submission `git init` shims.

## User systemd example

Example only; do not install without approval:

```bash
install -d -m 0750 /home/agent/.config/systemd/user /home/agent/.config/den-publish
cp deploy/systemd/den-publish.service.example /home/agent/.config/systemd/user/den-publish.service
systemctl --user daemon-reload
systemctl --user enable --now den-publish.service
```

Verify as the `agent` user/runtime:

```bash
systemctl --user status den-publish.service --no-pager
curl -fsS http://127.0.0.1:5090/healthz
curl -fsS http://127.0.0.1:5090/readyz
ss -ltnp | grep ':5090'
```

Rollback:

```bash
systemctl --user disable --now den-publish.service
rm -f /home/agent/.config/systemd/user/den-publish.service
systemctl --user daemon-reload
```

Leave runtime/audit/workspace data intact unless cleanup is explicitly approved.

## Live publish gate

Canonical push credentials and live canonical pushes require separate explicit approval. Before that gate, the service should only be used for validate-only and dry-run planning.

The `/promotion/dry-run` endpoint rejects `decision.validate_only=false` before validation/fetch/audit/publisher execution. A future live-publish rollout must use a separate explicit publish endpoint and must not reuse the dry-run endpoint for credential-backed pushes.


### Live publish endpoint gate

`/promotion/publish` is present but disabled in the validate-only deployment unless `DenPublish__Publishing__Enabled=true` is set. Do not set that flag in the persistent service until canonical credential placement and live-smoke rollback scope are separately approved.

When live publishing is enabled for a local-only smoke, use disposable bare repositories and a temporary foreground process first. The persistent service should continue to omit `DenPublish__Publishing__Enabled` until the credential gate is approved.


### Runtime configuration status

Use the local status endpoint to verify the effective service configuration without reading scattered env files directly:

```bash
curl -fsS http://127.0.0.1:5090/config/status
```

The response follows `den-publish-runtime-config-v1` and reports workspace root, audit file path, canonical remote policy, and live-publishing enablement. Canonical remote values are redacted and include only a fingerprint/display form suitable for drift checks. Missing production-required settings produce warnings.

This endpoint is the preferred integration point for a future central Den configuration panel. The panel should query service-owned config status surfaces and compare them against Den-owned desired state, rather than depending on operators remembering every env file path.


### Publish request review state

Production publish and dry-run requests must include Den review state in the submission payload. The publisher validates that `submission.review.review_round_id` matches `decision.review_round_id`, the verdict is `looks_good`, and any unresolved blocking findings are either resolved or covered by explicit `decision.scope_override_ids`.

Treat missing review state as a deployment/request bug. The service fails closed with `missing_review`; do not bypass this by loosening submission status alone.


### Live publish credential policy

Do not rely on ambient `agent` GitHub credentials for `/promotion/publish`. Live publishing requires all of the following before the service will construct the Git-backed live publisher:

- `DenPublish__Publishing__Enabled=true`
- `DenPublish__Publishing__CredentialMode=ssh_command`
- `DenPublish__Publishing__GitSshCommand=<redacted operator-managed ssh command>`

The SSH command value is treated as sensitive configuration. It is not included in `/config/status`; the status surface reports only `display=ssh_command` plus a fingerprint for drift checks. The service also sets `GIT_TERMINAL_PROMPT=0` for live `git push` calls.

Credential file placement and canonical live smoke still require a separate explicit approval gate.


### Scope override audit requirements

Promotion callers must provide structured override metadata when using scope overrides. Supplying only `scope_override_ids` is not enough for an unresolved blocking finding. Include matching `scope_overrides[]` entries with:

- `override_id`
- `reason`
- `approved_by`

The service persists used overrides to the audit JSONL record. This makes exceptional publish decisions explainable during central Den inventory/review and avoids undocumented bypasses.

## Final live publish approval runbook

The concrete approval/execution checklist for credential placement and canonical smoke is maintained in [`docs/live-publish-runbook.md`](live-publish-runbook.md). Do not enable live publishing or place canonical push credentials without approval of that semantic plan.

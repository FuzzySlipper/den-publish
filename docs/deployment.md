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

# den-publish live publish approval runbook

This runbook is the final approval artifact for enabling `/promotion/publish` against a canonical GitHub remote. It is intentionally written so operators can review the exact semantic plan before any credential file is created, any service environment is changed, or any canonical branch is pushed.

For the reusable project-to-project rehearsal path, use [`docs/live-publish-rehearsal-checklist.md`](live-publish-rehearsal-checklist.md). For the required human approval packet, use [`templates/agent-workflow/live-publish-approval-request.template.md`](../templates/agent-workflow/live-publish-approval-request.template.md).

## Current safe state

The deployed service is expected to remain in this state until the final gate is approved:

- user service: `den-publish.service` running as `agent`
- bind address: `127.0.0.1:5090`
- runtime root: `/home/agents/runtime/den-publish`
- service env path: `/home/agent/.config/den-publish/den-publish.env`
- before approval: live publishing disabled and credential policy missing/not configured
- after explicit #1468 approval: live publishing may remain enabled only with an explicit redacted `ssh_command` credential policy visible in `/config/status`
- `/promotion/dry-run`: validate-only only
- `/promotion/publish`: fails closed with `credential_unavailable`

Verify without reading secrets:

```bash
curl -fsS http://127.0.0.1:5090/readyz
curl -fsS http://127.0.0.1:5090/config/status
```

Before this runbook starts, `/config/status` must report `configurationContract=den-publish-runtime-config-v2`, `livePublishing.enabled=false`, and `liveCredentialPolicy.configured=false`. After a separate approval to leave live publish enabled, `/config/status` must instead report live enabled plus `liveCredentialPolicy.display=ssh_command`, `value=[redacted]`, and a fingerprint.

## Approval required

Do not execute this runbook from a worker sandbox or delegated project context. Den Core and worker sandboxes must not receive canonical push credentials. A sysadmin/operator must explicitly approve all of the following before execution:

1. credential owner and storage location;
2. canonical repository allowed for the smoke;
3. target smoke branch name;
4. whether to delete the smoke branch afterward;
5. whether live publishing remains enabled after the smoke or is immediately disabled again;
6. audit-retention expectation for both successful and failed smoke attempts.

A minimal approval statement should name the repo and branch, for example:

> Approve den-publish live publish smoke using an agent-owned deploy key at `/home/agents/runtime/den-publish/ssh/den-publish-github`, canonical repo `git@github.com:FuzzySlipper/den-publish.git`, target branch `task/1424-live-publish-smoke`, delete the branch afterward, and disable live publishing after the smoke.

## Credential design

Use a dedicated deploy key or deploy identity scoped to the canonical repository. Do not rely on ambient `agent` account GitHub SSH config or personal `gh` credentials.

Recommended files, created only after approval:

```text
/home/agents/runtime/den-publish/ssh/
├── den-publish-github        # private key, 0600, agent:agents
├── den-publish-github.pub    # public key, 0644, agent:agents
└── known_hosts               # GitHub host key material, 0644 or 0600, agent:agents
```

Recommended SSH command shape, stored only in the service environment file and never printed in full in Den messages. The service now requires the same hardened shape for live publish credentials and project code-gate read credentials: `ssh -F /dev/null`, explicit identity, explicit known_hosts, `IdentitiesOnly=yes`, `BatchMode=yes`, and `StrictHostKeyChecking=yes`.

```bash
ssh -F /dev/null -i /home/agents/runtime/den-publish/ssh/den-publish-github   -o IdentitiesOnly=yes   -o BatchMode=yes   -o UserKnownHostsFile=/home/agents/runtime/den-publish/ssh/known_hosts   -o StrictHostKeyChecking=yes
```

The service passes this command only to the child `git push` environment as `GIT_SSH_COMMAND` and also sets `GIT_TERMINAL_PROMPT=0`. `/config/status` reports only `display=ssh_command` plus a fingerprint.

## Preflight checks

Run as sysadmin, but execute repository/service commands as `agent`:

```bash
sudo -n -u agent -- git -C /home/dev/den-publish status --short --branch
sudo -n -u agent -- dotnet test /home/dev/den-publish/DenPublish.slnx
curl -fsS http://127.0.0.1:5090/config/status
sudo -n -u agent -- wc -l /home/agents/runtime/den-publish/audit/promotion-validation.jsonl
```


Before enabling live publish or retrying a workspace-related failure, run the read-only workspace preflight against the managed workspace for the project under test:

```bash
python3 /home/dev/den-publish/scripts/check-workspace-preflight.py   --workspace /home/agents/runtime/den-publish/workspaces/<project_id>   --expected-owner agent   --json
```

Treat `mixed_workspace_ownership`, `git_config_lock_risk`, `ssh_config_symlink_review_required`, and `ssh_config_permissions_too_open` as hard operational blockers. They indicate hygiene failures that `audit_warn` must not mask. Any ownership repair requires a separate sysadmin approval plan; prefer deleting/recreating disposable managed workspaces or running Git as the service owner over broad permission changes.

Required result:

- repo clean at the intended commit;
- tests pass;
- live publishing disabled before credential placement;
- audit file readable and line count recorded.


## Audit-warn lock-down controls

`audit_warn` is the soft default for trusted orchestrators while the promotion workflow is being hardened. It is intentionally easy to lock down globally or for a single project when a concrete problem appears. These changes affect validation policy only; they do not create credentials, enable live publishing, restart services, or mutate remotes by themselves.

### Global lock-down

Set all trusted-orchestrator requests to strict or defensive mode:

```text
DenPublish__PromotionPolicy__TrustedOrchestratorMode=strict
# or
DenPublish__PromotionPolicy__TrustedOrchestratorMode=defensive
```

Use `strict` to reject soft failures that `audit_warn` would otherwise allow with warnings. Use `defensive` as the incident posture when operators want trusted-orchestrator requests to remain identifiable while disabling the permissive warning escape hatch.

### Project-specific lock-down

Set a per-project override without changing the global default:

```text
DenPublish__Projects__den-publish__TrustedOrchestratorMode=defensive
# or
DenPublish__Projects__den-publish__TrustedOrchestratorMode=strict
```

For indexed project configuration, use the numeric section key and keep `ProjectId` unchanged:

```text
DenPublish__Projects__0__ProjectId=den-publish
DenPublish__Projects__0__TrustedOrchestratorMode=defensive
```

`/config/status` reports both the global `promotionPolicy.trustedOrchestratorMode` and each project's effective `projectPolicies[].trustedOrchestratorMode`. Values inherited from the global policy display as defaults; project overrides are marked configured.

### Incident triage checklist for allowed-with-warnings records

When the watchdog reports `recent_allowed_with_warnings_publish`, inspect the audit record before retrying promotion work:

1. **Who requested it:** `requested_by` and, when present, warning metadata `requested_by` / caller trust.
2. **Which warnings were allowed:** `warnings[].code`, `warnings[].message`, `warnings[].severity`, `warnings[].strict_action`, and `warnings[].permissive_action`.
3. **Exact source:** `submission_id`, `ingress_ref`, `fetched_head_commit`, and `base_commit`.
4. **Exact target:** `target_remote`, `target_branch`, and `operation`.
5. **Review state:** `review_round_id`, verdict, and unresolved blocking finding status in Den.
6. **Lock-down action:** set either `DenPublish__PromotionPolicy__TrustedOrchestratorMode=strict` for global lock-down or `DenPublish__Projects__<project>__TrustedOrchestratorMode=defensive` / `strict` for project-only containment, then restart through the normal approved service-change plan.

Do not bypass the audit record by replaying an older decision after lock-down. The material validation inputs and policy context must match the current request.

## Enable live publishing after credential placement

After credential files are placed and the public key is authorized on the canonical repository, update the service environment atomically with a timestamped backup. The environment must include the existing validate-only settings plus only this live delta:

```bash
DenPublish__Publishing__Enabled=true
DenPublish__Publishing__CredentialMode=ssh_command
DenPublish__Publishing__GitSshCommand=<redacted approved ssh command>
```

Restart and verify:

```bash
sudo -n -u agent -- env XDG_RUNTIME_DIR=/run/user/1001   DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1001/bus   systemctl --user restart den-publish.service
curl -fsS http://127.0.0.1:5090/readyz
curl -fsS http://127.0.0.1:5090/config/status
```

Required `/config/status` result:

- live publishing configured/enabled;
- credential policy configured;
- credential policy value redacted;
- no secret material displayed.

## Canonical live smoke

Use a reviewed Den submission payload with all normal validation gates populated:

- exact code-gate ingress ref;
- expected immutable head SHA;
- `looks_good` review state;
- no unresolved blocking findings, or structured override reason/approver if explicitly approved;
- allowed path prefixes for the smoke;
- target branch scoped to the task, for example `task/1424-live-publish-smoke`;
- `decision.validateOnly=false`;
- endpoint: `POST http://127.0.0.1:5090/promotion/publish`.

Success criteria:

- HTTP 200;
- `publishStatus=published`;
- validation `isPublishable=true`;
- audit line count increases by exactly one;
- latest audit record has the smoke decision id and expected head;
- `git ls-remote <canonical> refs/heads/<smoke-branch>` returns the expected head.

## Rollback

If the service fails to start or `/config/status` is wrong:

1. restore the timestamped pre-live environment file;
2. restart `den-publish.service`;
3. verify `livePublishing.enabled=false` or credential policy missing;
4. leave audit/workspaces intact for diagnosis.

If the smoke pushed a branch and deletion was approved:

```bash
git push <canonical> :refs/heads/<smoke-branch>
```

Run deletion with the same explicit credential policy, not ambient credentials. Verify `git ls-remote` no longer returns the branch.

If live publishing should not remain enabled after the smoke, remove `DenPublish__Publishing__Enabled`, `DenPublish__Publishing__CredentialMode`, and `DenPublish__Publishing__GitSshCommand` from the service env, restart, and verify `/promotion/publish` again fails closed with `credential_unavailable` and does not append audit records.

If live publishing is approved to remain enabled, run post-live drift checks in approved-live mode:

```bash
cd /home/dev/den-publish
python3 scripts/check-promotion-workflow-watchdog.py --allow-live-enabled --json
python3 scripts/check-project-promotion-readiness.py --project den-publish --allow-live-enabled --json
```

The same scripts without `--allow-live-enabled` intentionally remain fail-closed and should report live publishing as drift.

## Documentation packet

Before executing the smoke, record the filled approval request from `templates/agent-workflow/live-publish-approval-request.template.md`. After the smoke, post a Den task update with:

- commit/service version;
- canonical repo and branch name;
- expected head SHA;
- audit record id or decision id;
- whether the branch was retained or deleted;
- whether live publishing remained enabled or was disabled;
- credential material summarized only as `[REDACTED]` / configured mode / fingerprint.

# den-publish live publish approval runbook

This runbook is the final approval artifact for enabling `/promotion/publish` against a canonical GitHub remote. It is intentionally written so operators can review the exact semantic plan before any credential file is created, any service environment is changed, or any canonical branch is pushed.

## Current safe state

The deployed service is expected to remain in this state until the final gate is approved:

- user service: `den-publish.service` running as `agent`
- bind address: `127.0.0.1:5090`
- runtime root: `/home/agents/runtime/den-publish`
- service env path: `/home/agent/.config/den-publish/den-publish.env`
- live publishing: disabled
- credential policy: missing/not configured
- `/promotion/dry-run`: validate-only only
- `/promotion/publish`: fails closed with `credential_unavailable`

Verify without reading secrets:

```bash
curl -fsS http://127.0.0.1:5090/readyz
curl -fsS http://127.0.0.1:5090/config/status
```

`/config/status` must report `configurationContract=den-publish-runtime-config-v1`, `livePublishing.enabled=false`, and `liveCredentialPolicy.configured=false` before this runbook starts.

## Approval required

Do not execute this runbook from a worker sandbox or delegated project context. A sysadmin/operator must explicitly approve all of the following before execution:

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

Recommended SSH command shape, stored only in the service environment file and never printed in full in Den messages:

```bash
ssh -i /home/agents/runtime/den-publish/ssh/den-publish-github   -o IdentitiesOnly=yes   -o BatchMode=yes   -o UserKnownHostsFile=/home/agents/runtime/den-publish/ssh/known_hosts   -o StrictHostKeyChecking=yes
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

Required result:

- repo clean at the intended commit;
- tests pass;
- live publishing disabled before credential placement;
- audit file readable and line count recorded.

## Enable live publishing after credential placement

After credential files are placed and the public key is authorized on the canonical repository, update the service environment atomically with a timestamped backup. The environment must include the existing validate-only settings plus:

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
- `decision.validate_only=false`;
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

## Documentation packet

After the smoke, post a Den task update with:

- commit/service version;
- canonical repo and branch name;
- expected head SHA;
- audit record id or decision id;
- whether the branch was retained or deleted;
- whether live publishing remained enabled or was disabled;
- credential material summarized only as `[REDACTED]` / configured mode / fingerprint.

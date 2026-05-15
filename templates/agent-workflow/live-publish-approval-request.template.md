# Live publish approval request

This request is required before any canonical push credential is placed or `den-publish.service` live publishing is enabled.

## Requested live smoke window

- approving operator: `<name>`
- requested by: `<agent_or_user>`
- project_id: `<den_project_id>`
- task_id: `<task_id>`
- review_round_id: `<review_round_id>`
- submission_id: `<submission_id>`
- ingress_ref: `<immutable_code_gate_ref>`
- canonical repository: `<git@github.com:OWNER/REPO.git>`
- target branch: `task/<task_id>-<slug>`
- expected head commit: `<40-char-reviewed-head>`
- expected base branch: `main`
- operation: `push_branch`

## Dry-run evidence

- dry-run endpoint/tool used: `<Den Core facade or /promotion/dry-run>`
- dry-run decision id: `<decision_id>`
- dry-run publishStatus: `dry_run`
- dry-run isPublishable: `true`
- audit record pointer: `<audit pointer>`
- post-dry-run status: `livePublishing.enabled=false`

## Credential boundary

Den Core and worker sandboxes will not receive canonical push credentials.

The approved credential mode is:

```text
ssh_command
```

The credential is service-owned by `den-publish.service` and used only as child-process environment:

```text
GIT_SSH_COMMAND=[REDACTED]
GIT_TERMINAL_PROMPT=0
```

Required hardened SSH options:

- `ssh -F /dev/null`
- `IdentitiesOnly=yes`
- `BatchMode=yes`
- `UserKnownHostsFile=<service-owned known_hosts>`
- `StrictHostKeyChecking=yes`

Service status must redact the command and show only `display=ssh_command` plus fingerprint.

## Storage and scope

- private key path: `/home/agents/runtime/den-publish/ssh/<project>-github`
- public key path: `/home/agents/runtime/den-publish/ssh/<project>-github.pub`
- known_hosts path: `/home/agents/runtime/den-publish/ssh/known_hosts`
- file owner/mode target: `agent:agents`, private key `0600`
- credential scope: `<single canonical repo deploy key or deploy identity>`
- rotation/revocation owner: `<operator>`

## Cleanup decision

- delete smoke branch afterward: `<yes/no>`
- disable live publishing afterward: `<yes/no; default yes>`
- retain audit/workspace evidence: `yes unless separately approved cleanup`

## Rollback plan

1. Backup service env before live changes.
2. If service fails, restore env backup and restart.
3. If publish is ambiguous, run `git ls-remote` for the target branch before retrying.
4. If branch deletion is approved, delete `refs/heads/<target-branch>` with the same explicit credential policy.
5. Remove live env entries, restart, and verify `livePublishing.enabled=false` and `liveCredentialPolicy.configured=false`.

## Approval statement

> Approve den-publish live publish smoke for `<project_id>` task `<task_id>` using a service-owned `[REDACTED]` `ssh_command` credential scoped to `<canonical repository>`, target branch `<target branch>`, expected head `<40-char-reviewed-head>`, delete branch `<yes/no>`, and disable live publishing afterward `<yes/no>`.

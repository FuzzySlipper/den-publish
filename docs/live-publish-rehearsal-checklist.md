# Standard live-publish credential and rehearsal checklist (#1435)

This checklist is the standard path from a successful `den-publish` validate-only dry-run to a scoped live `/promotion/publish` smoke. It is a rehearsal/approval artifact, not permission to place credentials or enable live publishing.

## Boundary

Live publish remains an explicit sysadmin/operator gate. Coder workers, reviewer workers, Den Core, and project-specific runner sandboxes must not receive canonical GitHub push credentials.

The only standard credential boundary for canonical promotion is:

```text
den-publish.service -> child git push with GIT_SSH_COMMAND + GIT_TERMINAL_PROMPT=0
```

The persistent service must return to `livePublishing.enabled=false` unless a separately approved decision says otherwise.

## Phase 0 — current safe-state evidence

Collect without reading secret files:

```bash
curl -fsS http://127.0.0.1:5090/readyz
curl -fsS http://127.0.0.1:5090/config/status | python3 -m json.tool
python3 scripts/validate-live-publish-runbook.py --status-url http://127.0.0.1:5090/config/status
```

Required status before live work:

- `configurationContract=den-publish-runtime-config-v2`
- workspace root configured
- audit path configured
- project canonical/code-gate policy configured for the target project
- code-gate read credential, if present, shown only as `display=ssh_command` plus fingerprint
- `livePublishing.enabled=false`
- `liveCredentialPolicy.configured=false`
- warnings empty or explicitly explained in the approval request

## Phase 1 — dry-run success package

A project may request a live smoke only after a dry-run package exists with:

- Den project id and task id
- `submission_id`
- immutable `ingress_ref`
- exact `base_commit` and `head_commit`
- exact Den `review_round_id` with verdict `looks_good`
- changed-files claim and allowed prefixes
- target branch, normally `task/<task-id>-<slug>`
- dry-run endpoint used: `/promotion/dry-run`
- dry-run response: `publishStatus=dry_run`, `isPublishable=true`
- service audit decision id / audit line evidence
- post-dry-run proof that `livePublishing.enabled=false`

No live approval should be granted for a stale review head, unresolved blocking finding without structured override, missing code-gate read policy, or missing rollback/delete plan.

## Phase 2 — approval request

Use `templates/agent-workflow/live-publish-approval-request.template.md`. The approval request must name:

- approving human/operator
- project id and task id
- canonical repository
- target branch
- expected head commit
- review round id
- dry-run decision/audit evidence
- credential owner and storage path
- exact credential mode: `ssh_command`
- whether the smoke branch will be deleted afterward
- disable-after-smoke decision
- rollback plan

The request must state explicitly:

```text
Den Core and worker sandboxes will not receive canonical push credentials.
```

## Phase 3 — credential shape

Use a dedicated deploy key or deploy identity scoped to the target canonical repository. Recommended service-owned files, created only after approval:

```text
/home/agents/runtime/den-publish/ssh/
├── <project>-github        # private key, 0600, agent:agents
├── <project>-github.pub    # public key, 0644, agent:agents
└── known_hosts             # pinned host keys, 0644 or 0600, agent:agents
```

Recommended command shape:

```bash
ssh -F /dev/null \
  -i /home/agents/runtime/den-publish/ssh/<project>-github \
  -o IdentitiesOnly=yes \
  -o BatchMode=yes \
  -o UserKnownHostsFile=/home/agents/runtime/den-publish/ssh/known_hosts \
  -o StrictHostKeyChecking=yes
```

`den-publish` passes the command only to child Git as `GIT_SSH_COMMAND` and forces `GIT_TERMINAL_PROMPT=0`. `/config/status` must redact the command and report only `display=ssh_command` plus a fingerprint.

## Phase 4 — enable window

Before editing service env, create a timestamped backup. The live env delta is limited to:

```bash
DenPublish__Publishing__Enabled=true
DenPublish__Publishing__CredentialMode=ssh_command
DenPublish__Publishing__GitSshCommand=<redacted approved ssh command>
```

Restart `den-publish.service`, then verify:

```bash
curl -fsS http://127.0.0.1:5090/readyz
curl -fsS http://127.0.0.1:5090/config/status | python3 -m json.tool
```

Required status during the live window:

- live publishing enabled
- credential policy configured
- credential mode/fingerprint visible
- no raw SSH command, key path secret, token, or private key material printed in Den messages

## Phase 5 — live publish smoke

Call the Den Core field-based facade or the service `/promotion/publish` endpoint only with the exact reviewed decision/submission envelope:

- `decision.validateOnly=false`
- `decision.expectedHeadCommit=<reviewed head>`
- `decision.reviewRoundId=<approved review round>`
- `submission.ingressRef=<immutable code-gate ref>`
- `submission.headCommit=<same reviewed head>`
- `submission.review.verdict=looks_good`

Success criteria:

- HTTP 200
- `publishStatus=published`
- validation `isPublishable=true`
- audit line count increases as expected
- latest audit record matches decision id and expected head
- canonical remote branch resolves to expected head:

```bash
GIT_SSH_COMMAND='<redacted same approved command>' \
GIT_TERMINAL_PROMPT=0 \
git ls-remote <canonical-remote-url> refs/heads/<target-branch>
```

## Phase 6 — cleanup and rollback

If deletion was approved, delete only the smoke branch with the same explicit credential policy:

```bash
GIT_SSH_COMMAND='<redacted same approved command>' \
GIT_TERMINAL_PROMPT=0 \
git push <canonical-remote-url> :refs/heads/<target-branch>
```

Verify deletion:

```bash
GIT_SSH_COMMAND='<redacted same approved command>' \
GIT_TERMINAL_PROMPT=0 \
git ls-remote <canonical-remote-url> refs/heads/<target-branch>
```

If live publishing should not remain enabled, remove or restore away these env entries:

```text
DenPublish__Publishing__Enabled
DenPublish__Publishing__CredentialMode
DenPublish__Publishing__GitSshCommand
```

Restart and verify:

- `/config/status` reports `livePublishing.enabled=false`
- `/config/status` reports `liveCredentialPolicy.configured=false`
- `/promotion/publish` fails closed if attempted without credentials
- audit/workspace data are retained unless cleanup is explicitly approved

## Failure handling

- If service restart fails: restore timestamped env backup, restart, verify disabled status, retain logs/audit.
- If publish response is ambiguous after validation: do not retry blindly; first run `git ls-remote` for the target branch and inspect the audit record.
- If branch pushed but cleanup failed: leave live publishing disabled, record branch name/head, and escalate for deletion plan.
- If `/config/status` ever exposes raw credential material: immediately disable live publishing, rotate/revoke the credential, and treat the status surface as a security incident.

## Completion packet

Post a Den update containing:

- project/task/review ids
- canonical repo and target branch
- expected head
- dry-run proof reference
- live response summary
- audit decision id / record pointer
- canonical branch verification output
- whether branch was retained or deleted
- whether live publishing was disabled afterward
- credential summary only as `[REDACTED]`, mode, display, and fingerprint

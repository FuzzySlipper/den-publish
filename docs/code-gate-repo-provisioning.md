# Code-gate repository provisioning path (#1436)

This document defines how a Den project gets a standard `den-code-gate` repository for worker submissions, reviewer fetches, and `den-publish` dry-run/live promotion. It removes the old temporary local bare-repo hack from the normal workflow.

## Scope and boundary

`den-code-gate` stores candidate commits only. It is not a semantic reviewer and it does not hold canonical GitHub push authority.

Workers may receive code-gate-only submission access. Reviewers may receive code-gate read access. `den-publish.service` may receive service-side code-gate read access. None of those credentials are canonical GitHub push credentials.

Do not paste Forgejo admin tokens, deploy private keys, `GIT_SSH_COMMAND` values, or code-gate private keys into Den task messages or worker prompts.

## Naming policy

For a Den project `<project_id>` with canonical repo `<repo>`:

- Forgejo instance: `den-code-gate`
- owner/org: `<project_id>` unless a project-specific owner is documented
- repository: `<repo>`
- SSH remote: `ssh://git@192.168.1.10:3022/<owner>/<repo>.git`
- inventory file: `config/code-gate-repositories.json`
- promotion metadata file: `config/promotion-projects.json`

For `den-channels`:

```text
owner: den-channels
repo: den-channels
remote: ssh://git@192.168.1.10:3022/den-channels/den-channels.git
```

## Ref policy

Authoritative immutable submission refs:

```text
refs/heads/submissions/{project_id}/tasks/{task_id}/runs/{run_id}/attempt-{attempt_ordinal}
```

Convenience ref:

```text
refs/heads/submissions/{project_id}/tasks/{task_id}/current
```

Rules:

- Immutable attempt refs are authority and must not be moved by normal workers.
- A follow-up/rework commit gets a new attempt ordinal and a new submission id.
- `current` may be moved for browsing but is never publish authority.
- Reviews and publishes bind to exact full `head_commit`, not the ref name alone.

## Access policy

| Actor | Access | Scope |
|---|---|---|
| worker submitters | push | code-gate repo only, submission refs only; no canonical remote |
| reviewers | fetch | immutable ingress refs for review |
| `den-publish.service` | fetch | exact immutable ingress refs, service-side redacted `CodeGateGitSshCommand` |
| operators | admin | create/repair code-gate repos and access grants |

Prefer per-project or per-worker code-gate deploy keys/users over shared broad credentials. If a worker credential is passed to a worker, the prompt should name only the credential handle/instructions, not the private key or full command.

## Tool-backed preflight

Use:

```bash
python3 scripts/check-code-gate-repo.py --project den-channels
```

Default preflight checks:

- repository inventory shape
- den-publish `/config/status` has matching project code-gate policy and redacted read credential fingerprint
- persistent service is not live-publish enabled
- Forgejo HTTP app is reachable, accepting `200`, `401`, or `403` because the instance requires sign-in
- Forgejo SSH port is reachable
- authenticated repository lookup is skipped unless an operator supplies `DEN_CODE_GATE_ADMIN_TOKEN`

To emit a worker-safe packet:

```bash
python3 scripts/check-code-gate-repo.py --project den-channels --emit-worker-preflight
```

The emitted packet contains the remote/ref patterns and a sync-line template, but no secret material.

## Authenticated check/create path

An operator can check or create the repository by supplying the admin token through the environment. Do not print the token.

Check:

```bash
DEN_CODE_GATE_ADMIN_TOKEN=<redacted> \
python3 scripts/check-code-gate-repo.py --project den-channels
```

Create a missing repo under an existing owner org:

```bash
DEN_CODE_GATE_ADMIN_TOKEN=<redacted> \
python3 scripts/check-code-gate-repo.py --project den-channels --create
```

Create the owner org too, if approved:

```bash
DEN_CODE_GATE_ADMIN_TOKEN=<redacted> \
python3 scripts/check-code-gate-repo.py --project den-channels --create --create-owner-org
```

The script uses Forgejo API calls with the token in an HTTP header and does not print the token. Creation is opt-in and fails closed if the token is missing.

## Optional SSH access probe

If an operator wants to verify the actual code-gate Git read/write path without exposing the command, provide a per-project SSH command environment variable:

```bash
DEN_CODE_GATE_REPO_SSH_COMMAND_DEN_CHANNELS='<redacted code-gate-only ssh command>' \
python3 scripts/check-code-gate-repo.py --project den-channels --probe-ssh
```

The script passes the value only as child-process `GIT_SSH_COMMAND` and sets `GIT_TERMINAL_PROMPT=0`. It suppresses Git stdout/stderr and reports success/failure only.

## Worker start preflight

Before launching a coder worker for a promotion-aware task, run:

```bash
python3 scripts/check-code-gate-repo.py --project <project_id>
python3 scripts/check-promotion-metadata-drift.py --project <project_id>
python3 scripts/render-agent-context-packet.py --project <project_id> --task-id <task_id>
```

A worker may start when:

- preflight has no errors;
- missing authenticated repo lookup is understood or an operator has verified/created the repo;
- the worker receives code-gate-only submission instructions;
- Den task packet includes `submission=<id> ingress_ref=<ref> head=<sha> base=<sha> review_round=<id|pending> target=<branch>`.

## Den-channels status during #1436

The safe preflight for `den-channels` passed without a Forgejo admin token. It proved:

- `den-publish` runtime has matching `den-channels` code-gate policy;
- service-side code-gate read credential is redacted as `display=ssh_command` with fingerprint;
- persistent live publishing is disabled;
- Forgejo HTTP app is reachable on LAN, returning an auth-required status as expected;
- Forgejo SSH port is reachable.

Authenticated repo existence/create was intentionally skipped because this task did not approve reading or using the admin token.

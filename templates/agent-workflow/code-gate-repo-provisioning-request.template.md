# Code-gate repository provisioning request

Use this packet when a project needs a `den-code-gate` repository or access repair before worker submissions.

## Project

- project_id: `<den_project_id>`
- canonical repository: `<git@github.com:OWNER/REPO.git>`
- code-gate owner/org: `<project_id>`
- code-gate repository: `<repo>`
- code-gate remote: `ssh://git@192.168.1.10:3022/<owner>/<repo>.git`
- default branch: `main`

## Ref policy

- immutable ref pattern: `refs/heads/submissions/{project_id}/tasks/{task_id}/runs/{run_id}/attempt-{attempt_ordinal}`
- convenience ref pattern: `refs/heads/submissions/{project_id}/tasks/{task_id}/current`
- `current` is browsing convenience only; it is not publish authority.

## Access requested

- worker submitter access: `<needed/not-needed>` — code-gate-only push access, no canonical push credentials
- reviewer access: `<needed/not-needed>` — code-gate fetch access only
- den-publish service access: `<needed/not-needed>` — service-side code-gate read credential, redacted in `/config/status`

## Preflight commands

```bash
python3 scripts/check-code-gate-repo.py --project <project_id>
python3 scripts/check-code-gate-repo.py --project <project_id> --emit-worker-preflight
python3 scripts/check-promotion-metadata-drift.py --project <project_id>
```

## Authenticated operator action, if approved

```bash
DEN_CODE_GATE_ADMIN_TOKEN=<redacted> \
python3 scripts/check-code-gate-repo.py --project <project_id> --create --create-owner-org
```

Do not paste `DEN_CODE_GATE_ADMIN_TOKEN`, private keys, or full `GIT_SSH_COMMAND` values into Den messages or worker prompts.

## Completion evidence

- repo API check/create result: `<exists/created/skipped>`
- Forgejo HTTP reachability: `<ok>`
- Forgejo SSH reachability: `<ok>`
- den-publish runtime policy: `<ok>`
- code-gate read credential status: `<redacted ssh_command fingerprint/skipped>`
- worker packet generated: `<yes/no>`

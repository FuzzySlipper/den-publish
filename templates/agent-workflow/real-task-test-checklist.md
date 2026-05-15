# Real repo/task test checklist

This checklist prepares a real project task for the first full coder -> reviewer -> publish rehearsal.

## Inputs to collect

- Den project id: `<project_id>`
- Den task id: `<task_id>`
- canonical GitHub remote: `git@github.com:FuzzySlipper/<repo>.git`
- target branch: `task/<task_id>-<slug>`
- allowed changed-file prefixes: `<prefix list>`
- code-gate repo path: `ssh://git@192.168.1.10:3022/<owner_or_project>/<repo>.git`
- base branch and base commit
- coder worker identity/run id

## Preflight commands for operator/sysadmin

- `curl -fsS http://127.0.0.1:5090/readyz`
- `curl -fsS http://127.0.0.1:5090/config/status`
- confirm `/config/status` has `livePublishing.enabled=false` before validate-only prep
- confirm `den-code-gate` HTTP `http://192.168.1.10:3020/` is reachable
- confirm canonical repo exists before any live publish gate

## Dry-run rehearsal sequence

1. Coder posts `den_code_submission` packet.
2. Reviewer receives reviewer context packet and posts review verdict/findings for exact head.
3. Orchestrator creates `den_publish_decision` packet with `validateOnly=true`.
4. Submit the combined decision/submission envelope to `/promotion/dry-run`.
5. Preserve response, audit line count, fetched head, and managed workspace path evidence.
6. Only after dry-run success, decide whether to open a separate live publish approval gate using `docs/live-publish-rehearsal-checklist.md` and `templates/agent-workflow/live-publish-approval-request.template.md`.

## Live publish gate

Live publish is not part of routine prep. If approved:

- enable explicit `ssh_command` credential policy only for the scoped smoke window;
- use the hardened `ssh -F /dev/null ...` form proven in #1424;
- call `/promotion/publish` with `validateOnly=false`;
- verify remote head equals reviewed head;
- delete the smoke branch if the plan says to delete it;
- disable live publishing and verify `/config/status` reports it disabled.

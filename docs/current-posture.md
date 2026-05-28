# Current den-publish posture

`den-publish` is a **specialized / historical credential-isolated promotion lane**. It is not the ordinary default promotion path for trusted local Runner work.

## Current default for ordinary trusted Runner work

For normal Den work in trusted local `/home/dev/<repo>` checkouts, follow the global policy:

```text
local task branch -> tests/build -> Den review looks_good -> no unresolved blocking findings -> direct non-force git promotion -> Den evidence packet
```

Authoritative current guidance:

- Den global policy: `_global/agent-code-promotion-policy`
- Den post-split map: `den-core/den-post-split-forward-path-vs-history-2026-05`

## When this repo is relevant

Use `den-publish`, `den-code-gate`, or Trusted Publisher runbooks only when a task explicitly calls for a credential-isolated, untrusted-worker, server-side publishing, or historical code-gate investigation lane.

Appropriate examples:

- proving or maintaining the historical code-gate workflow;
- experimenting with an untrusted or credential-separated worker submission path;
- validating `den-publish` APIs, policy checks, managed workspaces, or watchdogs;
- responding to a task that explicitly names `den-publish`, `den-code-gate`, Trusted Publisher, or `/promotion/*` endpoints.

Inappropriate examples:

- adding ceremony to ordinary reviewed Runner work only because this repo exists;
- treating old code-gate rollout reports as current default guidance;
- routing a local trusted Runner branch through Forgejo/code-gate unless the task explicitly asks for that lane.

## How to read older docs here

Most docs in this repo were created while code-gate was being explored as a default promotion path. They remain useful evidence and specialized runbooks, but read them as **historical/specialized** unless a newer task explicitly opts back into them.

Historical docs intentionally preserved include:

- `docs/agent-guidance-rollout.md`
- `docs/agent-workflow-ux.md`
- `docs/project-promotion-readiness-checker.md`
- `docs/live-publish-rehearsal-checklist.md`
- `docs/live-publish-runbook.md`
- `docs/code-gate-repo-provisioning.md`
- `docs/promotion-workflow-watchdog.md`
- Den project docs tagged `code-gate`, `trusted-publisher`, or old rollout task ids.

When in doubt, use the Den global policy and post-split map above before using a code-gate runbook.

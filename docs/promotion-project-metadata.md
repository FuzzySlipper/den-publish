# Promotion project metadata and drift checks (#1433)

## Intent

This document defines the secret-free metadata Den needs before an orchestrator can ask the Den Core facade or `den-publish` to validate a code-gate submission. The goal is to make project work portable across machines without `/data/dev`, worker-local checkout, reviewed-bundle, or per-project filesystem shims.

The standard workflow remains:

```text
worker -> den-code-gate immutable ref -> Den review state -> Den Core facade -> den-publish /promotion/dry-run -> later approval-gated /promotion/publish
```

## Source of truth

The repo copy lives at:

```text
config/promotion-projects.json
```

The Den document copy for operators/agents is:

```text
den-publish/promotion-project-metadata-1433
```

No secrets, private key material, tokens, deploy-key contents, or canonical push credentials belong in either location. Credential paths/commands remain service-side and are exposed only through redacted `/config/status` fields and fingerprints.

## Project metadata schema

Each project entry uses `schema=den_promotion_project_inventory`, `schemaVersion=1` and includes:

| Field | Required for dry-run? | Required for live? | Notes |
| --- | --- | --- | --- |
| `projectId` | yes | yes | Den project id and den-publish policy key. |
| `status` | yes | yes | `dry_run_ready` means preflight must pass before a dry-run starts; `metadata_incomplete` is an explicit blocker. |
| `canonicalRemoteUrl` | yes | yes | Expected canonical remote, e.g. `git@github.com:FuzzySlipper/den-channels.git`. No credentials embedded. |
| `targetRemoteName` | yes | yes | Usually `canonical`. |
| `defaultBaseBranch` | yes | yes | Usually `main`. |
| `allowedOperations` | yes | yes | v1 normally `push_branch`; fast-forward operations need explicit policy. |
| `pushBranchPrefixes` | yes for `push_branch` | yes | Usually `task/`. |
| `fastForwardBranches` | if fast-forward is allowed | yes if used | Usually `main`; live use requires separate approval. |
| `codeGateInstance` | yes | yes | Usually `den-code-gate`. |
| `codeGateRemoteUrl` | yes | yes | Service-readable code-gate repo URL. No secret in URL. |
| `codeGateRepo` | recommended | recommended | Human-facing repo path/name for provisioning checks. |
| `immutableRefPattern` | yes | yes | Authoritative submission ref pattern. |
| `convenienceRefPattern` | recommended | recommended | Browsing only; never publish authority. |
| `allowedPathPrefixes` | yes | yes | Empty array means no project-specific prefix narrowing beyond task/review policy. |
| `dryRunRequires` | yes | yes | Human-readable checklist. |
| `livePublishRequires` | no | yes | Human-readable approval/rollback checklist. |

## Current inventory snapshot

### `den-channels`

Status: `dry_run_ready`.

Known metadata:

- canonical remote: `git@github.com:FuzzySlipper/den-channels.git`
- code-gate remote: `ssh://git@192.168.1.10:3022/den-channels/den-channels.git`
- target remote: `canonical`
- default base branch: `main`
- allowed push branch prefix: `task/`
- live fast-forward branch policy: `main`
- service-side code-gate read route: present in `/config/status` as redacted `ssh_command` fingerprint only
- persistent live publishing: must remain disabled outside approved smoke windows

This is enough to build a Den Core facade dry-run request without guessing; the actual task-specific values still come from the coder submission/review packets:

```text
submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id> target=<target_branch>
```

### `den-publish`

Status: `metadata_incomplete`.

Known canonical remote from service policy/docs: `git@github.com:FuzzySlipper/den-publish.git`.

Current blocker: no project-specific code-gate repo/read route is recorded in the inventory or live `/config/status`. The local `/home/dev/den-publish` checkout also currently has no `origin` remote configured, so repo-local metadata cannot be trusted alone.

### `den-core`

Status: `metadata_incomplete`.

Known canonical remote from `/home/dev/den-core`: `git@github.com:FuzzySlipper/den-core.git`.

Current blocker: no code-gate repo/read route is recorded in the inventory or live `/config/status`.

### `den-mcp`

Status: `metadata_incomplete`.

Known canonical remote from `/home/dev/den-mcp`: `git@github.com:FuzzySlipper/den-mcp.git`.

Current blocker: no code-gate repo/read route is recorded in the inventory or live `/config/status`.

## Drift checker

Run:

```bash
python3 scripts/check-promotion-metadata-drift.py
```

Useful options:

```bash
# Check only the ready project before a dry-run
python3 scripts/check-promotion-metadata-drift.py --project den-channels

# Generate a field-level Den Core facade skeleton for the ready project
python3 scripts/check-promotion-metadata-drift.py --emit-dry-run-skeleton den-channels

# Use a captured status payload instead of live HTTP
python3 scripts/check-promotion-metadata-drift.py --status-file /tmp/den-publish-config-status.json
```

The checker compares the desired inventory with the redacted live service surface:

```text
GET http://127.0.0.1:5090/config/status
```

It fails before dry-run when:

- a `dry_run_ready` project lacks `canonicalRemoteUrl`, `targetRemoteName`, `defaultBaseBranch`, or `codeGateRemoteUrl`;
- the project policy is missing from `/config/status`;
- runtime remote fingerprints drift from inventory values;
- runtime target branch policy drifts from inventory values;
- service-side code-gate read route is absent for a dry-run-ready project;
- the persistent service reports `livePublishing.enabled=true` outside a live approval window;
- `/config/status` is not the `den-publish-runtime-config-v2` contract.

By default the checker does not read credential stores and does not run `git ls-remote`. Optional active code-gate probing requires the operator to inject an explicit per-project SSH command into the environment, for example:

```bash
DEN_PUBLISH_DRIFT_CODE_GATE_SSH_COMMAND_DEN_CHANNELS='<hardened ssh command>' \
  python3 scripts/check-promotion-metadata-drift.py --project den-channels --probe-code-gate
```

The checker never prints the supplied command.

## Dry-run vs live boundary

Dry-run readiness requires only secret-free metadata plus service-side read access to the code-gate repo. The actual dry-run still requires exact task-local metadata:

- `submission_id`
- immutable `ingress_ref`
- full `base_commit`
- full `head_commit`
- `review_round_id` with `looks_good`
- target branch
- allowed path prefixes / structured scope overrides if applicable

Live publish additionally requires:

- explicit approval for a bounded live smoke/publish window;
- `/promotion/dry-run` success for the exact submission/head/review round;
- `DenPublish:Publishing:Enabled=true` only during the approved window;
- explicit live credential policy (`ssh_command`/`GIT_SSH_COMMAND`, `GIT_TERMINAL_PROMPT=0`, hardened SSH options) visible only as redacted status/fingerprint;
- canonical branch verification after push;
- rollback or branch deletion plan;
- live publishing disabled and verified afterward.

## Handoff to code-gate provisioning (#1436)

Projects with `metadata_incomplete` are not ready for cross-machine development through the standard path. The next required step is code-gate provisioning: create or verify the repo, establish worker submission/reviewer fetch access, configure the service-side read route, and then update this inventory plus `/config/status` policy.

## Code-gate repository provisioning link (#1436)

Repository ownership, creation, and worker/reviewer access preflights are maintained in `docs/code-gate-repo-provisioning.md` and `config/code-gate-repositories.json`. Run `python3 scripts/check-code-gate-repo.py --project <project_id>` before launching a promotion-aware worker.

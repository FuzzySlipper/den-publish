# Project promotion readiness checker (#1441)

`check-project-promotion-readiness.py` is the one-command, no-secret preflight for the default Den code workflow:

```text
worker -> den-code-gate immutable ref -> Den review state -> Den Core MCP facade -> den-publish dry-run -> approval-gated publish
```

It classifies whether a Den project can use the standard workflow without local filesystem shims or project-specific hacks.

## Standard use

```bash
python3 scripts/check-project-promotion-readiness.py --project den-channels
```

Machine-readable output:

```bash
python3 scripts/check-project-promotion-readiness.py --project den-channels --json
```

Exit codes:

| Code | Meaning |
| --- | --- |
| `0` | Ready, or explicitly not applicable. |
| `1` | Blocked/fail-closed condition, such as live publishing enabled or missing MCP facade tool. |
| `2` | Not ready but actionable, such as missing code-gate inventory or runtime policy. |

## Inputs

By default the checker reads:

- `config/promotion-projects.json`
- `config/code-gate-repositories.json`
- `http://127.0.0.1:5090/config/status`
- public MCP facade `http://192.168.1.10:5199/mcp`

The MCP facade is used for both:

- `tools/list`, to verify `request_den_publish_dry_run` exists;
- `tools/call list_projects`, to verify Den project existence/root path.

Offline or test runs can supply captured inputs:

```bash
python3 scripts/check-project-promotion-readiness.py \
  --project den-core \
  --promotion-inventory /tmp/promotion-projects.json \
  --code-gate-inventory /tmp/code-gate-repositories.json \
  --status-file /tmp/den-publish-config-status.json \
  --mcp-tools-file /tmp/mcp-tools.json \
  --den-projects-file /tmp/den-projects.json \
  --no-subchecks \
  --json
```

## Classifications

| Classification | Meaning | Typical next step |
| --- | --- | --- |
| `ready` | Metadata, code-gate inventory, runtime policy, MCP facade, Den project, and live-disabled checks are good. | Run validate-only dry-run proof when a reviewed immutable submission exists. |
| `needs_metadata` | Project exists but is missing promotion metadata. | Add secret-free canonical/code-gate policy metadata. |
| `needs_code_gate` | Project lacks code-gate repo inventory or code-gate fields. | Prepare approval packet for Forgejo repo/access provisioning. |
| `needs_runtime_policy` | Inventory exists but `den-publish` runtime policy is missing. | Add indexed persistent service policy after approval. |
| `blocked` | Fail-closed condition. | Fix blocker before worker/reviewer/dry-run use. |
| `not_applicable` | Den project is not a standalone code project, either because it has no root or because `config/promotion-projects.json` marks it under `nonPromotionTargets`. | Do not onboard unless scope changes; route work through the owning repo when supplied. |

## Fail-closed checks

The checker blocks if:

- `den-publish /config/status` is missing or not `den-publish-runtime-config-v2`;
- persistent `livePublishing.enabled=true`;
- live credential policy is configured outside a scoped approval window;
- the public MCP facade does not expose `request_den_publish_dry_run`;
- required canonical metadata fields are absent.

Projects listed in top-level `nonPromotionTargets[]` are classified as `not_applicable` without requiring Forgejo/runtime approval; include a `reason` and, when applicable, `routeThroughProjectId`.

## Subchecks

Unless `--no-subchecks` is supplied, the wrapper also runs existing no-secret validators:

```bash
python3 scripts/check-promotion-metadata-drift.py --project <project_id>
python3 scripts/check-code-gate-repo.py --project <project_id>
```

`check-code-gate-repo.py` authenticated Forgejo lookup remains optional and approval-gated. A skipped admin-token lookup is a warning, not a readiness failure, when all runtime and inventory checks otherwise pass.


## Workspace and SSH preflight

`check-project-promotion-readiness.py` classifies project-level workflow readiness. When a specific managed workspace exists or a promotion fails with Git/SSH workspace symptoms, also run the read-only workspace preflight:

```bash
python3 scripts/check-workspace-preflight.py   --workspace /home/agents/runtime/den-publish/workspaces/<project_id>   --expected-owner agent   --json
```

This checker does not repair ownership, change permissions, read credential contents, or contact remotes. It reports:

- mixed ownership under the managed workspace;
- `.git` / `.git/config` ownership that can cause `config.lock` or ref-lock failures;
- `.ssh/config` symlinks that need target review;
- OpenSSH config paths or symlink targets writable by group/other.

A blocked workspace/SSH preflight is not warning-eligible under `audit_warn`; fix or recreate the managed workspace before retrying fetch/publish.

## Secret boundary

The checker does not read Forgejo admin tokens, private keys, canonical push credentials, or deploy-key material by default. It does not create repos, mutate service config, or publish code. Any command output included in JSON is limited to existing checker stdout tails and should not contain secrets.

# Promotion workflow watchdog

`scripts/check-promotion-workflow-watchdog.py` is the quiet, secret-free drift check for the default Den code promotion path:

```text
worker -> den-code-gate immutable ref -> Den review state -> Den Core MCP facade -> den-publish dry-run -> approval-gated live publish
```

It is intended for script-only scheduled jobs and manual operator diagnosis. It does not mutate Den, Git, Forgejo, systemd, service config, credentials, or canonical remotes. Subprocess checks run with credential-shaped environment variables scrubbed (`DEN_CODE_GATE_*`, `GH_*`, `GITHUB_*`, token/secret/password/credential/private-key names, and `SSH_AUTH_SOCK`) so a watchdog run cannot accidentally consume provisioning or canonical Git credentials inherited from an operator shell.

## Scheduled-job behavior

Default mode is quiet on success:

```bash
python3 scripts/check-promotion-workflow-watchdog.py
```

- exit `0`, stdout empty: healthy; do not alert
- non-zero exit with stdout: alert on the printed findings

Use `--verbose` for a human OK line and `--json` for structured output:

```bash
python3 scripts/check-promotion-workflow-watchdog.py --verbose
python3 scripts/check-promotion-workflow-watchdog.py --json
python3 scripts/check-promotion-workflow-watchdog.py --allow-live-enabled --json
```

After #1468 live enablement, persistent live publishing is expected only when the operator intentionally opts into approved-live monitoring:

```bash
python3 scripts/check-promotion-workflow-watchdog.py --allow-live-enabled --json
```

Without `--allow-live-enabled`, the watchdog remains fail-closed and reports persistent live publishing/credentials as drift. This gives scheduled jobs a deliberate choice between pre-live safety policy and post-live approved-production policy.

## Checks performed

The watchdog verifies:

- `den-publish-den-core-tunnel.service` is active and enabled as a system service;
- local `den-publish` `/config/status` is reachable and reports `den-publish-runtime-config-v2`;
- default mode fails closed when `livePublishing.enabled=true`;
- default mode fails closed when `liveCredentialPolicy.configured=true`;
- approved-live mode (`--allow-live-enabled`) accepts persistent live publishing only when `/config/status` reports an explicit `ssh_command` credential policy with value `[redacted]` and a fingerprint;
- the public Den MCP facade exposes `request_den_publish_dry_run`;
- monitored promotion inventory projects match runtime project policy;
- `scripts/check-promotion-metadata-drift.py` reports no errors;
- each monitored project is `ready` according to `scripts/check-project-promotion-readiness.py --json`;
- each monitored project passes `scripts/check-code-gate-repo.py --project <project>` without errors.

Monitored project statuses are currently:

- `dry_run_ready`
- `code_gate_repo_and_read_key_provisioned`

This intentionally excludes metadata-incomplete/deferred projects such as RuleWeaver while still checking first-batch projects whose status reflects code-gate/read-route provisioning rather than fresh dry-run proof state.

`--project <project_id>` narrows the expensive per-project readiness/code-gate subchecks, but metadata/runtime alignment is still computed against the full monitored project set so valid sibling runtime policies are not reported as extras.

## Failure interpretation

Common findings:

| Component | Code | Meaning | First response |
|---|---|---|---|
| `systemd` | `service_not_active` / `service_not_enabled` | Den Core may not be able to reach den-publish through the reverse tunnel. | Inspect `systemctl status den-publish-den-core-tunnel.service`; do not edit unit files without approval. |
| `den_publish_status` | `unreachable` | Local den-publish API is down or not listening on the expected loopback port. | Check `den-publish.service` logs/status. |
| `den_publish_status` | `live_publishing_enabled` | Live publishing is enabled while the checker is running in default fail-closed mode. | If #1468 production live mode is approved, rerun with `--allow-live-enabled`; otherwise rollback/disable live. |
| `den_publish_status` | `live_credentials_configured` | Live canonical credentials are configured while the checker is running in default fail-closed mode. | If #1468 production live mode is approved, rerun with `--allow-live-enabled`; otherwise rollback/disable live. |
| `den_publish_status` | `live_credentials_not_explicit_redacted` | Approved-live mode saw live enabled but the credential policy was not explicit/redacted/fingerprinted. | Stop publishing and inspect service env/status; do not continue until status redaction is restored. |
| `mcp_facade` | `dry_run_tool_missing` | Den Core MCP facade no longer exposes the standard dry-run tool. | Inspect Den Core deployment/config before launching promotion work. |
| `metadata` | `runtime_policy_missing` | A monitored inventory project is absent from den-publish runtime policy. | Re-run metadata drift checker; do not hot-patch service config without an approval plan. |
| `readiness` | `project_unready` / `project_not_ready` | A per-project readiness preflight no longer classifies ready. | Run `python3 scripts/check-project-promotion-readiness.py --project <project> --json`. |
| `code_gate` | `subcheck_failed` | Code-gate inventory/runtime/reachability preflight failed for a project. | Run `python3 scripts/check-code-gate-repo.py --project <project>` and inspect component findings. |

## Cron/no-agent example

A Hermes no-agent cron job can run the script directly and alert only when stdout is non-empty or the process exits non-zero. Example prompt/tool settings should use the script itself as the delivered output, not an LLM summarizer.

```bash
cd /home/dev/den-publish
python3 scripts/check-promotion-workflow-watchdog.py
```

Do not enable a cron/systemd timer as part of code changes without a separate operator approval step.

## Manual verification

Current rollout verification command:

```bash
python3 -m pytest tests/test_promotion_workflow_watchdog.py tests/test_project_promotion_readiness.py -q
python3 scripts/check-promotion-workflow-watchdog.py --json
python3 scripts/check-promotion-workflow-watchdog.py --allow-live-enabled --json
```

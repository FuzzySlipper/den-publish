#!/usr/bin/env python3
"""Quiet watchdog for the Den code-gate -> den-publish promotion workflow.

Default output is watchdog-friendly: print nothing and return 0 when healthy;
print actionable findings and return non-zero when drift or failures are detected.
The checker is secret-free and never mutates Git, Forgejo, Den, systemd, or service config.
"""
from __future__ import annotations

import argparse
import json
import os
import pwd
import subprocess
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_STATUS_URL = "http://127.0.0.1:5090/config/status"
DEFAULT_MCP_URL = "http://192.168.1.10:5199/mcp"
DEFAULT_TUNNEL_SERVICE = "den-publish-den-core-tunnel.service"
DEFAULT_PROMOTION_INVENTORY = ROOT / "config" / "promotion-projects.json"
DEFAULT_CODE_GATE_INVENTORY = ROOT / "config" / "code-gate-repositories.json"


@dataclass(frozen=True)
class Finding:
    severity: str
    component: str
    code: str
    message: str
    detail: str | None = None

    def line(self) -> str:
        suffix = f" ({self.detail})" if self.detail else ""
        return f"{self.severity.upper()} {self.component} {self.code}: {self.message}{suffix}"


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise RuntimeError(f"missing JSON file: {path}") from exc
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"invalid JSON in {path}: {exc}") from exc


def fetch_json(url: str, *, timeout: int = 10) -> Any:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:  # noqa: S310 - LAN/operator URL
            raw = response.read().decode("utf-8")
    except urllib.error.URLError as exc:
        raise RuntimeError(f"failed to fetch {url}: {exc}") from exc
    try:
        return json.loads(raw)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"non-JSON response from {url}: {exc}") from exc


def sanitized_environment() -> dict[str, str]:
    sensitive_markers = ("TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "PRIVATE_KEY", "SSH_AUTH_SOCK")
    return {
        key: value
        for key, value in os.environ.items()
        if not key.startswith("DEN_CODE_GATE_")
        and not key.startswith("GITHUB_")
        and not key.startswith("GH_")
        and not any(marker in key.upper() for marker in sensitive_markers)
    }


def run_command(command: list[str], *, timeout: int = 20) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=sanitized_environment(),
        timeout=timeout,
        check=False,
    )


def systemctl_command(scope: str, service_user: str | None, *args: str) -> list[str]:
    if scope == "system":
        return ["systemctl", *args]
    if scope != "user":
        raise ValueError(f"unsupported systemd scope: {scope}")
    if service_user:
        uid = pwd.getpwnam(service_user).pw_uid
        env = f"XDG_RUNTIME_DIR=/run/user/{uid}"
        if os.geteuid() == uid:
            return ["env", env, "systemctl", "--user", *args]
        return ["sudo", "-n", "-u", service_user, "--", "env", env, "systemctl", "--user", *args]
    return ["systemctl", "--user", *args]


def check_systemd_service(scope: str, service_name: str, service_user: str | None) -> list[Finding]:
    findings: list[Finding] = []
    for mode, expected in [("is-active", "active"), ("is-enabled", "enabled")]:
        try:
            proc = run_command(systemctl_command(scope, service_user, mode, service_name), timeout=10)
        except Exception as exc:  # pragma: no cover - defensive around host-specific systemctl failures
            findings.append(Finding("error", "systemd", f"{mode}_check_failed", f"could not run systemctl {mode}", str(exc)))
            continue
        value = (proc.stdout or proc.stderr).strip().splitlines()[0] if (proc.stdout or proc.stderr).strip() else f"rc={proc.returncode}"
        if proc.returncode != 0 or value != expected:
            findings.append(Finding(
                "error",
                "systemd",
                f"service_not_{expected}",
                f"{service_name} is not {expected}",
                f"scope={scope} value={value}",
            ))
    return findings


def parse_mcp_response(raw: str) -> dict[str, Any]:
    if raw.lstrip().startswith("{"):
        return json.loads(raw)
    for block in raw.strip().split("\n\n"):
        data_lines = [line[5:].strip() for line in block.splitlines() if line.startswith("data:")]
        if data_lines:
            return json.loads("\n".join(data_lines))
    raise RuntimeError("MCP response did not contain JSON or SSE data")


def mcp_post(url: str, payload: dict[str, Any], session_id: str | None = None) -> tuple[str | None, dict[str, Any]]:
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if session_id:
        headers["Mcp-Session-Id"] = session_id
    request = urllib.request.Request(url, data=json.dumps(payload).encode("utf-8"), headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=15) as response:  # noqa: S310 - LAN/operator URL
            return response.headers.get("Mcp-Session-Id"), parse_mcp_response(response.read().decode("utf-8"))
    except urllib.error.URLError as exc:
        raise RuntimeError(f"failed to call MCP facade {url}: {exc}") from exc


def check_mcp_facade(url: str) -> list[Finding]:
    try:
        session_id, _ = mcp_post(url, {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "den-publish-workflow-watchdog", "version": "1"},
            },
        })
        _, parsed = mcp_post(url, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, session_id)
    except Exception as exc:
        return [Finding("error", "mcp_facade", "unreachable", "public MCP facade could not be queried", str(exc))]
    tools = parsed.get("result", {}).get("tools", []) if isinstance(parsed, dict) else []
    if not any(isinstance(tool, dict) and tool.get("name") == "request_den_publish_dry_run" for tool in tools):
        return [Finding("error", "mcp_facade", "dry_run_tool_missing", "request_den_publish_dry_run is not exposed")]
    return []


def live_credential_policy_is_explicit_and_redacted(status: dict[str, Any]) -> bool:
    credential = status.get("liveCredentialPolicy", {})
    return (
        credential.get("configured") is True
        and credential.get("display") == "ssh_command"
        and credential.get("value") == "[redacted]"
        and bool(credential.get("fingerprint"))
    )


def check_status_payload(status: Any, *, allow_live_enabled: bool = False) -> list[Finding]:
    findings: list[Finding] = []
    if not isinstance(status, dict) or status.get("configurationContract") != "den-publish-runtime-config-v2":
        findings.append(Finding("error", "den_publish_status", "bad_contract", "den-publish /config/status is not runtime config v2"))
        return findings
    live_enabled = status.get("livePublishing", {}).get("enabled") is True
    live_credential_configured = status.get("liveCredentialPolicy", {}).get("configured") is True
    if allow_live_enabled:
        if not live_enabled:
            findings.append(Finding("error", "den_publish_status", "live_publishing_not_enabled", "approved-live mode requires livePublishing.enabled=true"))
        elif not live_credential_policy_is_explicit_and_redacted(status):
            findings.append(Finding("error", "den_publish_status", "live_credentials_not_explicit_redacted", "live publishing is enabled but credential policy is not explicit ssh_command with redacted value and fingerprint"))
    else:
        if live_enabled:
            findings.append(Finding("error", "den_publish_status", "live_publishing_enabled", "livePublishing.enabled=true outside an approval window"))
        if live_credential_configured:
            findings.append(Finding("error", "den_publish_status", "live_credentials_configured", "live credential policy is configured outside an approval window"))
    return findings


def check_status(url: str, *, allow_live_enabled: bool = False) -> tuple[dict[str, Any] | None, list[Finding]]:
    try:
        status = fetch_json(url)
    except Exception as exc:
        return None, [Finding("error", "den_publish_status", "unreachable", "den-publish /config/status is unreachable", str(exc))]
    return status, check_status_payload(status, allow_live_enabled=allow_live_enabled)


MONITORED_PROJECT_STATUSES = {"dry_run_ready", "code_gate_repo_and_read_key_provisioned"}


def monitored_projects(inventory: dict[str, Any]) -> list[str]:
    projects = inventory.get("projects", []) if isinstance(inventory, dict) else []
    return sorted(
        str(project.get("projectId"))
        for project in projects
        if isinstance(project, dict) and project.get("status") in MONITORED_PROJECT_STATUSES
    )


def runtime_project_ids(status: dict[str, Any] | None) -> set[str]:
    if not isinstance(status, dict):
        return set()
    return {
        str(policy.get("projectId"))
        for policy in status.get("projectPolicies", []) or []
        if isinstance(policy, dict) and policy.get("projectId")
    }


def check_runtime_inventory_alignment(project_ids: list[str], status: dict[str, Any] | None) -> list[Finding]:
    runtime = runtime_project_ids(status)
    desired = set(project_ids)
    findings: list[Finding] = []
    missing = sorted(desired - runtime)
    extra = sorted(runtime - desired)
    if missing:
        findings.append(Finding("error", "metadata", "runtime_policy_missing", "dry-run-ready projects missing from runtime policy", ",".join(missing)))
    if extra:
        findings.append(Finding("warning", "metadata", "runtime_policy_extra", "runtime has policies not marked dry-run-ready in inventory", ",".join(extra)))
    return findings


def run_subcheck(component: str, command: list[str], *, warning_is_failure: bool = False) -> list[Finding]:
    proc = run_command(command, timeout=90)
    if proc.returncode == 0:
        return []
    severity = "error" if warning_is_failure or proc.returncode == 1 else "warning"
    tail = "\n".join((proc.stdout + proc.stderr).splitlines()[-8:])
    return [Finding(severity, component, "subcheck_failed", f"subcheck exited {proc.returncode}: {' '.join(command)}", tail)]


def check_project_readiness(project_id: str, *, allow_live_enabled: bool = False) -> list[Finding]:
    proc = run_command([
        sys.executable,
        "scripts/check-project-promotion-readiness.py",
        "--project",
        project_id,
        "--json",
        *(["--allow-live-enabled"] if allow_live_enabled else []),
    ], timeout=120)
    if proc.returncode != 0:
        tail = "\n".join((proc.stdout + proc.stderr).splitlines()[-12:])
        return [Finding("error", "readiness", "project_unready", f"{project_id} readiness check exited {proc.returncode}", tail)]
    try:
        payload = json.loads(proc.stdout)
    except json.JSONDecodeError as exc:
        return [Finding("error", "readiness", "bad_json", f"{project_id} readiness check did not emit JSON", str(exc))]
    if payload.get("classification") != "ready" or payload.get("ready") is not True:
        return [Finding("error", "readiness", "project_not_ready", f"{project_id} classification is {payload.get('classification')}")]
    return []


def collect_findings(args: argparse.Namespace) -> tuple[list[Finding], dict[str, Any]]:
    findings: list[Finding] = []
    summary: dict[str, Any] = {"schema": "den_publish_promotion_workflow_watchdog", "schemaVersion": 1}

    promotion_inventory = load_json(args.promotion_inventory)
    all_project_ids = monitored_projects(promotion_inventory)
    project_ids = all_project_ids
    if args.project:
        project_ids = [project for project in all_project_ids if project == args.project]
        if not project_ids:
            findings.append(Finding("error", "metadata", "project_not_monitored", f"{args.project} is not in a monitored promotion inventory status"))
    summary["projectsChecked"] = project_ids
    summary["monitoredProjects"] = all_project_ids

    findings.extend(check_systemd_service(args.systemd_scope, args.tunnel_service, args.service_user))
    status, status_findings = check_status(args.status_url, allow_live_enabled=args.allow_live_enabled)
    findings.extend(status_findings)
    findings.extend(check_runtime_inventory_alignment(all_project_ids, status))
    findings.extend(check_mcp_facade(args.mcp_url))

    findings.extend(run_subcheck("metadata", [sys.executable, "scripts/check-promotion-metadata-drift.py", *(["--allow-live-enabled"] if args.allow_live_enabled else [])], warning_is_failure=True))
    for project_id in project_ids:
        findings.extend(check_project_readiness(project_id, allow_live_enabled=args.allow_live_enabled))
        findings.extend(run_subcheck("code_gate", [sys.executable, "scripts/check-code-gate-repo.py", "--project", project_id, *(["--allow-live-enabled"] if args.allow_live_enabled else [])]))

    summary["findingCount"] = len(findings)
    summary["errorCount"] = sum(1 for finding in findings if finding.severity == "error")
    summary["warningCount"] = sum(1 for finding in findings if finding.severity == "warning")
    summary["statusUrl"] = args.status_url
    summary["mcpUrl"] = args.mcp_url
    summary["tunnelService"] = args.tunnel_service
    summary["systemdScope"] = args.systemd_scope
    return findings, summary


def main() -> int:
    parser = argparse.ArgumentParser(description="Quiet promotion workflow drift watchdog")
    parser.add_argument("--promotion-inventory", type=Path, default=DEFAULT_PROMOTION_INVENTORY)
    parser.add_argument("--code-gate-inventory", type=Path, default=DEFAULT_CODE_GATE_INVENTORY, help="reserved for symmetry with other preflights")
    parser.add_argument("--status-url", default=DEFAULT_STATUS_URL)
    parser.add_argument("--mcp-url", default=DEFAULT_MCP_URL)
    parser.add_argument("--tunnel-service", default=DEFAULT_TUNNEL_SERVICE)
    parser.add_argument("--systemd-scope", choices=["system", "user"], default="system")
    parser.add_argument("--service-user", default=None, help="user for --systemd-scope=user checks")
    parser.add_argument("--project", help="limit project readiness checks to one dry-run-ready project")
    parser.add_argument("--allow-live-enabled", action="store_true", help="accept persistent live publishing only when an explicit redacted ssh_command credential policy is configured")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--verbose", action="store_true", help="print an OK summary when healthy")
    args = parser.parse_args()

    try:
        findings, summary = collect_findings(args)
    except Exception as exc:
        findings = [Finding("error", "watchdog", "exception", "watchdog failed before completing checks", str(exc))]
        summary = {"schema": "den_publish_promotion_workflow_watchdog", "schemaVersion": 1, "findingCount": 1, "errorCount": 1, "warningCount": 0}

    errors = [finding for finding in findings if finding.severity == "error"]
    if args.json:
        payload = dict(summary)
        payload["ok"] = not errors
        payload["findings"] = [asdict(finding) for finding in findings]
        print(json.dumps(payload, indent=2, sort_keys=True))
    elif errors or args.verbose:
        if not findings:
            print(f"promotion_workflow_watchdog=ok projects={len(summary.get('projectsChecked', []))}")
        else:
            for finding in findings:
                print(finding.line())
            print(
                "promotion_workflow_watchdog="
                f"{'fail' if errors else 'ok'} errors={len(errors)} warnings={summary.get('warningCount', 0)} "
                f"projects={len(summary.get('projectsChecked', []))}"
            )
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())

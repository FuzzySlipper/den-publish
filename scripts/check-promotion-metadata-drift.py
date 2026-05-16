#!/usr/bin/env python3
"""Validate Den promotion project metadata and compare it with den-publish runtime status.

This checker is intentionally secret-free. It reads a desired metadata inventory from
config/promotion-projects.json, fetches the redacted den-publish /config/status surface,
and reports drift before a dry-run request is built.

Optional active code-gate probing is supported only when an operator supplies an
explicit per-project GIT_SSH_COMMAND through the environment; the command is never
printed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INVENTORY = ROOT / "config" / "promotion-projects.json"
DEFAULT_STATUS_URL = "http://127.0.0.1:5090/config/status"

REQUIRED_PROJECT_FIELDS = [
    "projectId",
    "status",
    "canonicalRemoteUrl",
    "targetRemoteName",
    "defaultBaseBranch",
    "allowedOperations",
    "pushBranchPrefixes",
    "fastForwardBranches",
    "codeGateInstance",
    "immutableRefPattern",
    "convenienceRefPattern",
    "allowedPathPrefixes",
]

DRY_RUN_REQUIRED_FIELDS = [
    "canonicalRemoteUrl",
    "targetRemoteName",
    "defaultBaseBranch",
    "codeGateRemoteUrl",
]


@dataclass(frozen=True)
class Finding:
    severity: str
    project_id: str
    code: str
    message: str

    def format(self) -> str:
        return f"{self.severity.upper()} {self.project_id} {self.code}: {self.message}"


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise SystemExit(f"missing JSON file: {path}")
    except json.JSONDecodeError as exc:
        raise SystemExit(f"invalid JSON in {path}: {exc}")


def fetch_status(url: str) -> dict[str, Any]:
    try:
        with urllib.request.urlopen(url, timeout=10) as response:
            payload = response.read().decode("utf-8")
    except urllib.error.URLError as exc:
        raise SystemExit(f"failed to fetch den-publish config status from {url}: {exc}")
    data = json.loads(payload)
    if not isinstance(data, dict):
        raise SystemExit("den-publish config status was not a JSON object")
    return data


def fingerprint(value: str | None) -> str | None:
    if not value:
        return None
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]


def project_status_by_id(status: dict[str, Any]) -> dict[str, dict[str, Any]]:
    policies = status.get("projectPolicies", [])
    if not isinstance(policies, list):
        return {}
    return {
        str(policy.get("projectId")): policy
        for policy in policies
        if isinstance(policy, dict) and policy.get("projectId")
    }


def setting_configured(setting: Any) -> bool:
    return isinstance(setting, dict) and bool(setting.get("configured"))


def setting_fingerprint(setting: Any) -> str | None:
    if isinstance(setting, dict):
        raw = setting.get("fingerprint")
        return raw if isinstance(raw, str) and raw else None
    return None


def live_credential_policy_is_explicit_and_redacted(status: dict[str, Any]) -> bool:
    credential = status.get("liveCredentialPolicy", {})
    return (
        credential.get("configured") is True
        and credential.get("display") == "ssh_command"
        and credential.get("value") == "[redacted]"
        and bool(credential.get("fingerprint"))
    )


def list_value(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []
    return [str(item) for item in value]


def validate_inventory_shape(inventory: dict[str, Any]) -> list[Finding]:
    findings: list[Finding] = []
    if inventory.get("schema") != "den_promotion_project_inventory":
        findings.append(Finding("error", "_inventory", "bad_schema", "schema must be den_promotion_project_inventory"))
    if inventory.get("schemaVersion") != 1:
        findings.append(Finding("error", "_inventory", "bad_schema_version", "schemaVersion must be 1"))
    projects = inventory.get("projects")
    if not isinstance(projects, list) or not projects:
        findings.append(Finding("error", "_inventory", "projects_missing", "projects must be a non-empty array"))
        return findings

    seen: set[str] = set()
    for project in projects:
        project_id = str(project.get("projectId", "<missing>")) if isinstance(project, dict) else "<invalid>"
        if not isinstance(project, dict):
            findings.append(Finding("error", project_id, "project_not_object", "project entry must be an object"))
            continue
        if project_id in seen:
            findings.append(Finding("error", project_id, "duplicate_project", "projectId appears more than once"))
        seen.add(project_id)
        for field in REQUIRED_PROJECT_FIELDS:
            if field not in project:
                findings.append(Finding("error", project_id, "metadata_field_missing", f"missing required field {field}"))
        if project.get("status") == "dry_run_ready":
            for field in DRY_RUN_REQUIRED_FIELDS:
                if not project.get(field):
                    findings.append(Finding("error", project_id, "dry_run_metadata_missing", f"dry-run-ready project missing {field}"))
        if project.get("codeGateRemoteUrl") and project.get("codeGateInstance") != "den-code-gate":
            findings.append(Finding("warning", project_id, "unexpected_code_gate", "codeGateInstance should normally be den-code-gate"))
    return findings


def compare_project_to_status(project: dict[str, Any], status_policy: dict[str, Any] | None) -> list[Finding]:
    project_id = str(project["projectId"])
    findings: list[Finding] = []
    dry_run_ready = project.get("status") == "dry_run_ready"

    if status_policy is None:
        severity = "error" if dry_run_ready else "warning"
        findings.append(Finding(severity, project_id, "runtime_policy_missing", "project policy missing from den-publish /config/status"))
        return findings

    checks = [
        ("canonicalRemoteUrl", "canonicalRemoteUrl"),
        ("codeGateRemoteUrl", "codeGateRemoteUrl"),
        ("targetRemoteName", "targetRemoteName"),
    ]
    for desired_field, status_field in checks:
        desired = project.get(desired_field)
        status_setting = status_policy.get(status_field)
        if not desired:
            if dry_run_ready:
                findings.append(Finding("error", project_id, f"{desired_field}_missing", f"inventory missing {desired_field}"))
            continue
        if not setting_configured(status_setting):
            findings.append(Finding("error", project_id, f"{status_field}_not_configured", f"/config/status does not configure {status_field}"))
            continue
        if desired_field in {"canonicalRemoteUrl", "codeGateRemoteUrl"}:
            expected = fingerprint(str(desired))
            actual = setting_fingerprint(status_setting)
            if actual and expected and actual != expected:
                findings.append(Finding("error", project_id, f"{status_field}_drift", f"/config/status fingerprint {actual} does not match inventory fingerprint {expected}"))

    if dry_run_ready and not setting_configured(status_policy.get("codeGateReadCredential")):
        findings.append(Finding("error", project_id, "code_gate_read_route_missing", "dry-run-ready project lacks a configured service-side code-gate read route"))

    desired_push_prefixes = set(list_value(project.get("pushBranchPrefixes")))
    live_push_prefixes = set(list_value(status_policy.get("pushBranchPrefixes")))
    if desired_push_prefixes and desired_push_prefixes != live_push_prefixes:
        findings.append(Finding("error", project_id, "push_branch_prefix_drift", f"runtime pushBranchPrefixes={sorted(live_push_prefixes)} inventory={sorted(desired_push_prefixes)}"))

    desired_ff = set(list_value(project.get("fastForwardBranches")))
    live_ff = set(list_value(status_policy.get("fastForwardBranches")))
    if desired_ff and desired_ff != live_ff:
        findings.append(Finding("error", project_id, "fast_forward_branch_drift", f"runtime fastForwardBranches={sorted(live_ff)} inventory={sorted(desired_ff)}"))

    return findings


def code_gate_env_name(project_id: str) -> str:
    safe = "".join(ch if ch.isalnum() else "_" for ch in project_id).upper()
    return f"DEN_PUBLISH_DRIFT_CODE_GATE_SSH_COMMAND_{safe}"


def probe_code_gate(project: dict[str, Any]) -> list[Finding]:
    project_id = str(project["projectId"])
    remote = project.get("codeGateRemoteUrl")
    if not remote:
        return []
    env_name = code_gate_env_name(project_id)
    ssh_command = os.environ.get(env_name)
    if not ssh_command:
        return [Finding("warning", project_id, "code_gate_probe_skipped", f"active code-gate probe skipped; set {env_name} to run git ls-remote without printing credentials")]
    env = os.environ.copy()
    env["GIT_SSH_COMMAND"] = ssh_command
    env["GIT_TERMINAL_PROMPT"] = "0"
    proc = subprocess.run(
        ["git", "ls-remote", "--heads", str(remote)],
        text=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        env=env,
        timeout=20,
        check=False,
    )
    if proc.returncode != 0:
        return [Finding("error", project_id, "code_gate_unreachable", "git ls-remote failed using supplied code-gate SSH command")]
    return []


def dry_run_skeleton(project: dict[str, Any]) -> dict[str, Any]:
    project_id = project["projectId"]
    target_branch = "task/<task-id>-<short-topic>"
    return {
        "projectId": project_id,
        "targetRemoteName": project.get("targetRemoteName", "canonical"),
        "canonicalRemoteUrl": project.get("canonicalRemoteUrl"),
        "codeGateRemoteUrl": project.get("codeGateRemoteUrl"),
        "defaultBaseBranch": project.get("defaultBaseBranch", "main"),
        "allowedPathPrefixes": project.get("allowedPathPrefixes", []),
        "syncLine": f"submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id> target={target_branch}",
        "denCoreFacadeFields": {
            "project_id": project_id,
            "task_id": "<task-id>",
            "submission_id": "<submission-id>",
            "ingress_ref": project.get("immutableRefPattern"),
            "head_commit": "<full-reviewed-head-sha>",
            "base_commit": "<full-base-sha>",
            "review_round_id": "<looks-good-review-round-id>",
            "target_branch": target_branch,
            "operation": "push_branch",
            "target_remote": project.get("targetRemoteName", "canonical"),
            "expected_base_branch": project.get("defaultBaseBranch", "main"),
            "canonical_remote_url": project.get("canonicalRemoteUrl"),
            "code_gate_remote_url": project.get("codeGateRemoteUrl"),
            "allowed_path_prefixes": project.get("allowedPathPrefixes", []),
            "scope_override_ids": [],
            "scope_overrides": [],
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--inventory", type=Path, default=DEFAULT_INVENTORY)
    parser.add_argument("--status-url", default=DEFAULT_STATUS_URL)
    parser.add_argument("--status-file", type=Path, help="read a captured /config/status JSON instead of HTTP")
    parser.add_argument("--project", help="limit checks to a single project id")
    parser.add_argument("--probe-code-gate", action="store_true", help="run optional git ls-remote probes when per-project SSH command env vars are present")
    parser.add_argument("--allow-live-enabled", action="store_true", help="accept persistent live publishing only with explicit redacted ssh_command credential policy")
    parser.add_argument("--emit-dry-run-skeleton", metavar="PROJECT_ID", help="print a dry-run request skeleton for the named project and exit")
    args = parser.parse_args()

    inventory = load_json(args.inventory)
    projects = inventory.get("projects", []) if isinstance(inventory, dict) else []
    if args.emit_dry_run_skeleton:
        for project in projects:
            if isinstance(project, dict) and project.get("projectId") == args.emit_dry_run_skeleton:
                print(json.dumps(dry_run_skeleton(project), indent=2, sort_keys=True))
                return 0
        raise SystemExit(f"project not found in inventory: {args.emit_dry_run_skeleton}")

    findings = validate_inventory_shape(inventory)
    if args.project:
        projects = [project for project in projects if isinstance(project, dict) and project.get("projectId") == args.project]
        if not projects:
            findings.append(Finding("error", args.project, "project_missing", "requested project is absent from inventory"))

    status = load_json(args.status_file) if args.status_file else fetch_status(args.status_url)
    if status.get("configurationContract") != "den-publish-runtime-config-v2":
        findings.append(Finding("error", "_service", "bad_config_contract", "den-publish /config/status is not den-publish-runtime-config-v2"))
    live_enabled = status.get("livePublishing", {}).get("enabled") is True
    live_credential_configured = status.get("liveCredentialPolicy", {}).get("configured") is True
    if args.allow_live_enabled:
        if not live_enabled:
            findings.append(Finding("error", "_service", "live_publishing_not_enabled", "approved-live mode requires livePublishing.enabled=true"))
        elif not live_credential_policy_is_explicit_and_redacted(status):
            findings.append(Finding("error", "_service", "live_credentials_not_explicit_redacted", "live publishing is enabled but credential policy is not explicit ssh_command with redacted value and fingerprint"))
    else:
        if live_enabled:
            findings.append(Finding("error", "_service", "live_publishing_enabled", "persistent den-publish service reports livePublishing.enabled=true"))
        if live_credential_configured:
            findings.append(Finding("error", "_service", "live_credentials_configured", "persistent den-publish service reports live credential policy configured"))
    for warning in status.get("warnings", []) or []:
        code = warning.get("code", "runtime_warning") if isinstance(warning, dict) else "runtime_warning"
        message = warning.get("message", str(warning)) if isinstance(warning, dict) else str(warning)
        findings.append(Finding("warning", "_service", str(code), str(message)))

    policies = project_status_by_id(status)
    for project in projects:
        if not isinstance(project, dict):
            continue
        findings.extend(compare_project_to_status(project, policies.get(str(project.get("projectId")))))
        if args.probe_code_gate:
            findings.extend(probe_code_gate(project))

    for finding in findings:
        print(finding.format())
    errors = [finding for finding in findings if finding.severity == "error"]
    if errors:
        print(f"promotion_metadata_drift=fail errors={len(errors)} findings={len(findings)}")
        return 1
    print(f"promotion_metadata_drift=ok projects={len(projects)} findings={len(findings)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

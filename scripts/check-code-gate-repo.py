#!/usr/bin/env python3
"""Check or provision den-code-gate repositories without printing credentials.

Default mode performs safe preflight: inventory shape, den-publish runtime drift,
Forgejo HTTP/SSH reachability, and optional authenticated repo lookup.

Creation is opt-in (`--create`) and requires an admin token supplied via an
environment variable. The token is never printed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import socket
import subprocess
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INVENTORY = ROOT / "config" / "code-gate-repositories.json"
DEFAULT_STATUS_URL = "http://127.0.0.1:5090/config/status"
SAFE_TOKEN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$")


@dataclass(frozen=True)
class Finding:
    severity: str
    code: str
    message: str

    def line(self) -> str:
        return f"{self.severity.upper()} {self.code}: {self.message}"


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(1)


def load_json(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"missing JSON file: {path}")
    except json.JSONDecodeError as exc:
        fail(f"invalid JSON in {path}: {exc}")
    if not isinstance(data, dict):
        fail(f"expected object in {path}")
    return data


def fetch_json(url: str, *, token: str | None = None, method: str = "GET", payload: dict[str, Any] | None = None, timeout: int = 8) -> tuple[int, Any]:
    data = None
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = f"token {token}"
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 - operator-supplied LAN URL
            raw = response.read().decode("utf-8")
            if not raw:
                return response.status, None
            try:
                return response.status, json.loads(raw)
            except json.JSONDecodeError:
                return response.status, raw
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            body: Any = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            body = raw
        return exc.code, body
    except urllib.error.URLError as exc:
        return 0, {"error": str(exc)}


def fingerprint(value: str | None) -> str | None:
    if not value:
        return None
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]


def safe_project_env(project_id: str) -> str:
    return "".join(ch if ch.isalnum() else "_" for ch in project_id).upper()


def repo_for_project(inventory: dict[str, Any], project_id: str) -> dict[str, Any]:
    for repo in inventory.get("repositories", []):
        if isinstance(repo, dict) and repo.get("projectId") == project_id:
            return repo
    fail(f"project not found in code-gate repository inventory: {project_id}")


def validate_shape(inventory: dict[str, Any], repo: dict[str, Any]) -> list[Finding]:
    findings: list[Finding] = []
    if inventory.get("schema") != "den_code_gate_repository_inventory":
        findings.append(Finding("error", "bad_schema", "schema must be den_code_gate_repository_inventory"))
    if inventory.get("schemaVersion") != 1:
        findings.append(Finding("error", "bad_schema_version", "schemaVersion must be 1"))
    forgejo = inventory.get("forgejo")
    if not isinstance(forgejo, dict):
        findings.append(Finding("error", "forgejo_missing", "forgejo object is required"))
    for field in ["projectId", "owner", "repo", "repoPath", "codeGateRemoteUrl", "immutableRefPattern", "convenienceRefPattern", "accessPolicy"]:
        if not repo.get(field):
            findings.append(Finding("error", "repo_field_missing", f"repository entry missing {field}"))
    for field in ["owner", "repo"]:
        value = str(repo.get(field, ""))
        if value and not SAFE_TOKEN.match(value):
            findings.append(Finding("error", "unsafe_repo_token", f"{field} is not a safe Forgejo token"))
    immutable = str(repo.get("immutableRefPattern", ""))
    if "{task_id}" not in immutable or "{run_id}" not in immutable or "{attempt_ordinal}" not in immutable:
        findings.append(Finding("error", "bad_ref_pattern", "immutableRefPattern must include task_id, run_id, and attempt_ordinal placeholders"))
    if "/current" not in str(repo.get("convenienceRefPattern", "")):
        findings.append(Finding("warning", "convenience_ref_unusual", "convenienceRefPattern should normally end in current"))
    return findings


def status_project(status: dict[str, Any], project_id: str) -> dict[str, Any] | None:
    for policy in status.get("projectPolicies", []) or []:
        if isinstance(policy, dict) and policy.get("projectId") == project_id:
            return policy
    return None


def setting_fingerprint(setting: Any) -> str | None:
    return setting.get("fingerprint") if isinstance(setting, dict) else None


def setting_configured(setting: Any) -> bool:
    return isinstance(setting, dict) and bool(setting.get("configured"))


def check_runtime_status(repo: dict[str, Any], status_url: str) -> list[Finding]:
    findings: list[Finding] = []
    status_code, status = fetch_json(status_url)
    if status_code != 200 or not isinstance(status, dict):
        return [Finding("error", "status_unreachable", f"failed to fetch den-publish status from {status_url}")]
    if status.get("configurationContract") != "den-publish-runtime-config-v2":
        findings.append(Finding("error", "bad_status_contract", "den-publish status contract is not v2"))
    if status.get("livePublishing", {}).get("enabled") is True:
        findings.append(Finding("error", "live_publishing_enabled", "persistent den-publish service reports live publishing enabled"))
    policy = status_project(status, str(repo["projectId"]))
    if policy is None:
        return findings + [Finding("error", "runtime_project_missing", "project missing from den-publish /config/status")]
    remote_setting = policy.get("codeGateRemoteUrl")
    if not setting_configured(remote_setting):
        findings.append(Finding("error", "runtime_code_gate_missing", "codeGateRemoteUrl missing from den-publish runtime policy"))
    elif setting_fingerprint(remote_setting) != fingerprint(str(repo["codeGateRemoteUrl"])):
        findings.append(Finding("error", "runtime_code_gate_drift", "codeGateRemoteUrl fingerprint differs between repository inventory and runtime status"))
    read_credential = policy.get("codeGateReadCredential") or {}
    if not setting_configured(read_credential):
        findings.append(Finding("warning", "runtime_read_credential_missing", "den-publish service lacks configured code-gate read credential"))
    else:
        if read_credential.get("value") != "[redacted]":
            findings.append(Finding("error", "runtime_read_credential_unredacted", "code-gate credential should be redacted in /config/status"))
        if read_credential.get("display") != "ssh_command":
            findings.append(Finding("error", "runtime_read_credential_display", "code-gate credential display should be ssh_command"))
        if not read_credential.get("fingerprint"):
            findings.append(Finding("error", "runtime_read_credential_no_fingerprint", "code-gate credential should expose fingerprint"))
    return findings


def check_forgejo_reachability(inventory: dict[str, Any]) -> list[Finding]:
    findings: list[Finding] = []
    forgejo = inventory.get("forgejo", {})
    base_url = str(forgejo.get("baseUrl", "")).rstrip("/")
    if not base_url:
        return [Finding("error", "forgejo_base_url_missing", "forgejo.baseUrl missing")]
    status, _ = fetch_json(f"{base_url}/api/v1/version")
    # den-code-gate requires signin, so 401/403 still proves HTTP app reachability.
    if status not in {200, 401, 403}:
        findings.append(Finding("error", "forgejo_http_unreachable", f"Forgejo version endpoint returned status {status}"))
    host = str(forgejo.get("sshHost", ""))
    port = int(forgejo.get("sshPort", 0) or 0)
    if not host or not port:
        findings.append(Finding("error", "forgejo_ssh_missing", "forgejo sshHost/sshPort missing"))
    else:
        try:
            with socket.create_connection((host, port), timeout=5):
                pass
        except OSError as exc:
            findings.append(Finding("error", "forgejo_ssh_unreachable", f"Forgejo SSH {host}:{port} unreachable: {exc}"))
    return findings


def repo_api_lookup(inventory: dict[str, Any], repo: dict[str, Any], token: str | None) -> tuple[bool | None, list[Finding]]:
    if not token:
        env_name = inventory.get("forgejo", {}).get("adminTokenEnv", "DEN_CODE_GATE_ADMIN_TOKEN")
        return None, [Finding("warning", "repo_api_probe_skipped", f"authenticated repo lookup skipped; set {env_name} to check/create without printing token")]
    base_url = str(inventory["forgejo"]["baseUrl"]).rstrip("/")
    owner = repo["owner"]
    name = repo["repo"]
    status, body = fetch_json(f"{base_url}/api/v1/repos/{owner}/{name}", token=token)
    if status == 200:
        return True, []
    if status == 404:
        return False, [Finding("warning", "repo_missing", f"repository {owner}/{name} does not exist")]
    return None, [Finding("error", "repo_api_lookup_failed", f"repository lookup returned HTTP {status}")]


def create_org_if_requested(inventory: dict[str, Any], repo: dict[str, Any], token: str, *, create_owner_org: bool) -> list[Finding]:
    if not create_owner_org:
        return []
    base_url = str(inventory["forgejo"]["baseUrl"]).rstrip("/")
    owner = repo["owner"]
    status, _ = fetch_json(f"{base_url}/api/v1/orgs/{owner}", token=token)
    if status == 200:
        return []
    if status != 404:
        return [Finding("error", "org_lookup_failed", f"owner org lookup returned HTTP {status}")]
    payload = {"username": owner, "full_name": owner, "visibility": "private"}
    create_status, _ = fetch_json(f"{base_url}/api/v1/orgs", token=token, method="POST", payload=payload)
    if create_status not in {200, 201}:
        return [Finding("error", "org_create_failed", f"owner org create returned HTTP {create_status}")]
    return [Finding("info", "org_created", f"created owner org {owner}")]


def create_repo(inventory: dict[str, Any], repo: dict[str, Any], token: str) -> list[Finding]:
    base_url = str(inventory["forgejo"]["baseUrl"]).rstrip("/")
    owner = repo["owner"]
    name = repo["repo"]
    payload = {
        "name": name,
        "private": repo.get("visibility", "private") != "public",
        "auto_init": False,
        "default_branch": repo.get("defaultBranch", "main"),
        "description": f"Den code-gate repository for project {repo['projectId']}",
    }
    status, _ = fetch_json(f"{base_url}/api/v1/orgs/{owner}/repos", token=token, method="POST", payload=payload)
    if status not in {200, 201}:
        return [Finding("error", "repo_create_failed", f"repository create returned HTTP {status}")]
    return [Finding("info", "repo_created", f"created repository {owner}/{name}")]


def optional_git_probe(repo: dict[str, Any], *, probe: bool) -> list[Finding]:
    if not probe:
        return []
    env_name = f"DEN_CODE_GATE_REPO_SSH_COMMAND_{safe_project_env(str(repo['projectId']))}"
    command = os.environ.get(env_name)
    if not command:
        return [Finding("warning", "ssh_probe_skipped", f"set {env_name} to run git ls-remote without printing credentials")]
    env = os.environ.copy()
    env["GIT_SSH_COMMAND"] = command
    env["GIT_TERMINAL_PROMPT"] = "0"
    proc = subprocess.run(
        ["git", "ls-remote", "--heads", str(repo["codeGateRemoteUrl"])],
        text=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        env=env,
        timeout=20,
        check=False,
    )
    if proc.returncode != 0:
        return [Finding("error", "ssh_probe_failed", "git ls-remote failed using supplied code-gate SSH command")]
    return [Finding("info", "ssh_probe_ok", "git ls-remote succeeded using supplied code-gate SSH command")]


def emit_worker_preflight(repo: dict[str, Any]) -> None:
    project_id = str(repo["projectId"])
    print(json.dumps({
        "schema": "den_code_gate_worker_preflight",
        "schemaVersion": 1,
        "projectId": project_id,
        "codeGateRemoteUrl": repo["codeGateRemoteUrl"],
        "immutableRefPattern": repo["immutableRefPattern"],
        "convenienceRefPattern": repo["convenienceRefPattern"],
        "workerCredentialInstruction": "Use an approved code-gate-only submission credential supplied outside Den task messages; never use canonical GitHub push credentials.",
        "reviewerInstruction": "Fetch only the immutable ingress_ref and verify the fetched commit equals head_commit before review.",
        "syncLine": "submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id or pending> target=<target_branch>",
        "optionalSshProbeEnv": f"DEN_CODE_GATE_REPO_SSH_COMMAND_{safe_project_env(project_id)}",
    }, indent=2, sort_keys=True))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--inventory", type=Path, default=DEFAULT_INVENTORY)
    parser.add_argument("--project", required=True)
    parser.add_argument("--status-url", default=DEFAULT_STATUS_URL)
    parser.add_argument("--token-env", default=None, help="Environment variable containing Forgejo admin token; default uses inventory forgejo.adminTokenEnv")
    parser.add_argument("--create", action="store_true", help="Create missing repo using admin token")
    parser.add_argument("--create-owner-org", action="store_true", help="Create missing owner org before repo creation")
    parser.add_argument("--probe-ssh", action="store_true", help="Run optional git ls-remote using per-project SSH command env var")
    parser.add_argument("--emit-worker-preflight", action="store_true")
    args = parser.parse_args()

    inventory = load_json(args.inventory)
    repo = repo_for_project(inventory, args.project)
    if args.emit_worker_preflight:
        emit_worker_preflight(repo)
        return 0

    findings: list[Finding] = []
    findings.extend(validate_shape(inventory, repo))
    findings.extend(check_runtime_status(repo, args.status_url))
    findings.extend(check_forgejo_reachability(inventory))
    token_env = args.token_env or str(inventory.get("forgejo", {}).get("adminTokenEnv", "DEN_CODE_GATE_ADMIN_TOKEN"))
    token = os.environ.get(token_env)
    exists, repo_findings = repo_api_lookup(inventory, repo, token)
    findings.extend(repo_findings)
    if args.create:
        if not token:
            findings.append(Finding("error", "create_token_missing", f"--create requires admin token env {token_env}"))
        elif exists is False:
            findings.extend(create_org_if_requested(inventory, repo, token, create_owner_org=args.create_owner_org))
            if not any(f.severity == "error" for f in findings):
                findings.extend(create_repo(inventory, repo, token))
        elif exists is True:
            findings.append(Finding("info", "repo_exists", "repository already exists; create skipped"))
    findings.extend(optional_git_probe(repo, probe=args.probe_ssh))

    for finding in findings:
        print(finding.line())
    errors = [finding for finding in findings if finding.severity == "error"]
    if errors:
        print(f"code_gate_repo_preflight=fail project={args.project} errors={len(errors)} findings={len(findings)}")
        return 1
    print(f"code_gate_repo_preflight=ok project={args.project} findings={len(findings)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

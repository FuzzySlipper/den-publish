#!/usr/bin/env python3
"""Classify project readiness for the Den code-gate -> den-publish workflow.

Default mode is secret-free and fail-closed. It reads promotion metadata,
code-gate inventory, redacted den-publish /config/status, Den project inventory,
and the public MCP facade tool list, then emits a machine-readable readiness
classification. It may run existing no-secret subchecks, but never uses admin
credentials or mutates Forgejo/service state.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PROMOTION_INVENTORY = ROOT / "config" / "promotion-projects.json"
DEFAULT_CODE_GATE_INVENTORY = ROOT / "config" / "code-gate-repositories.json"
DEFAULT_STATUS_URL = "http://127.0.0.1:5090/config/status"
DEFAULT_MCP_URL = "http://192.168.1.10:5199/mcp"
DEFAULT_DEN_PROJECTS_URL = DEFAULT_MCP_URL


class ReadinessError(RuntimeError):
    pass


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ReadinessError(f"missing JSON file: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ReadinessError(f"invalid JSON in {path}: {exc}") from exc


def fetch_json(url: str, *, timeout: int = 10) -> Any:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:  # noqa: S310 - LAN/operator URL
            raw = response.read().decode("utf-8")
    except urllib.error.URLError as exc:
        raise ReadinessError(f"failed to fetch {url}: {exc}") from exc
    try:
        return json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ReadinessError(f"non-JSON response from {url}: {exc}") from exc


def parse_mcp_response(raw: str) -> dict[str, Any]:
    if raw.lstrip().startswith("{"):
        return json.loads(raw)
    for block in raw.strip().split("\n\n"):
        data_lines = [line[5:].strip() for line in block.splitlines() if line.startswith("data:")]
        if data_lines:
            return json.loads("\n".join(data_lines))
    raise ReadinessError("MCP response did not contain JSON or SSE data")


def mcp_post(url: str, payload: dict[str, Any], session_id: str | None = None) -> tuple[str | None, dict[str, Any]]:
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    request_headers = dict(headers)
    if session_id:
        request_headers["Mcp-Session-Id"] = session_id
    request = urllib.request.Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers=request_headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=15) as response:  # noqa: S310 - LAN/operator URL
            return response.headers.get("Mcp-Session-Id"), parse_mcp_response(response.read().decode("utf-8"))
    except urllib.error.URLError as exc:
        raise ReadinessError(f"failed to call MCP facade {url}: {exc}") from exc


def mcp_initialize(url: str) -> str | None:
    session_id, _ = mcp_post(url, {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "den-publish-readiness-checker", "version": "1"},
        },
    })
    return session_id


def fetch_mcp_tools(url: str = DEFAULT_MCP_URL) -> dict[str, Any]:
    session_id = mcp_initialize(url)
    _, parsed = mcp_post(url, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, session_id)
    return parsed.get("result", {}) if isinstance(parsed, dict) else {}


def call_mcp_tool(url: str, name: str, arguments: dict[str, Any] | None = None) -> Any:
    session_id = mcp_initialize(url)
    _, parsed = mcp_post(
        url,
        {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": name, "arguments": arguments or {}}},
        session_id,
    )
    result = parsed.get("result", {}) if isinstance(parsed, dict) else {}
    content = result.get("content", []) if isinstance(result, dict) else []
    for item in content:
        if isinstance(item, dict) and item.get("type") == "text":
            text = str(item.get("text", ""))
            try:
                return json.loads(text)
            except json.JSONDecodeError:
                return text
    return result


def den_projects_from_payload(payload: Any) -> dict[str, dict[str, Any]]:
    if isinstance(payload, dict) and isinstance(payload.get("projects"), list):
        projects = payload["projects"]
    elif isinstance(payload, list):
        projects = payload
    else:
        projects = []
    result: dict[str, dict[str, Any]] = {}
    for project in projects:
        if not isinstance(project, dict):
            continue
        project_id = project.get("id") or project.get("project_id")
        if project_id:
            result[str(project_id)] = project
    return result


def project_by_id(inventory: dict[str, Any], project_id: str) -> dict[str, Any] | None:
    for project in inventory.get("projects", []) or []:
        if isinstance(project, dict) and project.get("projectId") == project_id:
            return project
    return None


def repo_by_project(inventory: dict[str, Any], project_id: str) -> dict[str, Any] | None:
    for repo in inventory.get("repositories", []) or []:
        if isinstance(repo, dict) and repo.get("projectId") == project_id:
            return repo
    return None


def non_promotion_target_by_id(inventory: dict[str, Any], project_id: str) -> dict[str, Any] | None:
    for target in inventory.get("nonPromotionTargets", []) or []:
        if isinstance(target, dict) and target.get("projectId") == project_id:
            return target
    return None


def policy_by_project(status: dict[str, Any], project_id: str) -> dict[str, Any] | None:
    for policy in status.get("projectPolicies", []) or []:
        if isinstance(policy, dict) and policy.get("projectId") == project_id:
            return policy
    return None


def check(status: str, message: str, **extra: Any) -> dict[str, Any]:
    payload = {"status": status, "message": message}
    payload.update(extra)
    return payload


def tools_have_dry_run(mcp_tools: dict[str, Any]) -> bool:
    tools = mcp_tools.get("tools", []) if isinstance(mcp_tools, dict) else []
    return any(isinstance(tool, dict) and tool.get("name") == "request_den_publish_dry_run" for tool in tools)


def run_subcheck(command: list[str]) -> dict[str, Any]:
    proc = subprocess.run(
        command,
        cwd=ROOT,
        text=True,
        capture_output=True,
        timeout=60,
        check=False,
    )
    # Do not include stderr by default; existing scripts should not print tokens, but stdout is enough for classification.
    return {
        "command": command,
        "returncode": proc.returncode,
        "stdoutTail": proc.stdout.splitlines()[-5:],
    }


def evaluate_project(
    project_id: str,
    *,
    promotion_inventory: dict[str, Any],
    code_gate_inventory: dict[str, Any],
    status: dict[str, Any],
    mcp_tools: dict[str, Any],
    den_projects: dict[str, dict[str, Any]] | None = None,
    run_subchecks: bool = True,
) -> dict[str, Any]:
    den_projects = den_projects or {}
    promotion = project_by_id(promotion_inventory, project_id)
    code_gate_repo = repo_by_project(code_gate_inventory, project_id)
    runtime_policy = policy_by_project(status, project_id)
    den_project = den_projects.get(project_id)
    non_promotion_target = non_promotion_target_by_id(promotion_inventory, project_id)

    checks: dict[str, dict[str, Any]] = {}
    next_actions: list[str] = []
    blockers: list[str] = []
    requires_approval = False

    if den_project is None:
        checks["den_project"] = check("warning", "project absent from supplied Den project inventory")
        next_actions.append("Confirm the Den project exists or provide a Den projects inventory file.")
    else:
        root_path = den_project.get("root_path") or den_project.get("rootPath")
        if root_path:
            checks["den_project"] = check("ok", "Den project exists with root path", rootPath=root_path)
        else:
            checks["den_project"] = check("warning", "Den project exists but has no root path")
            next_actions.append("Clarify whether this project has a standalone code repository/root path.")

    if non_promotion_target is not None:
        reason = str(non_promotion_target.get("reason") or "project is not a standalone promotion target")
        route_through = non_promotion_target.get("routeThroughProjectId")
        checks["promotion_metadata"] = check(
            "not_applicable",
            "project is explicitly marked as not a standalone promotion target",
            reason=reason,
            routeThroughProjectId=route_through,
        )
        checks["code_gate_inventory"] = check("not_applicable", "no standalone code-gate repository required")
        checks["runtime_policy"] = check("not_applicable", "no standalone den-publish runtime policy required")
        action = f"Do not onboard {project_id} as a standalone promotion target."
        if route_through:
            action += f" Route work through {route_through}."
        return {
            "schema": "den_project_promotion_readiness",
            "schemaVersion": 1,
            "projectId": project_id,
            "classification": "not_applicable",
            "ready": False,
            "requiresApproval": False,
            "checks": checks,
            "nextActions": [action, reason],
        }

    if promotion is None:
        checks["promotion_metadata"] = check("missing", "project missing from promotion metadata inventory")
        next_actions.append("Add project to config/promotion-projects.json after canonical remote/root is confirmed.")
    else:
        missing_fields = [field for field in ["canonicalRemoteUrl", "targetRemoteName", "defaultBaseBranch"] if not promotion.get(field)]
        missing_code_gate = [field for field in ["codeGateRemoteUrl", "codeGateRepo"] if not promotion.get(field)]
        if missing_fields:
            checks["promotion_metadata"] = check("error", "promotion metadata missing required canonical fields", missingFields=missing_fields)
            blockers.extend(missing_fields)
        elif missing_code_gate:
            checks["promotion_metadata"] = check("warning", "promotion metadata exists but code-gate fields are incomplete", missingFields=missing_code_gate)
            next_actions.append("Populate code-gate metadata after repo/access provisioning is approved.")
        else:
            checks["promotion_metadata"] = check("ok", "promotion metadata exists")

    if code_gate_repo is None:
        checks["code_gate_inventory"] = check("missing", "project missing from code-gate repository inventory")
        requires_approval = True
        next_actions.append("Prepare Forgejo code-gate repo/deploy-key approval packet before provisioning.")
    else:
        repo_missing = [field for field in ["owner", "repo", "repoPath", "codeGateRemoteUrl", "immutableRefPattern"] if not code_gate_repo.get(field)]
        if repo_missing:
            checks["code_gate_inventory"] = check("error", "code-gate repository inventory entry is incomplete", missingFields=repo_missing)
            blockers.extend(repo_missing)
        else:
            checks["code_gate_inventory"] = check("ok", "code-gate repository inventory exists", provisioningStatus=code_gate_repo.get("provisioningStatus"))

    if not isinstance(status, dict) or status.get("configurationContract") != "den-publish-runtime-config-v2":
        checks["runtime_status"] = check("error", "den-publish /config/status missing or wrong contract")
        blockers.append("runtime_status")
    else:
        checks["runtime_status"] = check("ok", "den-publish runtime status contract is v2")

    if status.get("livePublishing", {}).get("enabled") is True:
        checks["live_publish_disabled"] = check("error", "live publishing is enabled; readiness preflight fails closed")
        blockers.append("live_publish_enabled")
    else:
        checks["live_publish_disabled"] = check("ok", "live publishing is disabled")

    if status.get("liveCredentialPolicy", {}).get("configured") is True:
        checks["live_credentials_disabled"] = check("error", "live credential policy is configured outside a scoped approval window")
        blockers.append("live_credential_configured")
    else:
        checks["live_credentials_disabled"] = check("ok", "live credential policy is not configured")

    if runtime_policy is None:
        checks["runtime_policy"] = check("missing", "project missing from den-publish runtime policy")
        if code_gate_repo is not None:
            next_actions.append("Add indexed project policy to persistent den-publish runtime config after approval.")
    else:
        checks["runtime_policy"] = check("ok", "project exists in den-publish runtime policy")

    if tools_have_dry_run(mcp_tools):
        checks["mcp_facade"] = check("ok", "public MCP facade exposes request_den_publish_dry_run")
    else:
        checks["mcp_facade"] = check("error", "public MCP facade does not expose request_den_publish_dry_run")
        blockers.append("mcp_facade_tool_missing")

    if run_subchecks:
        subchecks: list[dict[str, Any]] = []
        subchecks.append(run_subcheck([sys.executable, "scripts/check-promotion-metadata-drift.py", "--project", project_id]))
        if code_gate_repo is not None:
            subchecks.append(run_subcheck([sys.executable, "scripts/check-code-gate-repo.py", "--project", project_id]))
        checks["subchecks"] = {"status": "ok" if all(item["returncode"] == 0 for item in subchecks) else "warning", "runs": subchecks}

    if blockers:
        classification = "blocked"
    elif promotion is None:
        if den_project is not None and not (den_project.get("root_path") or den_project.get("rootPath")):
            classification = "not_applicable"
        else:
            classification = "needs_metadata"
    elif code_gate_repo is None or not promotion.get("codeGateRemoteUrl") or not promotion.get("codeGateRepo"):
        classification = "needs_code_gate"
    elif runtime_policy is None:
        classification = "needs_runtime_policy"
    else:
        classification = "ready"

    ready = classification == "ready"
    if ready:
        next_actions.append("Project is ready for validate-only dry-run proof when a reviewed immutable submission exists.")
    elif classification == "needs_runtime_policy" and not any("runtime" in action for action in next_actions):
        next_actions.append("Add project to persistent den-publish runtime policy after approval.")
    elif classification == "needs_metadata":
        next_actions.append("Add promotion/code-gate metadata before provisioning or dry-run proof.")

    return {
        "schema": "den_project_promotion_readiness",
        "schemaVersion": 1,
        "projectId": project_id,
        "classification": classification,
        "ready": ready,
        "requiresApproval": requires_approval or classification in {"needs_code_gate", "needs_runtime_policy"},
        "checks": checks,
        "nextActions": list(dict.fromkeys(next_actions)),
    }


def load_den_projects(path: Path | None, url: str) -> dict[str, dict[str, Any]]:
    if path:
        return den_projects_from_payload(load_json(path))
    try:
        return den_projects_from_payload(call_mcp_tool(url, "list_projects"))
    except ReadinessError:
        return {}


def load_mcp_tools(path: Path | None, url: str) -> dict[str, Any]:
    if path:
        return load_json(path)
    return fetch_mcp_tools(url)


def print_text(result: dict[str, Any]) -> None:
    print(f"project={result['projectId']} classification={result['classification']} ready={str(result['ready']).lower()} requiresApproval={str(result['requiresApproval']).lower()}")
    for name, item in result["checks"].items():
        if name == "subchecks":
            print(f"{name}: {item['status']}")
            for run in item.get("runs", []):
                print(f"  rc={run['returncode']} cmd={' '.join(run['command'])}")
                for line in run.get("stdoutTail", []):
                    print(f"    {line}")
            continue
        print(f"{name}: {item['status']} - {item['message']}")
    for action in result.get("nextActions", []):
        print(f"next: {action}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", required=True)
    parser.add_argument("--promotion-inventory", type=Path, default=DEFAULT_PROMOTION_INVENTORY)
    parser.add_argument("--code-gate-inventory", type=Path, default=DEFAULT_CODE_GATE_INVENTORY)
    parser.add_argument("--status-url", default=DEFAULT_STATUS_URL)
    parser.add_argument("--status-file", type=Path)
    parser.add_argument("--mcp-url", default=DEFAULT_MCP_URL)
    parser.add_argument("--mcp-tools-file", type=Path)
    parser.add_argument("--den-projects-url", default=DEFAULT_DEN_PROJECTS_URL)
    parser.add_argument("--den-projects-file", type=Path)
    parser.add_argument("--no-subchecks", action="store_true")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    try:
        result = evaluate_project(
            args.project,
            promotion_inventory=load_json(args.promotion_inventory),
            code_gate_inventory=load_json(args.code_gate_inventory),
            status=load_json(args.status_file) if args.status_file else fetch_json(args.status_url),
            mcp_tools=load_mcp_tools(args.mcp_tools_file, args.mcp_url),
            den_projects=load_den_projects(args.den_projects_file, args.den_projects_url),
            run_subchecks=not args.no_subchecks,
        )
    except ReadinessError as exc:
        print(f"readiness_checker=error: {exc}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print_text(result)

    if result["classification"] == "blocked":
        return 1
    if result["ready"] or result["classification"] == "not_applicable":
        return 0
    return 2


if __name__ == "__main__":
    sys.exit(main())

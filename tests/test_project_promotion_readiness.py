#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "check-project-promotion-readiness.py"


def load_module():
    spec = importlib.util.spec_from_file_location("check_project_promotion_readiness", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def write_json(path: Path, payload: dict) -> Path:
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return path


def promotion_inventory(*projects: dict) -> dict:
    return {
        "schema": "den_promotion_project_inventory",
        "schemaVersion": 1,
        "projects": list(projects),
    }


def promotion_project(project_id: str, *, code_gate: bool = True) -> dict:
    return {
        "projectId": project_id,
        "status": "dry_run_ready" if code_gate else "metadata_incomplete",
        "canonicalRemoteUrl": f"git@github.com:FuzzySlipper/{project_id}.git",
        "targetRemoteName": "canonical",
        "defaultBaseBranch": "main",
        "allowedOperations": ["push_branch"],
        "pushBranchPrefixes": ["task/"],
        "fastForwardBranches": ["main"],
        "codeGateInstance": "den-code-gate",
        "codeGateRemoteUrl": f"ssh://git@192.168.1.10:3022/{project_id}/{project_id}.git" if code_gate else None,
        "codeGateRepo": f"{project_id}/{project_id}.git" if code_gate else None,
        "immutableRefPattern": "refs/heads/submissions/{project_id}/tasks/{task_id}/runs/{run_id}/attempt-{attempt_ordinal}",
        "convenienceRefPattern": "refs/heads/submissions/{project_id}/tasks/{task_id}/current",
        "allowedPathPrefixes": [],
    }


def code_gate_inventory(*project_ids: str) -> dict:
    return {
        "schema": "den_code_gate_repository_inventory",
        "schemaVersion": 1,
        "forgejo": {
            "instance": "den-code-gate",
            "baseUrl": "http://192.168.1.10:3020",
            "sshHost": "192.168.1.10",
            "sshPort": 3022,
            "adminTokenEnv": "DEN_CODE_GATE_ADMIN_TOKEN",
        },
        "repositories": [
            {
                "projectId": project_id,
                "owner": project_id,
                "repo": project_id,
                "repoPath": f"{project_id}/{project_id}.git",
                "visibility": "private",
                "defaultBranch": "main",
                "codeGateRemoteUrl": f"ssh://git@192.168.1.10:3022/{project_id}/{project_id}.git",
                "immutableRefPattern": "refs/heads/submissions/{project_id}/tasks/{task_id}/runs/{run_id}/attempt-{attempt_ordinal}",
                "convenienceRefPattern": "refs/heads/submissions/{project_id}/tasks/{task_id}/current",
                "provisioningStatus": "runtime_configured",
                "accessPolicy": [],
            }
            for project_id in project_ids
        ],
    }


def status_payload(*project_ids: str, live: bool = False, credential: bool = False) -> dict:
    return {
        "configurationContract": "den-publish-runtime-config-v2",
        "livePublishing": {"enabled": live},
        "liveCredentialPolicy": {"configured": credential},
        "projectPolicies": [
            {
                "projectId": project_id,
                "canonicalRemoteUrl": {"configured": True, "value": "[redacted]", "fingerprint": "abc"},
                "codeGateRemoteUrl": {"configured": True, "value": "[redacted]", "fingerprint": "def"},
                "codeGateReadCredential": {"configured": True, "value": "[redacted]", "display": "ssh_command", "fingerprint": "123"},
                "targetRemoteName": {"configured": True, "value": "canonical"},
                "pushBranchPrefixes": ["task/"],
                "fastForwardBranches": ["main"],
            }
            for project_id in project_ids
        ],
    }


def mcp_tools_payload(has_tool: bool = True) -> dict:
    tools = [{"name": "other_tool"}]
    if has_tool:
        tools.append({"name": "request_den_publish_dry_run"})
    return {"tools": tools}


def test_non_promotion_target_is_not_applicable_without_approval(tmp_path: Path):
    module = load_module()
    project_id = "den-desktop"
    inventory = promotion_inventory()
    inventory["nonPromotionTargets"] = [
        {
            "projectId": project_id,
            "reason": "Code currently lives inside den-mcp.",
            "routeThroughProjectId": "den-mcp",
        }
    ]

    result = module.evaluate_project(
        project_id,
        promotion_inventory=inventory,
        code_gate_inventory=code_gate_inventory(),
        status=status_payload(),
        mcp_tools=mcp_tools_payload(),
        den_projects={project_id: {"root_path": "/mnt/den-srv/dev/den-mcp"}},
        run_subchecks=False,
    )

    assert result["classification"] == "not_applicable"
    assert result["ready"] is False
    assert result["requiresApproval"] is False
    assert result["checks"]["promotion_metadata"]["status"] == "not_applicable"
    assert result["checks"]["code_gate_inventory"]["status"] == "not_applicable"
    assert "den-mcp" in " ".join(result["nextActions"])


def test_ready_project_outputs_machine_readable_ready_json(tmp_path: Path):
    module = load_module()
    project_id = "den-channels"

    result = module.evaluate_project(
        project_id,
        promotion_inventory=promotion_inventory(promotion_project(project_id)),
        code_gate_inventory=code_gate_inventory(project_id),
        status=status_payload(project_id),
        mcp_tools=mcp_tools_payload(),
        den_projects={project_id: {"root_path": project_id}},
        run_subchecks=False,
    )

    assert result["projectId"] == project_id
    assert result["classification"] == "ready"
    assert result["ready"] is True
    assert result["requiresApproval"] is False
    assert result["checks"]["promotion_metadata"]["status"] == "ok"
    assert result["checks"]["code_gate_inventory"]["status"] == "ok"
    assert result["checks"]["runtime_policy"]["status"] == "ok"
    assert result["checks"]["mcp_facade"]["status"] == "ok"


def test_missing_code_gate_and_runtime_are_distinguished(tmp_path: Path):
    module = load_module()
    project_id = "den-core"

    result = module.evaluate_project(
        project_id,
        promotion_inventory=promotion_inventory(promotion_project(project_id, code_gate=False)),
        code_gate_inventory=code_gate_inventory(),
        status=status_payload(),
        mcp_tools=mcp_tools_payload(),
        den_projects={project_id: {"root_path": project_id}},
        run_subchecks=False,
    )

    assert result["classification"] == "needs_code_gate"
    assert result["ready"] is False
    assert result["requiresApproval"] is True
    assert result["checks"]["promotion_metadata"]["status"] == "warning"
    assert result["checks"]["code_gate_inventory"]["status"] == "missing"
    assert result["checks"]["runtime_policy"]["status"] == "missing"
    assert "Forgejo" in " ".join(result["nextActions"])


def test_live_publish_enabled_fails_closed_even_when_project_metadata_is_ready(tmp_path: Path):
    module = load_module()
    project_id = "den-channels"

    result = module.evaluate_project(
        project_id,
        promotion_inventory=promotion_inventory(promotion_project(project_id)),
        code_gate_inventory=code_gate_inventory(project_id),
        status=status_payload(project_id, live=True),
        mcp_tools=mcp_tools_payload(),
        den_projects={project_id: {"root_path": project_id}},
        run_subchecks=False,
    )

    assert result["classification"] == "blocked"
    assert result["ready"] is False
    assert result["checks"]["live_publish_disabled"]["status"] == "error"


def test_cli_prints_json_and_returns_nonzero_for_unready_project(tmp_path: Path):
    project_id = "den-core"
    promotion_path = write_json(tmp_path / "promotion.json", promotion_inventory(promotion_project(project_id, code_gate=False)))
    code_gate_path = write_json(tmp_path / "codegate.json", code_gate_inventory())
    status_path = write_json(tmp_path / "status.json", status_payload())
    mcp_path = write_json(tmp_path / "mcp.json", mcp_tools_payload())
    den_projects_path = write_json(tmp_path / "den-projects.json", {"projects": [{"id": project_id, "root_path": project_id}]})

    proc = subprocess.run(
        [
            sys.executable,
            str(SCRIPT),
            "--project",
            project_id,
            "--promotion-inventory",
            str(promotion_path),
            "--code-gate-inventory",
            str(code_gate_path),
            "--status-file",
            str(status_path),
            "--mcp-tools-file",
            str(mcp_path),
            "--den-projects-file",
            str(den_projects_path),
            "--no-subchecks",
            "--json",
        ],
        text=True,
        capture_output=True,
        check=False,
    )

    assert proc.returncode == 2
    payload = json.loads(proc.stdout)
    assert payload["projectId"] == project_id
    assert payload["classification"] == "needs_code_gate"
    assert "DEN_CODE_GATE_ADMIN_TOKEN" not in proc.stdout

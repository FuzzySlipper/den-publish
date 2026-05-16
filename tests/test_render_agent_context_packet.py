from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "render-agent-context-packet.py"


def run_render(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(SCRIPT), *args],
        cwd=ROOT,
        text=True,
        capture_output=True,
    )


def write_inventory(tmp_path: Path, projects: list[dict]) -> Path:
    path = tmp_path / "promotion-projects.json"
    path.write_text(json.dumps({"schema": "test", "schemaVersion": 1, "projects": projects}), encoding="utf-8")
    return path


def complete_project(project_id: str = "den-core") -> dict:
    return {
        "projectId": project_id,
        "status": "code_gate_repo_and_read_key_provisioned",
        "canonicalRemoteUrl": f"git@github.com:FuzzySlipper/{project_id}.git",
        "targetRemoteName": "canonical",
        "defaultBaseBranch": "main",
        "codeGateInstance": "den-code-gate",
        "codeGateRemoteUrl": f"ssh://git@192.168.1.10:3022/{project_id}/{project_id}.git",
        "codeGateRepo": f"{project_id}/{project_id}.git",
        "immutableRefPattern": "refs/heads/submissions/{project_id}/tasks/{task_id}/runs/{run_id}/attempt-{attempt_ordinal}",
        "convenienceRefPattern": "refs/heads/submissions/{project_id}/tasks/{task_id}/current",
        "allowedPathPrefixes": ["src/", "tests/"],
    }


def test_rendered_packet_includes_default_code_gate_sync_and_role_instructions() -> None:
    result = run_render(
        "--project", "den-core",
        "--task-id", "1444",
        "--run-id", "run-1444",
        "--submission-id", "sub-1444",
        "--head", "a" * 40,
        "--base", "b" * 40,
        "--review-round", "676",
        "--target-branch", "task/1444-promotion-aware-packets-default",
    )

    assert result.returncode == 0, result.stderr
    rendered = result.stdout
    assert "submission=sub-1444 ingress_ref=refs/heads/submissions/den-core/tasks/1444/runs/run-1444/attempt-001 head=" + "a" * 40 in rendered
    assert "base=" + "b" * 40 in rendered
    assert "review_round=676 target=task/1444-promotion-aware-packets-default" in rendered
    assert "code_gate_remote_url: `ssh://git@192.168.1.10:3022/den-core/den-core.git`" in rendered
    assert "changed_files_claim" in rendered
    assert "tests_run" in rendered
    assert "Fetch only `ingress_ref`" in rendered
    assert "verify the fetched commit equals `head_commit`" in rendered
    assert "request_den_publish_dry_run" in rendered
    assert "Den-native packets use snake_case" in rendered
    assert "Direct `DenPublish.Api` JSON uses camelCase" in rendered


def test_render_fails_closed_for_missing_promotion_metadata(tmp_path: Path) -> None:
    project = complete_project()
    del project["codeGateRemoteUrl"]
    inventory = write_inventory(tmp_path, [project])

    result = run_render("--project", "den-core", "--task-id", "1444", "--inventory", str(inventory))

    assert result.returncode != 0
    assert "project metadata incomplete" in result.stderr
    assert "codeGateRemoteUrl" in result.stderr


def test_render_fails_closed_for_duplicate_project_metadata(tmp_path: Path) -> None:
    inventory = write_inventory(tmp_path, [complete_project(), complete_project()])

    result = run_render("--project", "den-core", "--task-id", "1444", "--inventory", str(inventory))

    assert result.returncode != 0
    assert "ambiguous project metadata" in result.stderr


def test_render_fails_closed_for_not_onboarded_project(tmp_path: Path) -> None:
    project = complete_project()
    project["status"] = "metadata_needed"
    inventory = write_inventory(tmp_path, [project])

    result = run_render("--project", "den-core", "--task-id", "1444", "--inventory", str(inventory))

    assert result.returncode != 0
    assert "project is not promotion-onboarded" in result.stderr

#!/usr/bin/env python3
"""Render a promotion-ready agent context packet from secret-free project metadata."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
from string import Template

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INVENTORY = ROOT / "config" / "promotion-projects.json"
TEMPLATE = ROOT / "templates" / "agent-workflow" / "agent-context-packet.template.md"


def load_inventory(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def project_by_id(inventory: dict, project_id: str) -> dict:
    for project in inventory.get("projects", []):
        if project.get("projectId") == project_id:
            return project
    raise SystemExit(f"project not found: {project_id}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", required=True)
    parser.add_argument("--task-id", required=True)
    parser.add_argument("--run-id", default="<worker_run_id>")
    parser.add_argument("--submission-id", default="<submission_id>")
    parser.add_argument("--target-branch", default=None)
    parser.add_argument("--review-round", default="pending")
    parser.add_argument("--head", default="<head_commit>")
    parser.add_argument("--base", default="<base_commit>")
    parser.add_argument("--inventory", type=Path, default=DEFAULT_INVENTORY)
    args = parser.parse_args()

    inventory = load_inventory(args.inventory)
    project = project_by_id(inventory, args.project)
    target_branch = args.target_branch or f"task/{args.task_id}-<short-slug>"
    ingress_ref = project["immutableRefPattern"] \
        .replace("{project_id}", args.project) \
        .replace("{task_id}", str(args.task_id)) \
        .replace("{run_id}", args.run_id) \
        .replace("{attempt_ordinal}", "001")
    convenience_ref = project["convenienceRefPattern"] \
        .replace("{project_id}", args.project) \
        .replace("{task_id}", str(args.task_id))
    sync_line = (
        f"submission={args.submission_id} ingress_ref={ingress_ref} "
        f"head={args.head} base={args.base} review_round={args.review_round} target={target_branch}"
    )

    values = {
        "project_id": args.project,
        "status": project["status"],
        "task_id": args.task_id,
        "submission_id": args.submission_id,
        "run_id": args.run_id,
        "target_branch": target_branch,
        "review_round": args.review_round,
        "head_commit": args.head,
        "base_commit": args.base,
        "sync_line": sync_line,
        "ingress_ref": ingress_ref,
        "convenience_ref": convenience_ref,
        "canonical_remote_url": project.get("canonicalRemoteUrl") or "<canonical_remote_url>",
        "code_gate_remote_url": project.get("codeGateRemoteUrl") or "<code_gate_remote_url>",
        "target_remote_name": project.get("targetRemoteName", "canonical"),
        "default_base_branch": project.get("defaultBaseBranch", "main"),
        "allowed_path_prefixes": ", ".join(project.get("allowedPathPrefixes", [])) or "<none configured>",
    }
    template = Template(TEMPLATE.read_text(encoding="utf-8"))
    print(template.safe_substitute(values))


if __name__ == "__main__":
    main()

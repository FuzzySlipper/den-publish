#!/usr/bin/env python3
"""Read-only den-publish managed-workspace preflight.

Checks mixed ownership, git control-file lock risks, and OpenSSH config
symlink/permission hazards without mutating the workspace or reading secrets.
"""
from __future__ import annotations

import argparse
import json
import os
import pwd
import grp
import stat
import sys
from pathlib import Path
from typing import Any


def owner_name(st: os.stat_result) -> str:
    try:
        return pwd.getpwuid(st.st_uid).pw_name
    except KeyError:
        return str(st.st_uid)


def group_name(st: os.stat_result) -> str:
    try:
        return grp.getgrgid(st.st_gid).gr_name
    except KeyError:
        return str(st.st_gid)


def mode_string(st: os.stat_result) -> str:
    return f"{stat.S_IMODE(st.st_mode):04o}"


def permissions_too_open(mode: str) -> bool:
    bits = int(mode, 8)
    return bool(bits & stat.S_IWGRP or bits & stat.S_IWOTH)


def entry_for(path: Path, root: Path) -> dict[str, Any]:
    st = path.lstat()
    rel = "." if path == root else path.relative_to(root).as_posix()
    target = os.readlink(path) if path.is_symlink() else None
    return {
        "relativePath": rel,
        "owner": owner_name(st),
        "group": group_name(st),
        "mode": mode_string(st),
        "isDirectory": path.is_dir() and not path.is_symlink(),
        "isSymlink": path.is_symlink(),
        "symlinkTarget": target,
    }


def collect_entries(root: Path, max_entries: int) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = [entry_for(root, root)]
    for dirpath, dirnames, filenames in os.walk(root):
        current = Path(dirpath)
        names = sorted(dirnames) + sorted(filenames)
        for name in names:
            path = current / name
            entries.append(entry_for(path, root))
            if len(entries) >= max_entries:
                return entries
    return entries


def analyze(root: Path, expected_owner: str, entries: list[dict[str, Any]]) -> list[dict[str, str]]:
    findings: list[dict[str, str]] = []
    mismatched = [entry for entry in entries if entry["owner"] != expected_owner]
    if mismatched:
        findings.append({
            "code": "mixed_workspace_ownership",
            "message": f"Workspace contains {len(mismatched)} entries not owned by expected owner '{expected_owner}'.",
            "guidance": "Do not repair automatically. Inspect first; if approved, repair only the managed workspace path or run Git as the owning service account.",
        })

    git_control_mismatches = [
        entry for entry in entries
        if (entry["relativePath"] == ".git" or entry["relativePath"].startswith(".git/"))
        and entry["owner"] != expected_owner
    ]
    if git_control_mismatches:
        findings.append({
            "code": "git_config_lock_risk",
            "message": "Git control files are not owned by the expected service user; git may fail on config.lock/ref locks.",
            "guidance": "Pause promotion. Do not let audit-warn mask workspace/SSH fetch failures; repair with explicit sysadmin approval.",
        })

    ssh_configs = [entry for entry in entries if entry["relativePath"] == ".ssh/config" or entry["relativePath"].endswith("/.ssh/config")]
    for entry in ssh_configs:
        if entry["isSymlink"]:
            findings.append({
                "code": "ssh_config_symlink_review_required",
                "message": f"OpenSSH config '{entry['relativePath']}' is a symlink to '{entry['symlinkTarget']}'.",
                "guidance": "Prefer ssh -F /dev/null with explicit identity, known_hosts, BatchMode, IdentitiesOnly, and StrictHostKeyChecking options.",
            })
            target = Path(str(entry["symlinkTarget"]))
            if not target.is_absolute():
                target = root / entry["relativePath"]
                target = target.parent / str(entry["symlinkTarget"])
            if target.exists():
                target_mode = mode_string(target.lstat())
                if permissions_too_open(target_mode):
                    findings.append({
                        "code": "ssh_config_symlink_target_permissions_too_open",
                        "message": f"OpenSSH config symlink target permissions are '{target_mode}', which are writable by group/other.",
                        "guidance": "Fix target permissions before using the config, or bypass config entirely with ssh -F /dev/null.",
                    })
        if permissions_too_open(str(entry["mode"])):
            findings.append({
                "code": "ssh_config_permissions_too_open",
                "message": f"OpenSSH config '{entry['relativePath']}' permissions are '{entry['mode']}', writable by group/other.",
                "guidance": "Fix config permissions or avoid reading config with ssh -F /dev/null.",
            })
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", required=True, help="Managed workspace path to inspect")
    parser.add_argument("--expected-owner", help="Expected Unix owner; defaults to the workspace root owner")
    parser.add_argument("--max-entries", type=int, default=5000)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    root = Path(args.workspace).resolve()
    if not root.exists() or not root.is_dir():
        payload = {"status": "blocked", "findings": [{"code": "workspace_missing", "message": f"Workspace path is missing or not a directory: {root}", "guidance": "Create/recreate the managed workspace as the service user before promotion."}]}
        print(json.dumps(payload, indent=2) if args.json else payload["findings"][0]["message"])
        return 1

    root_owner = owner_name(root.lstat())
    expected_owner = args.expected_owner or root_owner
    entries = collect_entries(root, max(1, args.max_entries))
    findings = analyze(root, expected_owner, entries)
    payload = {
        "status": "healthy" if not findings else "blocked",
        "workspace": str(root),
        "expectedOwner": expected_owner,
        "entryCount": len(entries),
        "findings": findings,
    }
    if args.json:
        print(json.dumps(payload, indent=2, sort_keys=True))
    else:
        if not findings:
            print(f"healthy: {root} owned consistently for {expected_owner}")
        else:
            for finding in findings:
                print(f"{finding['code']}: {finding['message']}")
                print(f"  guidance: {finding['guidance']}")
    return 0 if not findings else 1


if __name__ == "__main__":
    raise SystemExit(main())

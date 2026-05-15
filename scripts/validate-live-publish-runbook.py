#!/usr/bin/env python3
"""Validate live-publish runbooks preserve credential-isolation guardrails."""
from __future__ import annotations

import argparse
import json
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

REQUIRED_BY_FILE = {
    "docs/live-publish-runbook.md": [
        "Do not execute this runbook from a worker sandbox",
        "credential owner and storage location",
        "DenPublish__Publishing__Enabled=true",
        "DenPublish__Publishing__CredentialMode=ssh_command",
        "DenPublish__Publishing__GitSshCommand=<redacted approved ssh command>",
        "GIT_TERMINAL_PROMPT=0",
        "display=ssh_command",
        "fingerprint",
        "git ls-remote <canonical> refs/heads/<smoke-branch>",
        "disable live publishing",
        "credential_unavailable",
        "[REDACTED]",
    ],
    "docs/live-publish-rehearsal-checklist.md": [
        "Den Core and worker sandboxes will not receive canonical push credentials",
        "den-publish.service -> child git push with GIT_SSH_COMMAND + GIT_TERMINAL_PROMPT=0",
        "livePublishing.enabled=false",
        "liveCredentialPolicy.configured=false",
        "ssh -F /dev/null",
        "IdentitiesOnly=yes",
        "BatchMode=yes",
        "UserKnownHostsFile=/home/agents/runtime/den-publish/ssh/known_hosts",
        "StrictHostKeyChecking=yes",
        "timestamped backup",
        "decision.validateOnly=false",
        "git ls-remote <canonical-remote-url> refs/heads/<target-branch>",
        "git push <canonical-remote-url> :refs/heads/<target-branch>",
        "If `/config/status` ever exposes raw credential material",
    ],
    "templates/agent-workflow/live-publish-approval-request.template.md": [
        "This request is required before any canonical push credential is placed",
        "Den Core and worker sandboxes will not receive canonical push credentials.",
        "ssh_command",
        "GIT_SSH_COMMAND=[REDACTED]",
        "GIT_TERMINAL_PROMPT=0",
        "ssh -F /dev/null",
        "IdentitiesOnly=yes",
        "BatchMode=yes",
        "StrictHostKeyChecking=yes",
        "disable live publishing afterward",
        "Approval statement",
    ],
}

FORBIDDEN_DIRECTIVES = [
    "give workers GitHub credentials",
    "configure Den Core GitHub credentials",
    "use ambient agent GitHub credentials",
    "livePublishing.enabled=true by default",
]


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def validate_files() -> None:
    for rel, required_terms in REQUIRED_BY_FILE.items():
        path = ROOT / rel
        require(path.exists(), f"missing required file: {rel}")
        text = path.read_text(encoding="utf-8")
        for term in required_terms:
            require(term in text, f"{rel}: missing required term: {term}")
        lowered = text.lower()
        for forbidden in FORBIDDEN_DIRECTIVES:
            require(forbidden.lower() not in lowered, f"{rel}: forbidden directive found: {forbidden}")


def fetch_status(url: str) -> dict:
    with urllib.request.urlopen(url, timeout=5) as response:  # noqa: S310 - loopback/operator URL
        return json.loads(response.read().decode("utf-8"))


def validate_status(url: str) -> None:
    status = fetch_status(url)
    require(status.get("configurationContract") == "den-publish-runtime-config-v2", "unexpected configuration contract")
    live = status.get("livePublishing", {})
    require(live.get("enabled") is False, "livePublishing.enabled must be false outside approved smoke window")
    credential = status.get("liveCredentialPolicy", {})
    require(credential.get("configured") is False, "live credential policy must be unconfigured outside approved smoke window")
    require(credential.get("value") in ("", None), "live credential policy raw value should not be exposed")
    for project in status.get("projectPolicies", []):
        read_credential = project.get("codeGateReadCredential") or {}
        if read_credential.get("configured"):
            require(read_credential.get("value") == "[redacted]", "configured code-gate credential must be redacted")
            require(read_credential.get("display") == "ssh_command", "configured code-gate credential display should be ssh_command")
            require(bool(read_credential.get("fingerprint")), "configured code-gate credential should expose fingerprint")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--status-url", help="Optional live /config/status URL to verify safe disabled state")
    args = parser.parse_args()

    validate_files()
    if args.status_url:
        validate_status(args.status_url)
    print("live_publish_runbook=ok")


if __name__ == "__main__":
    main()

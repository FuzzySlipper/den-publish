from __future__ import annotations

import importlib.util
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "check-promotion-workflow-watchdog.py"


def load_module():
    spec = importlib.util.spec_from_file_location("promotion_workflow_watchdog", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def test_monitored_projects_include_rollout_statuses_only():
    module = load_module()
    inventory = {
        "projects": [
            {"projectId": "den-core", "status": "code_gate_repo_and_read_key_provisioned"},
            {"projectId": "den-router", "status": "dry_run_ready"},
            {"projectId": "future", "status": "metadata_incomplete"},
        ]
    }

    assert module.monitored_projects(inventory) == ["den-core", "den-router"]


def test_status_check_fails_closed_for_live_publish_enabled():
    module = load_module()
    status = {
        "configurationContract": "den-publish-runtime-config-v2",
        "livePublishing": {"enabled": True},
        "liveCredentialPolicy": {"configured": False},
    }

    findings = module.check_status_payload(status)

    assert any(f.code == "live_publishing_enabled" and f.severity == "error" for f in findings)


def test_status_check_allows_approved_redacted_live_policy():
    module = load_module()
    status = {
        "configurationContract": "den-publish-runtime-config-v2",
        "livePublishing": {"enabled": True},
        "liveCredentialPolicy": {
            "configured": True,
            "display": "ssh_command",
            "value": "[redacted]",
            "fingerprint": "abcdef1234567890",
        },
    }

    findings = module.check_status_payload(status, allow_live_enabled=True)

    assert findings == []


def test_status_check_requires_live_enabled_in_approved_mode():
    module = load_module()
    status = {
        "configurationContract": "den-publish-runtime-config-v2",
        "livePublishing": {"enabled": False},
        "liveCredentialPolicy": {"configured": False},
    }

    findings = module.check_status_payload(status, allow_live_enabled=True)

    assert any(f.code == "live_publishing_not_enabled" and f.severity == "error" for f in findings)


def test_status_check_rejects_unredacted_live_policy_even_when_allowed():
    module = load_module()
    status = {
        "configurationContract": "den-publish-runtime-config-v2",
        "livePublishing": {"enabled": True},
        "liveCredentialPolicy": {
            "configured": True,
            "display": "ssh_command",
            "value": "ssh -i /secret/key",
            "fingerprint": "abcdef1234567890",
        },
    }

    findings = module.check_status_payload(status, allow_live_enabled=True)

    assert any(f.code == "live_credentials_not_explicit_redacted" for f in findings)


def test_runtime_inventory_alignment_reports_missing_policy():
    module = load_module()
    status = {
        "projectPolicies": [
            {"projectId": "den-core"},
        ]
    }

    findings = module.check_runtime_inventory_alignment(["den-core", "den-router"], status)

    assert len(findings) == 1
    assert findings[0].code == "runtime_policy_missing"
    assert "den-router" in findings[0].detail


def test_sanitized_environment_removes_credential_names(monkeypatch):
    module = load_module()
    monkeypatch.setenv("DEN_CODE_GATE_ADMIN_TOKEN", "secret")
    monkeypatch.setenv("GH_TOKEN", "secret")
    monkeypatch.setenv("SSH_AUTH_SOCK", "/tmp/socket")
    monkeypatch.setenv("NORMAL_SETTING", "kept")

    env = module.sanitized_environment()

    assert "DEN_CODE_GATE_ADMIN_TOKEN" not in env
    assert "GH_TOKEN" not in env
    assert "SSH_AUTH_SOCK" not in env
    assert env["NORMAL_SETTING"] == "kept"


def test_project_filter_alignment_uses_full_monitored_inventory(monkeypatch):
    module = load_module()
    args = type("Args", (), {
        "promotion_inventory": Path("unused.json"),
        "project": "den-router",
        "systemd_scope": "system",
        "tunnel_service": "den-publish-den-core-tunnel.service",
        "service_user": None,
        "status_url": "http://127.0.0.1:5090/config/status",
        "mcp_url": "http://192.168.1.10:5199/mcp",
        "allow_live_enabled": False,
    })()
    inventory = {
        "projects": [
            {"projectId": "den-core", "status": "code_gate_repo_and_read_key_provisioned"},
            {"projectId": "den-router", "status": "dry_run_ready"},
        ]
    }
    status = {
        "configurationContract": "den-publish-runtime-config-v2",
        "livePublishing": {"enabled": False},
        "liveCredentialPolicy": {"configured": False},
        "projectPolicies": [{"projectId": "den-core"}, {"projectId": "den-router"}],
    }

    monkeypatch.setattr(module, "load_json", lambda path: inventory)
    monkeypatch.setattr(module, "check_systemd_service", lambda *args: [])
    monkeypatch.setattr(module, "check_status", lambda url, **kwargs: (status, []))
    monkeypatch.setattr(module, "check_mcp_facade", lambda url: [])
    monkeypatch.setattr(module, "run_subcheck", lambda *args, **kwargs: [])
    monkeypatch.setattr(module, "check_project_readiness", lambda project_id, **kwargs: [])

    findings, summary = module.collect_findings(args)

    assert findings == []
    assert summary["projectsChecked"] == ["den-router"]
    assert summary["monitoredProjects"] == ["den-core", "den-router"]

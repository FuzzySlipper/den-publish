#!/usr/bin/env python3
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

REQUIRED_DOC_TERMS = [
    'den-code-gate',
    'submission_id',
    'ingress_ref',
    'head_commit',
    'review_round_id',
    'scope_overrides',
    '/promotion/dry-run',
    'validate_only',
    'Orchestrators approve high-level Den decisions',
]

REQUIRED_JSON_PATHS = {
    'templates/agent-workflow/coder-completion-packet.template.json': [
        ('schema',),
        ('submission_id',),
        ('code_gate_remote_url',),
        ('ingress_ref',),
        ('base_commit',),
        ('head_commit',),
        ('changed_files_claim',),
        ('tests_run',),
    ],
    'templates/agent-workflow/orchestrator-publish-decision.template.json': [
        ('decision', 'decision_id'),
        ('decision', 'submission_id'),
        ('decision', 'expected_head_commit'),
        ('decision', 'review_round_id'),
        ('decision', 'scope_override_ids'),
        ('decision', 'scope_overrides'),
        ('decision', 'validate_only'),
        ('submission', 'review', 'verdict'),
        ('submission', 'head_commit'),
        ('submission', 'ingress_ref'),
    ],
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def get_path(obj: object, path: tuple[str, ...]) -> object:
    cur = obj
    for part in path:
        require(isinstance(cur, dict) and part in cur, f'missing JSON path: {".".join(path)}')
        cur = cur[part]
    return cur


def main() -> None:
    doc = (ROOT / 'docs' / 'agent-workflow-ux.md').read_text(encoding='utf-8')
    for term in REQUIRED_DOC_TERMS:
        require(term in doc, f'missing required doc term: {term}')

    for rel, paths in REQUIRED_JSON_PATHS.items():
        data = json.loads((ROOT / rel).read_text(encoding='utf-8'))
        for path in paths:
            get_path(data, path)

    checklist = (ROOT / 'templates' / 'agent-workflow' / 'real-task-test-checklist.md').read_text(encoding='utf-8')
    for term in ['livePublishing.enabled=false', 'ssh -F /dev/null', '/promotion/publish', 'disable live publishing']:
        require(term in checklist, f'missing checklist term: {term}')

    print('agent_workflow_ux_docs=ok')


if __name__ == '__main__':
    main()

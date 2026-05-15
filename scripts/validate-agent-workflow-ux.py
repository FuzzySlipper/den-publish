#!/usr/bin/env python3
from __future__ import annotations

import json
import re
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
    'validateOnly',
    'Orchestrators approve high-level Den decisions',
    '_global/agent-code-promotion-policy',
    'Den Core field-based `den-publish` dry-run facade',
    'scripts/check-promotion-metadata-drift.py --project <project_id>',
    'scripts/check-code-gate-repo.py --project <project_id>',
    'docs/code-gate-repo-provisioning.md',
]

REQUIRED_AGENT_CONTEXT_TERMS = [
    'worker -> den-code-gate -> Den review -> Den Core den-publish facade -> den-publish /promotion/dry-run',
    'Legacy Den Core `publish_reviewed_branch` / `publish_worker_branch` are compatibility only',
    'submission=sub_example_1434 ingress_ref=refs/heads/submissions/den-channels/tasks/1416/runs/run-example-1434/attempt-001',
    'python3 scripts/check-promotion-metadata-drift.py --project den-channels',
    'Direct `DenPublish.Api` JSON uses camelCase only',
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
        ('workspacePath',),
        ('allowedPathPrefixes',),
        ('decision', 'decisionId'),
        ('decision', 'submissionId'),
        ('decision', 'targetBranch'),
        ('decision', 'expectedHeadCommit'),
        ('decision', 'reviewRoundId'),
        ('decision', 'scopeOverrideIds'),
        ('decision', 'scopeOverrides'),
        ('decision', 'validateOnly'),
        ('submission', 'review', 'verdict'),
        ('submission', 'review', 'reviewRoundId'),
        ('submission', 'submissionId'),
        ('submission', 'baseCommit'),
        ('submission', 'headCommit'),
        ('submission', 'ingressRef'),
        ('submission', 'codeGateRemoteUrl'),
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


def find_snake_case_keys(obj: object, prefix: str = '') -> list[str]:
    findings: list[str] = []
    if isinstance(obj, dict):
        for key, value in obj.items():
            dotted = f'{prefix}.{key}' if prefix else key
            if '_' in key:
                findings.append(dotted)
            findings.extend(find_snake_case_keys(value, dotted))
    elif isinstance(obj, list):
        for index, value in enumerate(obj):
            findings.extend(find_snake_case_keys(value, f'{prefix}[{index}]'))
    return findings


def first_json_block_after(text: str, marker: str) -> object:
    start = text.index(marker)
    match = re.search(r'```json\n(.*?)\n```', text[start:], re.DOTALL)
    require(match is not None, f'missing JSON block after {marker!r}')
    return json.loads(match.group(1))


def main() -> None:
    doc = (ROOT / 'docs' / 'agent-workflow-ux.md').read_text(encoding='utf-8')
    for term in REQUIRED_DOC_TERMS:
        require(term in doc, f'missing required doc term: {term}')

    for rel, paths in REQUIRED_JSON_PATHS.items():
        data = json.loads((ROOT / rel).read_text(encoding='utf-8'))
        for path in paths:
            get_path(data, path)
        if rel == 'templates/agent-workflow/orchestrator-publish-decision.template.json':
            snake_case_keys = find_snake_case_keys(data)
            require(not snake_case_keys, f'orchestrator API payload contains snake_case keys: {snake_case_keys}')
            require(get_path(data, ('decision', 'validateOnly')) is True, 'decision.validateOnly must be true for /promotion/dry-run template')


    migration_doc = (ROOT / 'docs' / 'den-core-boundary-migration.md').read_text(encoding='utf-8')
    migration_payload = first_json_block_after(migration_doc, 'Minimum `/promotion/dry-run` API payload fields')
    migration_snake_case_keys = find_snake_case_keys(migration_payload)
    require(not migration_snake_case_keys, f'boundary migration API JSON contains snake_case keys: {migration_snake_case_keys}')

    deployment_doc = (ROOT / 'docs' / 'deployment.md').read_text(encoding='utf-8')
    for term in ['scopeOverrideIds', 'scopeOverrides[]', 'overrideId', 'approvedBy']:
        require(term in deployment_doc, f'missing camelCase deployment API term: {term}')
    for term in ['scope_override_ids', 'scope_overrides[]', 'override_id', 'approved_by']:
        require(term not in deployment_doc, f'deployment API guidance still contains snake_case term: {term}')

    architecture_doc = (ROOT / 'docs' / 'architecture.md').read_text(encoding='utf-8')
    for term in ['decision.scopeOverrideIds', 'scopeOverrides[]', 'approvedBy']:
        require(term in architecture_doc, f'missing camelCase architecture API term: {term}')
    for term in ['decision.scope_override_ids', 'structured `scope_overrides[]` entry', 'non-empty `reason` and `approved_by`']:
        require(term not in architecture_doc, f'architecture API guidance still contains snake_case term: {term}')

    checklist = (ROOT / 'templates' / 'agent-workflow' / 'real-task-test-checklist.md').read_text(encoding='utf-8')
    for term in ['livePublishing.enabled=false', 'ssh -F /dev/null', '/promotion/publish', 'disable live publishing']:
        require(term in checklist, f'missing checklist term: {term}')

    context_template = (ROOT / 'templates' / 'agent-workflow' / 'agent-context-packet.template.md').read_text(encoding='utf-8')
    for term in [
        'Default workflow: `worker -> den-code-gate -> Den review -> Den Core den-publish facade -> den-publish /promotion/dry-run`',
        'Legacy Den Core `publish_reviewed_branch` / `publish_worker_branch` are compatibility only',
        'Direct `DenPublish.Api` JSON uses camelCase only',
        'python3 scripts/check-promotion-metadata-drift.py --project $project_id',
    ]:
        require(term in context_template, f'missing agent context template term: {term}')

    context_example = (ROOT / 'examples' / 'agent-workflow' / 'den-channels-dry-run-context.example.md').read_text(encoding='utf-8')
    for term in REQUIRED_AGENT_CONTEXT_TERMS:
        require(term in context_example, f'missing generated context packet term: {term}')
    for negative_guidance in [
        'Do not use `/data/dev`, `/mnt/den-srv/dev`, reviewed-bundle imports, or worker-local checkout paths as the standard promotion route.',
        'Legacy Den Core `publish_reviewed_branch` / `publish_worker_branch` are compatibility only',
    ]:
        require(negative_guidance in context_example, f'missing negative legacy-shim guidance: {negative_guidance}')


    code_gate_doc = (ROOT / 'docs' / 'code-gate-repo-provisioning.md').read_text(encoding='utf-8')
    for term in [
        'Workers may receive code-gate-only submission access',
        'refs/heads/submissions/{project_id}/tasks/{task_id}/runs/{run_id}/attempt-{attempt_ordinal}',
        'python3 scripts/check-code-gate-repo.py --project den-channels',
        'DEN_CODE_GATE_ADMIN_TOKEN=<redacted>',
        'DEN_CODE_GATE_REPO_SSH_COMMAND_DEN_CHANNELS',
        'Do not paste Forgejo admin tokens, deploy private keys, `GIT_SSH_COMMAND` values, or code-gate private keys into Den task messages or worker prompts.',
        'Authenticated repo existence/create was intentionally skipped',
    ]:
        require(term in code_gate_doc, f'missing code-gate provisioning doc term: {term}')

    code_gate_template = (ROOT / 'templates' / 'agent-workflow' / 'code-gate-repo-provisioning-request.template.md').read_text(encoding='utf-8')
    for term in [
        'code-gate-only push access, no canonical push credentials',
        'DEN_CODE_GATE_ADMIN_TOKEN=<redacted>',
        'Do not paste `DEN_CODE_GATE_ADMIN_TOKEN`, private keys, or full `GIT_SSH_COMMAND` values',
    ]:
        require(term in code_gate_template, f'missing code-gate provisioning template term: {term}')

    guidance_doc = (ROOT / 'docs' / 'agent-guidance-rollout.md').read_text(encoding='utf-8')
    for term in [
        '_global/agent-code-promotion-policy',
        'publish_reviewed_branch` as legacy/compatibility',
        'submission=<submission_id> ingress_ref=<ingress_ref> head=<head_commit> base=<base_commit> review_round=<review_round_id or pending> target=<target_branch>',
        'camelCase',
    ]:
        require(term in guidance_doc, f'missing guidance rollout term: {term}')

    print('agent_workflow_ux_docs=ok')


if __name__ == '__main__':
    main()

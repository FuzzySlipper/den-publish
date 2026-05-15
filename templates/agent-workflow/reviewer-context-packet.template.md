# Reviewer context packet template

Use this packet to review one immutable code-gate submission. Do not review `current` refs as authority.

## Submission identity

- project_id: `<den_project_id>`
- task_id: `<task_id>`
- submission_id: `<submission_id>`
- worker_run_id: `<worker_run_id>`
- attempt_ordinal: `<attempt_ordinal>`
- parent_submission_id: `<parent_submission_id or none>`

## Git object source

- code_gate_instance: `den-code-gate`
- code_gate_remote_url: `ssh://git@192.168.1.10:3022/<owner_or_project>/<project_repo>.git`
- ingress_ref: `refs/heads/submissions/<den_project_id>/tasks/<task_id>/runs/<worker_run_id>/attempt-001`
- expected_head_commit: `<40-char-head-sha>`
- base_branch: `main`
- base_commit: `<40-char-base-sha>`

## Review instructions

1. Fetch only the immutable `ingress_ref` from `code_gate_remote_url`.
2. Verify the fetched commit equals `expected_head_commit` before reviewing.
3. Review diff against `base_commit` unless the task explicitly records a different base.
4. Compare observed changed files with `changed_files_claim`.
5. Post structured findings to Den and set a verdict bound to this exact `submission_id` and `expected_head_commit`.

## Required reviewer output

```json
{
  "submission_id": "<submission_id>",
  "head_commit": "<40-char-head-sha>",
  "base_commit": "<40-char-base-sha>",
  "review_round_id": 0,
  "verdict": "looks_good",
  "findings": [
    {
      "finding_id": "finding_<id>",
      "blocking": true,
      "resolved": false,
      "override_id": "override_<id_or_null>"
    }
  ],
  "notes": "<summary>"
}
```

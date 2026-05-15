# Rework / rereview packet template

Use this when a coder responds to reviewer findings with a follow-up commit.

## Prior submission

- parent_submission_id: `<prior_submission_id>`
- prior_head_commit: `<prior-head-sha>`
- prior_review_round_id: `<prior-review-round-id>`
- findings addressed: `<finding ids>`

## New immutable submission

- submission_id: `<new_submission_id>`
- attempt_ordinal: `<prior + 1>`
- ingress_ref: `refs/heads/submissions/<den_project_id>/tasks/<task_id>/runs/<worker_run_id>/attempt-00N`
- base_commit: `<same-or-updated-base-sha>`
- head_commit: `<new-40-char-head-sha>`
- changed_files_claim: `<updated file list>`
- tests_run: `<updated test evidence>`

## Orchestrator steps

1. Mark the prior submission superseded when the new submission is accepted.
2. Request a new review round for the new `submission_id` and `head_commit`.
3. Do not reuse a prior `looks_good` verdict for the new head unless a future explicit carry-forward policy exists.
4. Only publish the latest approved submission.

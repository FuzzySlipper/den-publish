# den-publish architecture notes

This repo implements the service side of the Den `den-code-gate` publishing contract.

Authoritative design: Den doc `den-code-gate-den-publish-workflow-contract-1420`.

Initial service boundaries:

- Den owns workflow state and audit records.
- `den-code-gate` Forgejo owns candidate Git object storage.
- Reviewers are agents/humans that fetch exact code-gate refs.
- `den-publish` owns final Git validation/fetch/push mechanics and credentials.

The first scaffold intentionally contains only health endpoints and core contract primitives. Promotion engine work belongs to task #1424 after code-gate and Den submission contracts are in place.

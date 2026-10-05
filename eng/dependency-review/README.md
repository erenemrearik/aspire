# Dependency review pilot

This offline Python 3 check enforces a small set of existing documented holds
and Npgsql/EF Core major constraints, plus root NuGet download sources and
extension Yarn resolved download sources. It does not restore or execute
dependencies, query advisories, parse every manifest, or certify compatibility.
Audit sources are not download sources. Missing/malformed inputs fail explicitly.

```bash
python3 eng/dependency-review/check.py
python3 -m unittest discover -s eng/dependency-review -p 'test_*.py'
```

Exit codes: `0` means these constraints passed, `1` means a constraint failed,
and `2` means inspection could not complete. `--root <checkout>` inspects another
isolated checkout using this script's policy, without modifying it.

`constraints.json` intentionally covers documented exceptions, not a snapshot
of every dependency. An intentional migration must update the policy and nearby
manifest rationale together, with consumer evidence in the PR. Adding a hold
requires a focused positive/negative test; routine patch updates remain allowed.

Generated workflow coherence uses the existing
`.github/workflows/validate-agentic-workflows.yml` compiler gate rather than a
second compiler or a wrapper/runtime-version equality check. Approved-source
checks cannot establish feed reachability or permissions. The evidence procedure
is in `.agents/skills/pr-testing/dependency-review.md`.

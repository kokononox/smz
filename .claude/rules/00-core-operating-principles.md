# Core operating principles

- Inspect the relevant source, workflow, hardware log, bundle, tests, and latest failure before editing.
- Make the smallest reversible change that fixes the verified root cause; do not rewrite unrelated code.
- Preserve project formats, `.amsj` compatibility, route semantics, markers, recovery behavior, and rollback paths.
- State assumptions. Stop and ask when ambiguity affects hardware safety, release contents, data loss, credentials, or merge scope.
- After two failed attempts with the same strategy, stop retrying, re-read the evidence, and change the hypothesis.
- Do not hide a failure by deleting, weakening, or skipping a valid test.
- Finish with changed files, tests run, remaining risks, required hardware actions, and external side effects.

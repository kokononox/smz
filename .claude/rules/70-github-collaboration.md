# GitHub collaboration

- Use a focused branch and Pull Request for source, firmware, workflow, packaging, and governance changes.
- PRs must document root-cause evidence, scope, tests, hardware impact, bundle/manifest impact, rollback, and the next manual test.
- Prefer Conventional Commit prefixes: `fix:`, `feat:`, `test:`, `docs:`, `ci:`, and `chore:`.
- Do not merge with unresolved failures, unknown package contents, or an unreviewed hardware-safety impact.
- Close or update overlapping stale PRs before merging.
- Record durable design decisions in `docs/ARCHITECTURE_DECISIONS.md` and reproducible failures as Issues.
- Publish user-testable immutable ZIPs as GitHub Releases with a checksum; do not rely only on expiring Actions artifacts.

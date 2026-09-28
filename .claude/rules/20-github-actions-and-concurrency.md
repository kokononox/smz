# GitHub Actions and concurrency

- New or materially changed workflows must declare least-privilege `permissions`, `timeout-minutes`, and an explicit `concurrency` policy.
- Use `cancel-in-progress: true` only for read-only validation. Release, corrective, packaging, and state-writing jobs must normally preserve in-flight work.
- Keep build, hardware-package, release, and corrective workflows in separate concurrency groups.
- Prevent recursive push loops with precise event, branch, path, and commit guards.
- Bound polling and retries with timeout, backoff, and a terminal result.
- Never rerun an obsolete failed SHA when the fix exists only on a newer commit.
- Generated commits must be atomic and conflict-aware; never force-push `main` or the active release branch.
- Keep diagnostic artifact retention short; publish hardware-test packages as versioned Releases.

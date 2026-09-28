# Contributing to smz

## Change flow
1. Start from the current target branch and create a focused branch.
2. Reproduce the issue and record the smallest decisive evidence.
3. Add or update a regression test before broadening the change.
4. Run focused checks, then the applicable full suites.
5. Open a Pull Request using the repository template.
6. Merge only after required checks pass and hardware/package impact is understood.
7. Publish hardware-testable packages as versioned Releases with SHA256.

## Commit style
Use `fix:`, `feat:`, `test:`, `docs:`, `ci:`, or `chore:` followed by a concise English description.

## Safety
Do not commit secrets or sensitive logs. Do not force-push shared release branches. Do not change Golden 100 or verified mouse/sound/restart behavior outside the requested scope.

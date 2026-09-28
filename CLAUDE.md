# smz development instructions

Read the applicable files under `.claude/rules/` before changing code, firmware, workflows, packaging, or releases.

Project priorities, in order:
1. Preserve verified hardware behavior and safety.
2. Fix the evidenced root cause with the smallest reversible change.
3. Keep Pico memory, manifest, backward compatibility, and release contracts green.
4. Deliver through a focused branch and pull request; never bypass failing checks.

At handoff report changed files, tests, hardware evidence, remaining risk, manual test steps, and external side effects.

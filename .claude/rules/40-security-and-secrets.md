# Security and secrets

- Store credentials only in GitHub Secrets, protected environments, or the approved connection manager; never commit tokens, cookies, passwords, private keys, or unredacted sensitive logs.
- Default workflow permissions to `contents: read`; grant write only to the job that actually publishes or updates state.
- Never execute untrusted pull-request code with production secrets; do not use `pull_request_target` for builds.
- Validate ZIP paths and filenames; reject traversal, unexpected executables, and manifest mismatches.
- Hardware diagnostics must remain safe by default: automatic probes may identify/PING, but must not send Guard/HID actions without an explicit user operation.
- If exposure is suspected, stop, revoke or rotate first, remove the secret safely, and document the incident without repeating its value.

# Testing and delivery

Before declaring a change complete:
1. Compile-check every changed Python file and build changed .NET projects when available.
2. Validate changed workflow YAML.
3. Run the smallest focused regression first, then the full applicable portable and Windows suites.
4. For firmware packaging, verify manifest inventory, every checksum, archive members, runtime size gates, and release assets.
5. For memory fixes, preserve stage telemetry and test the exact project or route shape that reproduced the failure.

A missing tool or fixture is not a pass: report it explicitly and rely on CI only after the focused local checks pass. Hardware-only claims remain pending until the Pico/Windows log proves them.

---
name: affected-tests
description: List the Svg.Skia test projects a branch's changes can reach, so the local gate runs those instead of the whole suite. Use when running the desk profile's test gate, or when asked which tests a change needs.
---

# Test only what a change can reach

```sh
python3 .claude/skills/affected-tests/affected_tests.py      # against origin/master
```

It prints one test project per line, `ALL`, or nothing. Then, after
`dotnet build Svg.Skia.slnx -c Release`:

- **a list:** `dotnet test <project> -c Release --no-build` for each;
- **`ALL`:** `dotnet test Svg.Skia.slnx -c Release --no-build`, since the change reaches the build
  itself (`Directory.*`, `build/`, `global.json`, the solution, a submodule pointer) or a file no
  project owns;
- **nothing:** docs or tooling only; say no tests ran.

The pull request's CI still runs the whole suite on Linux; this is the local gate only. The full
suite is held up by `Svg.Studio.UnitTests` (~4 min) and `Svg.Skia.UnitTests` (~3 min), which most
changes outside `src/Svg.Studio` and the core pipeline no longer reach.

A test that opens repository files at runtime is invisible to project references: add it to
`RUNTIME_READERS` in the script, or it will be skipped when only those files change.

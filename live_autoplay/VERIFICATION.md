# Native-game package verification

Packaging checks performed on macOS arm64 on 2026-10-31. This record covers the
portable package and its relationship to the existing working integration.

| Check | Result |
| --- | --- |
| Offline launcher | Both released models load; model identities and mod encoder fields agree; zero game connections/actions |
| Python suite | 64 tests passed, covering the installer, controller, action receipts, pause behavior, accounting, model loading, recorded observations and service lifetime |
| Working-source comparison | Exact Q-values, candidate IDs and selected actions match for five retained cases: Choice, Battle, Calendar, Hex and Ravenous |
| Native/reference cases | Calendar, Hex and Ravenous produce identical collated model inputs and scores; native scores also match the recorded values |
| Relocated repository | A separate copy passes `run.py --check` in an isolated Python process, resolving its own models/runtime without the private project tree |
| C# source | All 77 copied source files compile against the installed v0.111.0 game assemblies with .NET 9; zero errors and one existing nullable warning in the general state builder |
| Retained implementation | Mod DLL and all 77 C# files preserved; controller, transport, journaling, process lease and native-potion adapter preserved |
| Contract | Only four informational file paths changed; IDs, feature definitions and compatibility rules preserved |
| Game-owned service lifetime | An exited parent process stops the controller, closes the journal and releases the lease; a terminal launch requires explicit shutdown |

The installer was exercised against **temporary game directories**, including
preview without writes, preserving a virtual environment's Python path, backup of
existing mod files, preservation of unrelated files, rejection of an active game,
and restoration after an injected write failure. The installed Steam game and
its working mod were not modified by these checks.

Run the reproducible package checks from the repository root:

```sh
python live_autoplay/run.py --check
python -m pip install -r live_autoplay/requirements-dev.txt
python -m pytest -q -p no:cacheprovider live_autoplay/tests
```

Supply `--dotnet` to the launcher, or `RS1_DOTNET` to pytest, if the .NET 9
runtime is outside the normal search paths. A developer can additionally build
the C# source using the command in the [guide](README.md#development-and-scope).

## What these checks do not establish

No new full native-game run was performed with the packaged launcher and
installer. The prebuilt DLL comes from the working installation; the separate
source compilation is not a claim of binary reproducibility. Windows and Linux
gameplay, later game versions, other characters, other ascensions and multiplayer
are unverified or outside the adapter's deployment scope.

The five recorded cases check specific inference and projection behavior, not
every game mechanic or execution path. They provide no native-game win-rate
estimate. The published **68/256** result remains the separate simulator
benchmark in [the main result report](../docs/RESULTS.md).

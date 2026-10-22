# Third-party notices

This file records source attribution and notices for material included in this
release. Original contributions to this release are provided under the root
[MIT license](LICENSE). Existing third-party components retain their own
copyright notices and license terms as preserved below. No license is granted
to commercial game assets or other material that is not included in the release.

## STS2 emulator ancestry

The C# engine derives from
[Zamiell/slay-the-spire-2-emulator](https://github.com/Zamiell/slay-the-spire-2-emulator),
as credited in the local accepted build08 ancestor's README. The ancestor's MIT
license is preserved verbatim in
[`simulator/licenses/Sts2Emulator-MIT.txt`](simulator/licenses/Sts2Emulator-MIT.txt).
Its copyright line is `Copyright (c) 2024 The Complete Contributors`.

The released source includes local changes for game v0.111.0, training RNG and
combat reset support, a Juggernaut repair, and the combat/full-run worker
interfaces. The source relationship and exact file identities are recorded in
[`simulator/source/engine/whole_game_build08/SOURCE.json`](simulator/source/engine/whole_game_build08/SOURCE.json)
and [`simulator/provenance/RELEASE_PROVENANCE.json`](simulator/provenance/RELEASE_PROVENANCE.json).

Slay the Spire 2 and its game content belong to Mega Crit. This is an independent
research emulator and agent release. The included emulator definitions describe
game mechanics; commercial game assemblies, assets, and the decompiled reference
source are not part of this release.

## System.IO.Hashing 9.0.0

The worker directories include the `net9.0` build of Microsoft's
`System.IO.Hashing` NuGet package, version 9.0.0. Its assembly hash matches the
selected evaluation's pinned dependency. The local NuGet package metadata names
the MIT license and the [dotnet/runtime](https://github.com/dotnet/runtime)
repository at commit `9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3`.

The package's notices are preserved verbatim:

- [MIT license](simulator/licenses/System.IO.Hashing/LICENSE.TXT)
- [Package third-party notices](simulator/licenses/System.IO.Hashing/THIRD-PARTY-NOTICES.TXT)
- [Original NuGet package metadata](simulator/licenses/System.IO.Hashing/system.io.hashing.nuspec)

The package third-party notice file is retained as supplied; its broad .NET
component listing does not mean those components are all bundled here.

## Separately installed dependencies

Python, NumPy, PyTorch, and the .NET runtime/SDK are installed separately and are
not redistributed by this release. Their original distributions carry their own
licenses and notices. See the release's environment instructions for the versions
used by the evaluation runtime.

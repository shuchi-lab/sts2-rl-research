# Watch the Battle and Choice agents play Slay the Spire 2

Load both trained agents into the **real Steam game** and watch their actions in
the game window. The Battle agent chooses cards, targets and potions; the Choice
agent handles routes, rewards, shops, rest sites and events. The overlay displays
their action ratings and lets you request one step or enable autoplay.

This is a minimal package of the working native-game bridge. It reuses the
repository's [published models](../results/model_identities.json) and frozen
inference runtime:

| Agent | Included checkpoint | Parameters |
| --- | --- | ---: |
| Choice | Choice90k N1 · `n1-half-blue90-original-mac_a090000` | 344,609 |
| Battle | Wide+deep Battle177.5k · `enemy_wide_deep_act3_ft_e032500` | 929,653 |

The working setup is **Slay the Spire 2 v0.111.0 on macOS arm64**, single-player
Ironclad at Ascension 1 or 2, without run modifiers. The installer also recognizes
Windows and Linux layouts; gameplay on those platforms is unverified. Game
updates can require bridge changes; the installer detects the installation layout,
not the game version.

## Install

Keep the **whole repository** together: this folder reads `../models/`,
`../runtime/` and `../simulator/`. You need your own Steam installation of the
game, Python 3.11+ with the root [requirements](../requirements.txt), and the
[.NET 9 runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
A GPU, Codex account and LLM API key are unnecessary.

From the repository root, create a Python environment if you have not already:

```sh
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python live_autoplay/run.py --check
```

The check loads both real checkpoints, validates the mod's feature contract and
exits without connecting to the game. It briefly starts the bundled worker to
read its schema, then closes it; it does not simulate a fight.
If .NET is outside `PATH`, append `--dotnet /absolute/path/to/dotnet` to the check
and installer commands. The installer saves that location for launches from Steam.

**Close the game**, then preview and install into the normal macOS Steam location:

```sh
python live_autoplay/install.py \
  --game-dir "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2" \
  --dry-run

python live_autoplay/install.py \
  --game-dir "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
```

For a different Steam library, change `--game-dir`. On Windows, use your activated
environment's `python` and pass the game's installation folder with double quotes.
See the [root setup instructions](../README.md#install) for environment activation.

The installer checks the selected Python environment, copies the mod DLL,
manifest and contract, and writes `STS2_MCP.conf` with this clone's launcher path.
Existing versions of those four files are backed up under `.local/backups/`;
other mod files and saved history are preserved. An existing loose
`mods/STS2_MCP.dll` must be removed or moved aside before using the folder layout,
so the game does not load two copies. Keep the clone and Python environment in
place; rerun the installer if you move them.

## Play in the game window

1. Launch the game through Steam and enable the mod when prompted.
2. The overlay starts the Python agents automatically in **Advice** mode. If it
   shows **Open**, click it and wait for **Connected**.
3. Start a fresh single-player Ironclad run at A1 or A2 with no modifiers. Let the
   agents observe the run from the beginning so their decision history is complete.
4. Use **Step** for one decision, or **Auto** to let both agents continue playing.
   **Pause** or **F6** stops further agent actions. Adjust the delay in the overlay
   to make the moves easier to watch.

When the game starts the service, closing the game also shuts down that service
and closes its journal. A service started manually in a terminal stays under your
control; stop it with **Ctrl+C** when finished.

| Control | Behavior |
| --- | --- |
| Advice | Show ratings without playing actions |
| Step | Execute one decision, including required selection confirmation |
| Auto | Continue through supported combat and noncombat decisions |
| Pause / F6 | Stop authorizing further actions; an already accepted action may finish |

Ratings show Q-values and relative preferences among the available actions. A
displayed percentage is **not a probability of winning the game**.
Unsupported states, missing history or uncertain action outcomes pause the agent
with a reason. Taking over strategic choices manually can also require a pause;
resume only when the overlay reports a supported, ready state. Existing saves
require matching history recorded by this bridge.

To run the service in a terminal for troubleshooting:

```sh
python live_autoplay/run.py
```

Use `--endpoint http://127.0.0.1:15527/api/v1/agent` if you installed on another
port. Use one clone's service at a time; that clone refuses a second controller
for the same local bridge. Journals and
machine-specific settings are written under `.local/` and ignored by Git.

## How the two agents connect to the game

```text
Steam game + STS2_MCP mod
    public state and legal actions
              ↓ localhost HTTP
Python controller → frozen Battle or Choice model → action ratings
              ↓ Step / Auto authorization
Mod executes the chosen native action → updated game window
```

The mod converts the visible game state into the original encoders' inputs and
binds the chosen candidate to the real game action. The controller waits for the
action to settle before updating history and scoring the next decision. Research
features and their experimental checkpoints are documented in `../research/`;
the model pair used here is identified above.

```text
live_autoplay/
├── README.md                 Setup, controls and scope
├── run.py                    Start agents or run the offline check
├── install.py                Preview/install the mod for this clone
├── agent_contract.json       Public IDs, feature fields and compatibility rules
├── bin/STS2_MCP.dll           Working native-game mod
├── mod/                      C# bridge and overlay source
├── python/sts2_autoplay/      Frozen inference adapter and action controller
├── tests/                    Controller, installer and recorded-state checks
├── licenses/                 Upstream STS2MCP license
└── VERIFICATION.md           Packaging checks and evidence limits
```

## Development and scope

```sh
python -m pip install -r live_autoplay/requirements-dev.txt
python -m pytest -q -p no:cacheprovider live_autoplay/tests
```

Tests that load the models use the same .NET discovery as the launcher. For a
nonstandard installation, set `RS1_DOTNET` to its executable path when running
pytest. Tests use temporary fixtures and a local mock HTTP server; they do not
play the installed game.

To build the mod yourself, install the **.NET 9 SDK** and use your own game's
assemblies:

```sh
dotnet build live_autoplay/mod/STS2_MCP.csproj -c Release \
  -p:STS2GameDir="/path/to/Slay the Spire 2"
```

This is an experimental native-game deployment, with [offline verification and
limitations](VERIFICATION.md). The repository's **68/256 result is a simulator
benchmark**; it is not a measured win rate for this game integration. Commercial
game binaries, personal saves, training data and gameplay recordings are not
included. The supplied mod binary is retained from the working installation;
the build command writes a separate development binary.

The bridge derives from [STS2MCP](https://github.com/Gennadiyev/STS2MCP), with local
extensions for RL inference, native state projection, action handling and the
overlay. Its [MIT license](licenses/STS2MCP-MIT.txt) and the repository's
[third-party notices](../THIRD_PARTY_NOTICES.md) apply.

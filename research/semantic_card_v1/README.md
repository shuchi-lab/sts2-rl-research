# Semantic card encoding · experimental v1

Represent cards through shared mechanics: damage, Block, draws, status changes,
selection rules, and ordered effects. Card names and IDs locate descriptions;
they are not learned identity inputs in this representation.

This module publishes the representation and runnable inspection examples. The
repository's **v1.0.0 benchmark uses the earlier card-identity model**. Its 26.6%
selected validation result is not a result for this semantic representation.

## Start here

| Read | Purpose |
| --- | --- |
| [Representation](docs/representation.md) | Record shapes, mechanics order, and worked examples |
| [Feature reference](docs/feature-reference.md) | Every field's index, meaning, and encoding |
| [Limitations](docs/limitations.md) | Coverage, missing runtime components, and reserved fields |
| [Saved Bash example](examples/expected-output.json) | Reproducible input and indexed output |

## Run an example

Use Python 3.11+ in a virtual environment. From the repository root:

```sh
python -m pip install -r research/semantic_card_v1/requirements.txt
python research/semantic_card_v1/examples/inspect_card.py --card Bash --cost 2 --damage 8 --hits 1 --in-hand --playable
```

Only NumPy is required. The inspector does not load a checkpoint, PyTorch, .NET,
the simulator, or files outside this repository.

The supplied cost, damage, and hit count are **illustrative current observations**.
The script encodes them; it does not calculate combat outcomes or verify legality.
Omit a numeric argument to leave that value unknown. Printed card amounts still
appear in the mechanics program, separately from these current observations.

Selected output:

```text
 index  feature                         encoded value
     0  current_cost                    1.098612
     1  current_cost_known              1.000000
     4  damage                          2.197225
     5  damage_known                    1.000000
    59  type:Attack                     1.000000
    76  target:AnyEnemy                 1.000000
```

Bash's mechanics program contains damage first, then applying Vulnerable.
The complete JSON output includes indexed features for both steps:

```sh
python research/semantic_card_v1/examples/inspect_card.py --card Bash --cost 2 --damage 8 --hits 1 --in-hand --playable --json
```

More examples:

```sh
# Damage remains unknown unless explicitly supplied.
python research/semantic_card_v1/examples/inspect_card.py --card StrikeIronclad

# Upgrading changes random exhaust into chosen exhaust.
python research/semantic_card_v1/examples/inspect_card.py --card TrueGrit
python research/semantic_card_v1/examples/inspect_card.py --card TrueGrit --upgraded

# Inspect an expression that refers to the player's Block.
python research/semantic_card_v1/examples/inspect_card.py --card BodySlam --json

# Show admitted catalog names; MadScience requires live instance context.
python research/semantic_card_v1/examples/inspect_card.py --list
```

## What is included

| File | Responsibility |
| --- | --- |
| [schema.py](schema.py) | Ordered feature names, vocabulary, and numeric transforms |
| [catalog.py](catalog.py) | Mechanics programs and masked sequence construction |
| [source_metadata.json](source_metadata.json) | Static metadata for the source game build |
| [encoding.py](encoding.py) | Current-card encoding from explicit values |
| [reference.py](reference.py) | Regenerate the indexed feature reference |
| [SOURCE.json](SOURCE.json) | Snapshot hashes and the dependency adaptation |

The snapshot compiles 207 admitted card identities, each with base/upgraded
programs. This is compiler coverage, not a new gameplay accuracy or win-rate
measurement. See [limitations](docs/limitations.md).

## Verify and maintain

```sh
python -m unittest discover -s research/semantic_card_v1/tests -v
python -m research.semantic_card_v1.reference --write
```

Tests check masks and indices, effect order, behavior-changing upgrades,
identity-independent mechanics, isolated execution, and generated-file consistency.

The schema identifier is `semantic_card.schema/v1`. Its version is independent
of the benchmark release version. Changing field order or meaning requires a
new schema contract; existing checkpoints and encoded data are not automatically
compatible.

Original project contributions use the repository's [MIT license](../../LICENSE).
See [third-party notices](../../THIRD_PARTY_NOTICES.md) for attribution. Game names
and mechanics refer to Slay the Spire 2; no game assets are included in this module.

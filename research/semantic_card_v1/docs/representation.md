# Card representation

The representation combines **what a card does** with **what is currently known
about its instance**. It uses shared mechanics categories instead of a learned
embedding indexed by card identity.

## Records and shapes

| Record | Shape for one item | Purpose |
| --- | --- | --- |
| Current card | `[84]` | Current numeric values, availability, flags, type, rarity, target |
| Effect steps | `[S, 278]` | An ordered program of S mechanics steps |
| Expression terms | `[S, T, 83]` | Up to T expression terms per effect step |
| Candidate previews | `[P, 15]` | Public previews for a candidate action |
| Selection purpose | `[31]` | Meaning and quantities of an open selection |
| Continuation frames | `[F, 20]` | Suspended resolution and ordered selected/snapshot cards |

These are separate records, not one concatenated feature vector. `S`, `T`, `P`,
and `F` vary with the card and state. The minimal inspector emits the first
three records. The remaining layouts document the surrounding live observation
contract, whose adapter is not bundled here.

`Catalog.descriptor_arrays()` returns `semantic_steps`, `semantic_step_mask`,
`semantic_terms`, and `semantic_term_mask`. For a batch of J descriptions the
shapes are `[J,S,278]`, `[J,S]`, `[J,S,T,83]`, and `[J,S,T]`. Boolean masks identify
real rows. Padding must be ignored; do not interpret a zero-padded row as a real
effect. Descriptor zero is an empty padding description.

## The current-card vector

All ranges are inclusive and zero-based.

| Indices | Group |
| --- | --- |
| 0–33 | 17 numeric values, each followed by a known flag |
| 34–58 | 25 boolean flags |
| 59–64 | Six shared card-type categories |
| 65–74 | Ten shared rarity categories |
| 75–83 | Nine shared target categories |

Numeric values use `slog(x) = sign(x) × ln(1 + abs(x))`. An unavailable value
has value `0` and known flag `0`; a known zero has value `0` and known flag `1`.
This distinguishes a free card from a card whose payable cost is unavailable.

For an illustrative Strike observation with cost 1, damage 6, and one hit:

```sh
python research/semantic_card_v1/examples/inspect_card.py --card StrikeIronclad --cost 1 --damage 6 --hits 1 --in-hand --playable
```

| Index | Field | Raw input | Encoded value |
| ---: | --- | ---: | ---: |
| 0 | current_cost | 1 | 0.693147 |
| 1 | current_cost_known | available | 1 |
| 4 | damage | 6 | 1.945910 |
| 5 | damage_known | available | 1 |
| 8 | hits | 1 | 0.693147 |
| 9 | hits_known | available | 1 |
| 35 | in_hand | true | 1 |
| 59 | type:Attack | Attack | 1 |
| 65 | rarity:Basic | Basic | 1 |
| 76 | target:AnyEnemy | AnyEnemy | 1 |

The example does not infer these current quantities from the printed description.
If the caller omits `--damage`, damage and damage_known are both zero even though
the static mechanics program contains a damage amount. Strength, Weak, target
modifiers, and prior effects can make effective values differ from printed values.

The standalone helper accepts already-observed values:

```python
from research.semantic_card_v1.encoding import encode_current_card

row = encode_current_card(
    card_type="Attack", rarity="Basic", target="AnyEnemy",
    numeric={"current_cost": 1, "damage": 6, "hits": 1},
    flags={"in_hand": True, "playable": True},
)
```

Unknown numeric names, nonfinite values, invalid flags, and unknown category
values raise an error. This helper performs encoding, not game-state validation.

## Ordered mechanics: Bash

The base Bash program contains two steps:

```json
[
  {"op": "damage", "amount": 8, "target": "enemy", "count": 1, "powered": true},
  {"op": "status", "amount": 2, "target": "enemy", "property": "vulnerable", "operator": "add", "scope": "turn"}
]
```

Each step is encoded separately into 278 fields. The ordering expresses that
damage precedes applying Vulnerable. Swapping these steps changes the mechanics,
so the description is not an unordered bag of keywords. Bash+ changes the
magnitudes to damage 10 and Vulnerable 3.

Categories such as `op:damage` and `target:enemy` are shared across cards. Numeric
magnitudes use the same slog transform and paired availability flags. Unspecified
categorical groups have their `none` field set, not an all-zero group.

## Behavior-changing upgrades: True Grit

Both variants gain Block and then exhaust a card from hand. The base variant's
exhaust step uses `selection:random`; the upgraded variant uses `selection:chosen`.
The program changes along with the numbers. An upgrade flag alone would not
explain that difference.

## Expressions: Body Slam

Some magnitudes depend on state. Body Slam's compiled program includes an
expression using the shared `ref:block` reference rather than inventing a fixed
current damage amount. Expressions use an ordered prefix sequence: an operator
with its arity precedes its argument terms. Literal values, state references,
coefficients, and snapshot/live flags have separate fields.

The inspector shows the symbolic terms and their encodings. It does not evaluate
the formula against a battle. Formula meaning comes from the whole ordered
program; the term's `known` flag is not the sequence-padding mask.

## From inputs to a learned embedding

The development architecture encodes expression terms, combines them with
ordered effect steps, and combines that description with current-card features.
It can produce a learned card vector of width 64, 128, or 256. Those learned
coordinates do not have fixed human-assigned meanings such as damage or cost.

The module publishes the named inputs. It does not include that trained neural
encoder or claim that these examples reproduce a trained policy's embeddings.
Card IDs and names are metadata for catalog lookup. Descriptor indices also only
locate programs; they must not become learned card-identity embeddings.

See the [complete feature reference](feature-reference.md) for every position,
including the preview, selection, and continuation records.

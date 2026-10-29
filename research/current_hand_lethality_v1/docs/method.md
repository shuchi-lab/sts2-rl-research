# Method and limitations

## Masking and added information

Both experiment arms set enemy `max_hp` and `hp_fraction` to zero after the base
adapter encodes them. Current HP, block, and other observations remain available.
Masking the fraction as well prevents direct reconstruction of maximum HP from
current HP and that fraction. This removes two direct inputs; it is not a claim
that all information correlated with maximum HP has disappeared. The simulator
still retains the true state for mechanics.

The feature arm adds two fields per enemy and four per legal action. The
baseline has the same enlarged dimensions and initial parameters, but its added
columns contain zeros. Therefore this pair tests adding lethality information
**under a common HP mask**. It does not isolate the effect of HP masking itself.

## Native current-hand search

The original judge runs the frozen native engine on private, sanitized state
copies. It explores legal plays of cards already in the current hand, including
setup skills such as Bash or Defend. Engine execution accounts for energy,
costs, block, Weak, Vulnerable, Strength, and ordered card effects.

| Limit | Value |
| --- | ---: |
| Total attempted simulated edges per observation | 128 |
| Edges per first action, including its immediate edge | 32 |
| Maximum card plays along a path | 8 |

The first sweep prioritizes immediate checks across root card actions before
continuations spend the remaining budget. If the total budget cannot cover an
action, its result remains unknown. Continuations are explored in rounds across
first actions. Reaching a budget/depth limit with work remaining sets
completeness false.

A kill witness requires the native engine to remove a dead, non-escaped enemy
and the player to remain alive at that killing prefix. Reducing an enemy to zero
HP when it revives or reattaches is insufficient. Killing one enemy does not
certify defeating all enemies, surviving their next turn, or maximizing reward.
The policy receives flags, not a sequence it must execute. The judge recomputes
them after each real decision.

## Unsupported branches are unknown

Before branching, the judge replaces hidden RNG state, sanitizes hidden enemy
move-machine fields, and canonicalizes unknown draw order. It rejects an entire
simulated edge if it consumes randomness, changes the draw pile, draws or
generates a card, changes the remaining hand beyond removing the played card,
or automatically plays extra cards. It also rejects edges that cross the turn,
leave a pending selection/play/effect, introduce or transform enemies, remove a
living enemy, or encounter an unhandled terminal result.

Such an excluded edge cannot certify even an apparently obvious immediate kill.
For example, a damage-plus-draw card may produce unknown output. Native removal
of an actually killed enemy is allowed and mapped back to its original row.
Simulation failures on hypothetical edges also mark them unknown.

Consequently, an incomplete negative is not a general impossibility proof.
Positive witnesses remain useful even when other branches are incomplete.
The extra computation is part of both training and inference: observed gains
belong to the policy **plus computed search inputs**.

## Public Python interface

```python
from research.current_hand_lethality_v1.encoding import encode, feature_arrays

# raw has enemies, legal, and the native current_hand_lethality block.
extra_enemy, extra_action = feature_arrays(raw)

# Base rows are already encoded in the frozen field order and transforms.
# Shapes: base_enemy [N, 30], base_action [A, 6].
enemy, action = encode(raw, base_enemy, base_action, profile="masked_lethality")
# Returns float32 [N, 32], [A, 10], without mutating any input.

baseline_enemy, baseline_action = encode(
    raw, base_enemy, base_action, profile="masked_baseline"
)
```

`feature_arrays(..., enabled=False)` and the baseline profile do not require a
native judge block. The enabled profile rejects a missing/wrong schema,
misaligned enemy rows, duplicate action ordinals, mismatched hand/target values,
or non-boolean flags. Base array shapes and finiteness are checked. Passing an
already enlarged tensor is an error, preventing accidental double-appending.

This small module does not reconstruct the entire combat observation from raw
game data. It does not provide a live environment adapter, native judge binary,
trained policy, or training runner. Matching shapes alone do not make a different
base feature layout compatible. Source identities are in [SOURCE.json](../SOURCE.json).

## Recorded example and verification boundary

The example selects case `b168-ordering_lethal-p0` from the retained native
analysis. Its enemy has 15 HP, and both card orders were executed natively.
The fixture stores the unchanged judge block, legal options, relevant enemy
state, setup, and resulting HP/status for both paths. Its two dense base inputs
are reconstructed from the frozen base-adapter formulas for this exact state.
No learned policy generated the example.

The local Python tests verify the packaged encoding and recorded-example
consistency. They do not rerun the native search or reproduce the training
results. The retained development handoff reports 23 native checks covering
ordering, energy, block, unknown branches, target identity, survival, hidden-state
invariance, and state/RNG purity. Those native tests are not bundled here.

The experiment package is a small research snapshot. Full native reproduction
would additionally require the matching worker, engine dependencies, observation
adapter, checkpoints, and evaluation cases; the released benchmark runtime is
not advertised as an interchangeable substitute.

# Feature reference

All positions are **zero-based within their own dense tensor row**. The frozen
base layout is `crl.features/v3`; the added native block uses
`current-hand-lethality/v1`. Names and order are defined in [schema.py](../schema.py).

`slog(x) = sign(x) × ln(1 + abs(x))`. The public encoder consumes base fields
**after** their original transforms. It does not re-encode HP, costs, or other
base values. Every added flag is raw `float32` 0 or 1, without `slog`.

## Enemy rows: 32 fields

The first 30 positions retain the source layout. Positions 1 and 2 are zero in
both profiles; their original names remain in the schema so later fields do not
shift. The masking changes model inputs, not the simulator's HP values.

| Position | Field | Model value / meaning |
| ---: | --- | --- |
| 0 | `hp` | `slog(current HP)`; retained |
| 1 | `max_hp` | **Masked to 0**; originally `slog(maximum HP)` |
| 2 | `hp_fraction` | **Masked to 0**; originally `hp / max(max_hp, 1)` |
| 3 | `block` | `slog(current block)` |
| 4 | `targetable` | Whether the native view permits targeting this enemy |
| 5 | `alive` | Whether current HP is greater than zero |
| 6 | `is_hex_applier` | This row is the current Hex source |
| 7 | `is_shrink_applier` | This row is the current Shrink source |
| 8 | `slot_position` | Row index divided by `max(number of enemies - 1, 1)` |
| 9 | `n_intents` | Raw count of declared intents |
| 10 | `curl_up_pending` | Native pending Curl Up flag |
| 11 | `possessed_player_strength` | `slog(Strength held by this enemy)` |
| 12 | `possessed_player_dexterity` | `slog(Dexterity held by this enemy)` |
| 13 | `steam_eruption_about_to_blow` | Steam Eruption phase equals 1 |
| 14 | `steam_eruption_exploding` | Steam Eruption phase equals 2 |
| 15 | `steam_eruption_damage` | `slog(native Steam Eruption damage)` |
| 16 | `nemesis_intangible_on` | Native Nemesis Intangible flag |
| 17 | `at_player_back` | Native enemy-at-player-back flag |
| 18 | `is_dampen_caster` | Native Dampen-source flag |
| 19 | `stolen_gold` | `slog(gold held by this enemy)` |
| 20 | `has_stolen_card` | Whether this enemy holds a stolen card |
| 21 | `escaped` | Native escaped flag |
| 22 | `off_balance` | Native Off Balance flag |
| 23 | `powered_hits_this_turn` | `slog(native powered-hit count)` |
| 24 | `has_slot` | Whether native slot is nonnegative |
| 25 | `slot` | `max(native slot, 0) / 5` |
| 26 | `strangle_before_play` | `slog(native pre-play Strangle value)` |
| 27 | `has_secondary_intent` | Native secondary-intent flag |
| 28 | `is_parked_play_target` | Target of a parked card play |
| 29 | `is_parked_autoplay_target` | Target of a parked automatic play |
| **30** | **`lethal_sequence_found`** | An accepted path kills this enemy while the player is alive at that prefix |
| **31** | **`lethal_search_complete`** | All first-action searches completed within the supported scope |

The two added fields are zeroed for rows with HP ≤ 0. The current native judge's
enemy completeness value is shared across enemies: it requires all root card
searches to be complete. It is not a separate exhaustive proof for each enemy.

## Legal-action rows: 10 fields

There is one row per current legal option, including non-card options. These
positions belong to the action feature tensor, not to the card feature tensor or
the model's entire concatenated action-scoring input.

| Position | Field | Meaning |
| ---: | --- | --- |
| 0 | `auto_target` | Native engine auto-target fallback is recorded on this option |
| 1 | `has_target` | Option has an explicit enemy target index ≥ 0 |
| 2 | `sel_hand` | Selection candidate comes from the hand |
| 3 | `sel_discard` | Selection candidate comes from the discard pile |
| 4 | `sel_draw` | Selection candidate comes from the draw pile |
| 5 | `sel_generated` | Selection candidate is from generated choices |
| **6** | **`immediate_kill`** | This card action itself certifies a surviving kill of the relevant enemy |
| **7** | **`lethal_starting_here`** | A surviving kill path begins with this exact card/target action; includes immediate kills |
| **8** | **`immediate_known`** | The judge accepted the immediate simulated edge |
| **9** | **`continuation_complete`** | Search rooted at this first action completed within the supported scope |

## Native output → model flags

The native JSON stores `enemies[i].Found/Complete` and action results containing
`Option`, `Hand`, `Target`, `Immediate[]`, `Found[]`, `ImmediateKnown`, and
`Complete`. `Option` refers to the current legal-option ordinal, not a permanent
card ID. The encoder verifies the corresponding hand index and target.

- **Target ≥ 0:** take `Immediate[target]` and `Found[target]`. A path that only
  kills a different enemy does not set this target's flag.
- **Target = -1:** use `any(...)` across enemies currently above zero HP. This
  supports untargeted setup skills and area effects. It means *any one* living
  enemy, not necessarily all enemies or the most dangerous enemy.
- `ImmediateKnown` and `Complete` are action-level values; they are not
  aggregated per target.
- Native branches preserve original enemy identities when a dead enemy is
  removed and the remaining enemy list shifts.
- Missing action-result rows stay all zero. Non-card actions such as `end_turn`,
  potion use, and selections have no lethality result in this scope.

## Interpret found and complete together

| Found | Complete | Interpretation |
| ---: | ---: | --- |
| 1 | 1 | A kill witness exists; the supported search completed |
| 1 | 0 | A kill witness exists; other branches remain unresolved |
| 0 | 1 | No surviving kill found after completing the supported search |
| 0 | 0 | Unknown; do not interpret as impossibility |

Similarly, `immediate_kill=0, immediate_known=0` is unknown, whereas
`immediate_kill=0, immediate_known=1` records an accepted non-killing immediate
result. Baseline zero placeholders and non-card zero rows are not negative
search conclusions. Consult [method.md](method.md) for the scope of completeness.

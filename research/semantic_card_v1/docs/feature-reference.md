# Feature reference

Schema: `semantic_card.schema/v1`. All indices below are **zero-based** and local to their record.

Generated from [schema.py](../schema.py) and the reviewed descriptions in [reference.py](../reference.py).
Regenerate from the repository root with `python -m research.semantic_card_v1.reference --write`.

## Encoding conventions

- `slog(x) = sign(x) × ln(1 + abs(x))`, stored as float32. This is not clipping or standardization.
- `slog + known`: the value and its following availability flag are distinct fields. Known zero is `(0, 1)`; unavailable is `(0, 0)`.
- `one-hot`: one field per shared category value. Effect categories default to `none`. These are mechanics categories, not card identity encodings.
- `0/1`: a boolean indicator; omitted example flags default to zero and have no separate unknown channel.
- `raw`: numeric value copied directly as float32; no log transform.
- All 20 continuation fields use slog, including flags: an asserted flag is `ln(2)`, not `1`.
- An effect or expression sequence uses separate boolean padding masks. A padded all-zero row is not a valid `none` step.
- The inspector emits current-card, effect-step, and expression-term records. Preview, selection, and continuation tables document the surrounding live schema; their adapter is outside this module.

The six records are separate arrays. They are not concatenated into one 511-dimensional card vector.

## Current card — 84 fields

| Index | Field | Encoding | Meaning |
| ---: | --- | --- | --- |
| 0 | `current_cost` | slog + known | Current payable energy cost supplied by a public preview. |
| 1 | `current_cost_known` | 0/1 | Whether `current_cost` is available; one for a known zero, zero for unavailable. |
| 2 | `current_x` | slog + known | Current X value; the live adapter uses current_cost for an X-cost card. |
| 3 | `current_x_known` | 0/1 | Whether `current_x` is available; one for a known zero, zero for unavailable. |
| 4 | `damage` | slog + known | Current effective damage from a preview; hit count and target scope are separate. |
| 5 | `damage_known` | 0/1 | Whether `damage` is available; one for a known zero, zero for unavailable. |
| 6 | `block` | slog + known | Current effective Block from a preview. |
| 7 | `block_known` | 0/1 | Whether `block` is available; one for a known zero, zero for unavailable. |
| 8 | `hits` | slog + known | Current number of hits from a preview. |
| 9 | `hits_known` | 0/1 | Whether `hits` is available; one for a known zero, zero for unavailable. |
| 10 | `play_count` | slog + known | Current number of plays/replays from a preview. |
| 11 | `play_count_known` | 0/1 | Whether `play_count` is available; one for a known zero, zero for unavailable. |
| 12 | `upgrade_level` | slog + known | Upgrade indicator exported by the card instance; the inspector uses 0 or 1. |
| 13 | `upgrade_level_known` | 0/1 | Whether `upgrade_level` is available; one for a known zero, zero for unavailable. |
| 14 | `bonus_damage` | slog + known | Instance BonusDamage modifier, before interpreting effective damage. |
| 15 | `bonus_damage_known` | 0/1 | Whether `bonus_damage` is available; one for a known zero, zero for unavailable. |
| 16 | `bonus_block` | slog + known | Instance BonusBlock modifier, before interpreting effective Block. |
| 17 | `bonus_block_known` | 0/1 | Whether `bonus_block` is available; one for a known zero, zero for unavailable. |
| 18 | `growth` | slog + known | Instance MomentumDamage growth counter. |
| 19 | `growth_known` | 0/1 | Whether `growth` is available; one for a known zero, zero for unavailable. |
| 20 | `enchant_amount` | slog + known | Instance enchantment amount/counter. |
| 21 | `enchant_amount_known` | 0/1 | Whether `enchant_amount` is available; one for a known zero, zero for unavailable. |
| 22 | `cost_bump` | slog + known | Instance CostBump modifier. |
| 23 | `cost_bump_known` | 0/1 | Whether `cost_bump` is available; one for a known zero, zero for unavailable. |
| 24 | `cost_for_combat` | slog + known | Combat-wide cost override; unknown when the engine sentinel is unset. |
| 25 | `cost_for_combat_known` | 0/1 | Whether `cost_for_combat` is available; one for a known zero, zero for unavailable. |
| 26 | `cost_this_turn` | slog + known | This-turn cost override; unknown when the engine sentinel is unset. |
| 27 | `cost_this_turn_known` | 0/1 | Whether `cost_this_turn` is available; one for a known zero, zero for unavailable. |
| 28 | `cost_restore` | slog + known | CostOverrideBaseline while a temporary override exists; otherwise unknown. |
| 29 | `cost_restore_known` | 0/1 | Whether `cost_restore` is available; one for a known zero, zero for unavailable. |
| 30 | `known_top_position` | slog + known | One-based position in the publicly known top of the draw pile. |
| 31 | `known_top_position_known` | 0/1 | Whether `known_top_position` is available; one for a known zero, zero for unavailable. |
| 32 | `known_bottom_position` | slog + known | One-based distance from the publicly known bottom of the draw pile. |
| 33 | `known_bottom_position_known` | 0/1 | Whether `known_bottom_position` is available; one for a known zero, zero for unavailable. |
| 34 | `playable` | 0/1 | Public preview says playable; in the inspector this requires --playable. |
| 35 | `in_hand` | 0/1 | Card belongs to the hand; in the inspector this requires --in-hand. |
| 36 | `cost_x` | 0/1 | Card definition has an X energy cost. |
| 37 | `unplayable` | 0/1 | Unplayable keyword, distinct from temporary action illegality. |
| 38 | `exhaust` | 0/1 | Current Exhaust keyword. |
| 39 | `ethereal` | 0/1 | Current Ethereal keyword, including combat modifiers. |
| 40 | `retain` | 0/1 | Current Retain keyword/flag, including turn and combat modifiers. |
| 41 | `innate` | 0/1 | Current Innate keyword. |
| 42 | `sly` | 0/1 | Current Sly keyword/flag. |
| 43 | `eternal` | 0/1 | Current Eternal keyword. |
| 44 | `strike_tag` | 0/1 | Card has the shared Strike tag; not a card identity one-hot. |
| 45 | `defend_tag` | 0/1 | Card has the shared Defend tag; not a card identity one-hot. |
| 46 | `can_upgrade` | 0/1 | Definition permits upgrading and this variant is not already upgraded. |
| 47 | `free_this_turn` | 0/1 | Instance FreeThisTurn flag. |
| 48 | `free_until_played` | 0/1 | Instance FreeUntilPlayed flag. |
| 49 | `retained` | 0/1 | Reserved slot: the source live adapter does not populate it; zero in its output. |
| 50 | `played_this_turn` | 0/1 | Instance PlayedThisTurn flag. |
| 51 | `enchant_spent` | 0/1 | Instance enchantment-spent flag. |
| 52 | `dampened` | 0/1 | Instance DampenedUpgrade flag. |
| 53 | `bound` | 0/1 | Instance Bound flag. |
| 54 | `pending` | 0/1 | Card is in a suspended/pending play group. |
| 55 | `enemy_held` | 0/1 | Card is held by an enemy. |
| 56 | `offer` | 0/1 | Node was created as a selection/offer candidate outside a matched physical pile row. |
| 57 | `source_snapshot` | 0/1 | Node represents a selection source or an ordered chosen/snapshot card. |
| 58 | `temporary_copy` | 0/1 | Reserved slot: the source live adapter does not populate it; zero in its output. |
| 59 | `type:Attack` | one-hot | Card type is Attack. |
| 60 | `type:Skill` | one-hot | Card type is Skill. |
| 61 | `type:Power` | one-hot | Card type is Power. |
| 62 | `type:Status` | one-hot | Card type is Status. |
| 63 | `type:Curse` | one-hot | Card type is Curse. |
| 64 | `type:Quest` | one-hot | Card type is Quest. |
| 65 | `rarity:Basic` | one-hot | Card rarity is Basic. |
| 66 | `rarity:Common` | one-hot | Card rarity is Common. |
| 67 | `rarity:Uncommon` | one-hot | Card rarity is Uncommon. |
| 68 | `rarity:Rare` | one-hot | Card rarity is Rare. |
| 69 | `rarity:Ancient` | one-hot | Card rarity is Ancient. |
| 70 | `rarity:Token` | one-hot | Card rarity is Token. |
| 71 | `rarity:Status` | one-hot | Card rarity is Status. |
| 72 | `rarity:Curse` | one-hot | Card rarity is Curse. |
| 73 | `rarity:Event` | one-hot | Card rarity is Event. |
| 74 | `rarity:Quest` | one-hot | Card rarity is Quest. |
| 75 | `target:Self` | one-hot | Card target is Self. |
| 76 | `target:AnyEnemy` | one-hot | Card target is AnyEnemy. |
| 77 | `target:AllEnemies` | one-hot | Card target is AllEnemies. |
| 78 | `target:RandomEnemy` | one-hot | Card target is RandomEnemy. |
| 79 | `target:None` | one-hot | Card target is None. |
| 80 | `target:AnyPlayer` | one-hot | Card target is AnyPlayer. |
| 81 | `target:AnyAlly` | one-hot | Card target is AnyAlly. |
| 82 | `target:AnyCreature` | one-hot | Card target is AnyCreature. |
| 83 | `target:AllPlayers` | one-hot | Card target is AllPlayers. |

## Effect step — 278 fields

| Index | Field | Encoding | Meaning |
| ---: | --- | --- | --- |
| 0 | `op:none` | one-hot | Effect operation unspecified or not applicable in this step. |
| 1 | `op:damage` | one-hot | Effect operation: damage. |
| 2 | `op:block` | one-hot | Effect operation: block. |
| 3 | `op:draw` | one-hot | Effect operation: draw. |
| 4 | `op:energy` | one-hot | Effect operation: energy. |
| 5 | `op:max_energy` | one-hot | Effect operation: max energy. |
| 6 | `op:lose_hp` | one-hot | Effect operation: lose hp. |
| 7 | `op:heal` | one-hot | Effect operation: heal. |
| 8 | `op:max_hp` | one-hot | Effect operation: max hp. |
| 9 | `op:gold` | one-hot | Effect operation: gold. |
| 10 | `op:potion` | one-hot | Effect operation: potion. |
| 11 | `op:status` | one-hot | Effect operation: status. |
| 12 | `op:move` | one-hot | Effect operation: move. |
| 13 | `op:exhaust` | one-hot | Effect operation: exhaust. |
| 14 | `op:generate` | one-hot | Effect operation: generate. |
| 15 | `op:clone` | one-hot | Effect operation: clone. |
| 16 | `op:transform` | one-hot | Effect operation: transform. |
| 17 | `op:upgrade` | one-hot | Effect operation: upgrade. |
| 18 | `op:modify` | one-hot | Effect operation: modify. |
| 19 | `op:autoplay` | one-hot | Effect operation: autoplay. |
| 20 | `op:replay` | one-hot | Effect operation: replay. |
| 21 | `op:rule_begin` | one-hot | Effect operation: rule begin. |
| 22 | `op:rule_end` | one-hot | Effect operation: rule end. |
| 23 | `op:group_begin` | one-hot | Effect operation: group begin. |
| 24 | `op:group_end` | one-hot | Effect operation: group end. |
| 25 | `op:condition_begin` | one-hot | Effect operation: condition begin. |
| 26 | `op:condition_end` | one-hot | Effect operation: condition end. |
| 27 | `op:kill` | one-hot | Effect operation: kill. |
| 28 | `op:remove_rule` | one-hot | Effect operation: remove rule. |
| 29 | `target:none` | one-hot | Effect recipient unspecified or not applicable in this step. |
| 30 | `target:self` | one-hot | Effect recipient: self. |
| 31 | `target:enemy` | one-hot | Effect recipient: enemy. |
| 32 | `target:all_enemies` | one-hot | Effect recipient: all enemies. |
| 33 | `target:random_enemy` | one-hot | Effect recipient: random enemy. |
| 34 | `target:all_creatures` | one-hot | Effect recipient: all creatures. |
| 35 | `target:source_card` | one-hot | Effect recipient: source card. |
| 36 | `target:event_card` | one-hot | Effect recipient: event card. |
| 37 | `target:chosen_card` | one-hot | Effect recipient: chosen card. |
| 38 | `target:all_matching_cards` | one-hot | Effect recipient: all matching cards. |
| 39 | `target:random_card` | one-hot | Effect recipient: random card. |
| 40 | `target:top_card` | one-hot | Effect recipient: top card. |
| 41 | `target:last_card` | one-hot | Effect recipient: last card. |
| 42 | `target:target_owner` | one-hot | Effect recipient: target owner. |
| 43 | `event:none` | one-hot | Trigger event unspecified or not applicable in this step. |
| 44 | `event:play` | one-hot | Trigger event: play. |
| 45 | `event:turn_start` | one-hot | Trigger event: turn start. |
| 46 | `event:turn_end` | one-hot | Trigger event: turn end. |
| 47 | `event:draw` | one-hot | Trigger event: draw. |
| 48 | `event:card_play` | one-hot | Trigger event: card play. |
| 49 | `event:attack_play` | one-hot | Trigger event: attack play. |
| 50 | `event:skill_play` | one-hot | Trigger event: skill play. |
| 51 | `event:power_play` | one-hot | Trigger event: power play. |
| 52 | `event:exhaust` | one-hot | Trigger event: exhaust. |
| 53 | `event:self_exhaust` | one-hot | Trigger event: self exhaust. |
| 54 | `event:block_gain` | one-hot | Trigger event: block gain. |
| 55 | `event:hp_loss` | one-hot | Trigger event: hp loss. |
| 56 | `event:unblocked_damage` | one-hot | Trigger event: unblocked damage. |
| 57 | `event:incoming_attack` | one-hot | Trigger event: incoming attack. |
| 58 | `event:vulnerable_apply` | one-hot | Trigger event: vulnerable apply. |
| 59 | `event:shuffle` | one-hot | Trigger event: shuffle. |
| 60 | `event:kill` | one-hot | Trigger event: kill. |
| 61 | `event:retain` | one-hot | Trigger event: retain. |
| 62 | `event:discard` | one-hot | Trigger event: discard. |
| 63 | `event:combat_start` | one-hot | Trigger event: combat start. |
| 64 | `event:card_finished` | one-hot | Trigger event: card finished. |
| 65 | `event:self_draw` | one-hot | Trigger event: self draw. |
| 66 | `event:self_discard` | one-hot | Trigger event: self discard. |
| 67 | `event:attack_finished` | one-hot | Trigger event: attack finished. |
| 68 | `phase:none` | one-hot | Execution timing unspecified or not applicable in this step. |
| 69 | `phase:immediate` | one-hot | Execution timing: immediate. |
| 70 | `phase:before` | one-hot | Execution timing: before. |
| 71 | `phase:after` | one-hot | Execution timing: after. |
| 72 | `phase:before_draw` | one-hot | Execution timing: before draw. |
| 73 | `phase:after_draw` | one-hot | Execution timing: after draw. |
| 74 | `phase:card_completion` | one-hot | Execution timing: card completion. |
| 75 | `phase:end_turn_flush` | one-hot | Execution timing: end turn flush. |
| 76 | `phase:next_turn_start` | one-hot | Execution timing: next turn start. |
| 77 | `phase:next_turn_end` | one-hot | Execution timing: next turn end. |
| 78 | `phase:combat_end` | one-hot | Execution timing: combat end. |
| 79 | `phase:after_selection` | one-hot | Execution timing: after selection. |
| 80 | `phase:before_damage` | one-hot | Execution timing: before damage. |
| 81 | `phase:after_damage` | one-hot | Execution timing: after damage. |
| 82 | `property:none` | one-hot | Property being modified unspecified or not applicable in this step. |
| 83 | `property:strength` | one-hot | Property being modified: strength. |
| 84 | `property:dexterity` | one-hot | Property being modified: dexterity. |
| 85 | `property:vulnerable` | one-hot | Property being modified: vulnerable. |
| 86 | `property:weak` | one-hot | Property being modified: weak. |
| 87 | `property:frail` | one-hot | Property being modified: frail. |
| 88 | `property:damage` | one-hot | Property being modified: damage. |
| 89 | `property:block` | one-hot | Property being modified: block. |
| 90 | `property:cost` | one-hot | Property being modified: cost. |
| 91 | `property:max_energy` | one-hot | Property being modified: max energy. |
| 92 | `property:draw_permission` | one-hot | Property being modified: draw permission. |
| 93 | `property:block_permission` | one-hot | Property being modified: block permission. |
| 94 | `property:block_retention` | one-hot | Property being modified: block retention. |
| 95 | `property:hand_retention` | one-hot | Property being modified: hand retention. |
| 96 | `property:play_count` | one-hot | Property being modified: play count. |
| 97 | `property:destination` | one-hot | Property being modified: destination. |
| 98 | `property:max_hp` | one-hot | Property being modified: max hp. |
| 99 | `property:damage_received` | one-hot | Property being modified: damage received. |
| 100 | `property:cost_restore` | one-hot | Property being modified: cost restore. |
| 101 | `property:exhaust_permission` | one-hot | Property being modified: exhaust permission. |
| 102 | `property:upgrade` | one-hot | Property being modified: upgrade. |
| 103 | `property:ethereal` | one-hot | Property being modified: ethereal. |
| 104 | `property:retain` | one-hot | Property being modified: retain. |
| 105 | `property:innate` | one-hot | Property being modified: innate. |
| 106 | `property:unplayable` | one-hot | Property being modified: unplayable. |
| 107 | `property:intangible` | one-hot | Property being modified: intangible. |
| 108 | `property:artifact` | one-hot | Property being modified: artifact. |
| 109 | `property:plating` | one-hot | Property being modified: plating. |
| 110 | `property:thorns` | one-hot | Property being modified: thorns. |
| 111 | `property:damage_multiplier` | one-hot | Property being modified: damage multiplier. |
| 112 | `property:vulnerable_multiplier` | one-hot | Property being modified: vulnerable multiplier. |
| 113 | `property:escape_progress` | one-hot | Property being modified: escape progress. |
| 114 | `property:stunned` | one-hot | Property being modified: stunned. |
| 115 | `operator:none` | one-hot | Update operator unspecified or not applicable in this step. |
| 116 | `operator:add` | one-hot | Update operator: add. |
| 117 | `operator:subtract` | one-hot | Update operator: subtract. |
| 118 | `operator:multiply` | one-hot | Update operator: multiply. |
| 119 | `operator:set` | one-hot | Update operator: set. |
| 120 | `operator:prevent` | one-hot | Update operator: prevent. |
| 121 | `operator:preserve` | one-hot | Update operator: preserve. |
| 122 | `operator:remove` | one-hot | Update operator: remove. |
| 123 | `operator:double` | one-hot | Update operator: double. |
| 124 | `operator:reset` | one-hot | Update operator: reset. |
| 125 | `operator:clamp` | one-hot | Update operator: clamp. |
| 126 | `scope:none` | one-hot | Lifetime or application scope unspecified or not applicable in this step. |
| 127 | `scope:event` | one-hot | Lifetime or application scope: event. |
| 128 | `scope:hit` | one-hot | Lifetime or application scope: hit. |
| 129 | `scope:card` | one-hot | Lifetime or application scope: card. |
| 130 | `scope:turn` | one-hot | Lifetime or application scope: turn. |
| 131 | `scope:combat` | one-hot | Lifetime or application scope: combat. |
| 132 | `scope:permanent` | one-hot | Lifetime or application scope: permanent. |
| 133 | `scope:next_card` | one-hot | Lifetime or application scope: next card. |
| 134 | `scope:next_attack` | one-hot | Lifetime or application scope: next attack. |
| 135 | `scope:next_skill` | one-hot | Lifetime or application scope: next skill. |
| 136 | `scope:next_turn` | one-hot | Lifetime or application scope: next turn. |
| 137 | `scope:until_played` | one-hot | Lifetime or application scope: until played. |
| 138 | `scope:until_exhausted` | one-hot | Lifetime or application scope: until exhausted. |
| 139 | `scope:per_matching_card` | one-hot | Lifetime or application scope: per matching card. |
| 140 | `selection:none` | one-hot | Selection method unspecified or not applicable in this step. |
| 141 | `selection:chosen` | one-hot | Selection method: chosen. |
| 142 | `selection:random` | one-hot | Selection method: random. |
| 143 | `selection:all` | one-hot | Selection method: all. |
| 144 | `selection:top` | one-hot | Selection method: top. |
| 145 | `selection:bottom` | one-hot | Selection method: bottom. |
| 146 | `selection:last` | one-hot | Selection method: last. |
| 147 | `selection:choose_from_random` | one-hot | Selection method: choose from random. |
| 148 | `filter:none` | one-hot | Eligibility filter unspecified or not applicable in this step. |
| 149 | `filter:any` | one-hot | Eligibility filter: any. |
| 150 | `filter:attack` | one-hot | Eligibility filter: attack. |
| 151 | `filter:skill` | one-hot | Eligibility filter: skill. |
| 152 | `filter:power` | one-hot | Eligibility filter: power. |
| 153 | `filter:non_attack` | one-hot | Eligibility filter: non attack. |
| 154 | `filter:non_power` | one-hot | Eligibility filter: non power. |
| 155 | `filter:exhaust` | one-hot | Eligibility filter: exhaust. |
| 156 | `filter:non_exhaust` | one-hot | Eligibility filter: non exhaust. |
| 157 | `filter:strike` | one-hot | Eligibility filter: strike. |
| 158 | `filter:defend` | one-hot | Eligibility filter: defend. |
| 159 | `filter:rare` | one-hot | Eligibility filter: rare. |
| 160 | `filter:common` | one-hot | Eligibility filter: common. |
| 161 | `filter:uncommon` | one-hot | Eligibility filter: uncommon. |
| 162 | `filter:zero_cost` | one-hot | Eligibility filter: zero cost. |
| 163 | `filter:playable` | one-hot | Eligibility filter: playable. |
| 164 | `filter:upgradeable` | one-hot | Eligibility filter: upgradeable. |
| 165 | `filter:unupgraded` | one-hot | Eligibility filter: unupgraded. |
| 166 | `filter:generated` | one-hot | Eligibility filter: generated. |
| 167 | `filter:status` | one-hot | Eligibility filter: status. |
| 168 | `filter:curse` | one-hot | Eligibility filter: curse. |
| 169 | `filter:non_basic` | one-hot | Eligibility filter: non basic. |
| 170 | `filter:ethereal` | one-hot | Eligibility filter: ethereal. |
| 171 | `filter:not_source` | one-hot | Eligibility filter: not source. |
| 172 | `filter:attack_or_skill` | one-hot | Eligibility filter: attack or skill. |
| 173 | `filter:attack_or_power` | one-hot | Eligibility filter: attack or power. |
| 174 | `filter:same_definition` | one-hot | Eligibility filter: same definition. |
| 175 | `pool:none` | one-hot | Generation/transformation pool unspecified or not applicable in this step. |
| 176 | `pool:ironclad` | one-hot | Generation/transformation pool: ironclad. |
| 177 | `pool:colorless` | one-hot | Generation/transformation pool: colorless. |
| 178 | `pool:neutral` | one-hot | Generation/transformation pool: neutral. |
| 179 | `pool:foreign` | one-hot | Generation/transformation pool: foreign. |
| 180 | `source:none` | one-hot | Source pile or entity unspecified or not applicable in this step. |
| 181 | `source:hand` | one-hot | Source pile or entity: hand. |
| 182 | `source:draw` | one-hot | Source pile or entity: draw. |
| 183 | `source:discard` | one-hot | Source pile or entity: discard. |
| 184 | `source:exhaust` | one-hot | Source pile or entity: exhaust. |
| 185 | `source:all_piles` | one-hot | Source pile or entity: all piles. |
| 186 | `source:current_card` | one-hot | Source pile or entity: current card. |
| 187 | `source:event_card` | one-hot | Source pile or entity: event card. |
| 188 | `source:last_card` | one-hot | Source pile or entity: last card. |
| 189 | `source:ironclad_pool` | one-hot | Source pile or entity: ironclad pool. |
| 190 | `source:colorless_pool` | one-hot | Source pile or entity: colorless pool. |
| 191 | `source:neutral_pool` | one-hot | Source pile or entity: neutral pool. |
| 192 | `source:enemies` | one-hot | Source pile or entity: enemies. |
| 193 | `destination:none` | one-hot | Destination pile or location unspecified or not applicable in this step. |
| 194 | `destination:hand` | one-hot | Destination pile or location: hand. |
| 195 | `destination:draw_top` | one-hot | Destination pile or location: draw top. |
| 196 | `destination:draw_bottom` | one-hot | Destination pile or location: draw bottom. |
| 197 | `destination:draw_random` | one-hot | Destination pile or location: draw random. |
| 198 | `destination:discard` | one-hot | Destination pile or location: discard. |
| 199 | `destination:exhaust` | one-hot | Destination pile or location: exhaust. |
| 200 | `destination:removed` | one-hot | Destination pile or location: removed. |
| 201 | `destination:play` | one-hot | Destination pile or location: play. |
| 202 | `destination:inventory` | one-hot | Destination pile or location: inventory. |
| 203 | `reference:none` | one-hot | Object/value reference mode unspecified or not applicable in this step. |
| 204 | `reference:self` | one-hot | Object/value reference mode: self. |
| 205 | `reference:event` | one-hot | Object/value reference mode: event. |
| 206 | `reference:chosen` | one-hot | Object/value reference mode: chosen. |
| 207 | `reference:last_played` | one-hot | Object/value reference mode: last played. |
| 208 | `reference:last_turn` | one-hot | Object/value reference mode: last turn. |
| 209 | `reference:snapshot` | one-hot | Object/value reference mode: snapshot. |
| 210 | `reference:live` | one-hot | Object/value reference mode: live. |
| 211 | `condition:none` | one-hot | Condition gating the step unspecified or not applicable in this step. |
| 212 | `condition:always` | one-hot | Condition gating the step: always. |
| 213 | `condition:target_vulnerable` | one-hot | Condition gating the step: target vulnerable. |
| 214 | `condition:target_survives` | one-hot | Condition gating the step: target survives. |
| 215 | `condition:target_attacking` | one-hot | Condition gating the step: target attacking. |
| 216 | `condition:killed_target` | one-hot | Condition gating the step: killed target. |
| 217 | `condition:hp_lost_turn` | one-hot | Condition gating the step: hp lost turn. |
| 218 | `condition:no_attack_last_turn` | one-hot | Condition gating the step: no attack last turn. |
| 219 | `condition:same_type_last_turn` | one-hot | Condition gating the step: same type last turn. |
| 220 | `condition:draw_has_match` | one-hot | Condition gating the step: draw has match. |
| 221 | `condition:hand_has_match` | one-hot | Condition gating the step: hand has match. |
| 222 | `condition:counter_reached` | one-hot | Condition gating the step: counter reached. |
| 223 | `condition:card_is_exhausted` | one-hot | Condition gating the step: card is exhausted. |
| 224 | `condition:card_from_draw` | one-hot | Condition gating the step: card from draw. |
| 225 | `condition:not_initial_draw` | one-hot | Condition gating the step: not initial draw. |
| 226 | `condition:first_event` | one-hot | Condition gating the step: first event. |
| 227 | `condition:every_n` | one-hot | Condition gating the step: every n. |
| 228 | `condition:no_hp_loss` | one-hot | Condition gating the step: no hp loss. |
| 229 | `condition:all_enemies_dead` | one-hot | Condition gating the step: all enemies dead. |
| 230 | `condition:target_blocked` | one-hot | Condition gating the step: target blocked. |
| 231 | `condition:is_in_hand` | one-hot | Condition gating the step: is in hand. |
| 232 | `condition:owner_turn` | one-hot | Condition gating the step: owner turn. |
| 233 | `amount` | slog + known | Primary magnitude; absent when supplied by an expression or instance state. |
| 234 | `amount_known` | 0/1 | Whether `amount` was explicitly supplied, including zero. |
| 235 | `count` | slog + known | Number of items, hits, repetitions, or selections, as determined by the operation. |
| 236 | `count_known` | 0/1 | Whether `count` was explicitly supplied, including zero. |
| 237 | `threshold` | slog + known | Trigger/condition threshold. |
| 238 | `threshold_known` | 0/1 | Whether `threshold` was explicitly supplied, including zero. |
| 239 | `duration` | slog + known | Duration in the units of the step's scope/timing. |
| 240 | `duration_known` | 0/1 | Whether `duration` was explicitly supplied, including zero. |
| 241 | `increment` | slog + known | Counter or magnitude increment. |
| 242 | `increment_known` | 0/1 | Whether `increment` was explicitly supplied, including zero. |
| 243 | `initial` | slog + known | Initial counter or magnitude value. |
| 244 | `initial_known` | 0/1 | Whether `initial` was explicitly supplied, including zero. |
| 245 | `limit` | slog + known | Application/repetition cap. |
| 246 | `limit_known` | 0/1 | Whether `limit` was explicitly supplied, including zero. |
| 247 | `secondary` | slog + known | Secondary magnitude used by the operation. |
| 248 | `secondary_known` | 0/1 | Whether `secondary` was explicitly supplied, including zero. |
| 249 | `minimum` | slog + known | Lower bound. |
| 250 | `minimum_known` | 0/1 | Whether `minimum` was explicitly supplied, including zero. |
| 251 | `maximum` | slog + known | Upper bound. |
| 252 | `maximum_known` | 0/1 | Whether `maximum` was explicitly supplied, including zero. |
| 253 | `probability` | slog + known | Probability parameter, when explicitly supplied. |
| 254 | `probability_known` | 0/1 | Whether `probability` was explicitly supplied, including zero. |
| 255 | `powered` | 0/1 | Use the game's powered damage/Block modifier rules. |
| 256 | `snapshot` | 0/1 | Capture referenced values or a collection before subsequent changes. |
| 257 | `distinct` | 0/1 | Choose distinct results. |
| 258 | `with_replacement` | 0/1 | Random sampling permits repeated results. |
| 259 | `can_skip` | 0/1 | The selection/effect permits skipping. |
| 260 | `cost_free` | 0/1 | Play or generate using the described free-cost behavior. |
| 261 | `upgraded` | 0/1 | Effect generates, chooses, or applies an upgraded variant. |
| 262 | `temporary` | 0/1 | Modification has temporary lifetime. |
| 263 | `unpowered` | 0/1 | Use an unpowered amount, bypassing ordinary card damage/Block scaling. |
| 264 | `stochastic` | 0/1 | Effect includes randomness; the description is not a sampled outcome. |
| 265 | `conditional` | 0/1 | Effect depends on a condition. |
| 266 | `live` | 0/1 | Read the referenced value when the effect executes. |
| 267 | `repeat_each` | 0/1 | Apply the payload for each qualifying item/event. |
| 268 | `reset_turn` | 0/1 | Reset the counter/state each turn. |
| 269 | `reset_combat` | 0/1 | Reset the counter/state each combat. |
| 270 | `before_normal_draw` | 0/1 | Resolve before ordinary turn-start draws. |
| 271 | `skip_first` | 0/1 | Skip the first otherwise qualifying occurrence. |
| 272 | `self_exhaust` | 0/1 | Trigger/behavior refers specifically to this card exhausting. |
| 273 | `optional` | 0/1 | The effect or selection is optional. |
| 274 | `uses_instance_amount` | 0/1 | Magnitude uses the public instance_amount variable. |
| 275 | `uses_instance_secondary` | 0/1 | Magnitude uses the public instance_secondary variable. |
| 276 | `uses_instance_progress` | 0/1 | Behavior uses the public instance_progress counter. |
| 277 | `not_card_block` | 0/1 | Block comes from a rule/effect rather than an ordinary card-Block event. |

## Expression term — 83 fields

| Index | Field | Encoding | Meaning |
| ---: | --- | --- | --- |
| 0 | `op:constant` | one-hot | Literal constant. |
| 1 | `op:state` | one-hot | Read a named state value. |
| 2 | `op:count` | one-hot | Count matching items. |
| 3 | `op:add` | one-hot | Add arguments. |
| 4 | `op:subtract` | one-hot | Subtract arguments in order. |
| 5 | `op:multiply` | one-hot | Multiply arguments. |
| 6 | `op:divide` | one-hot | Divide arguments in order. |
| 7 | `op:min` | one-hot | Minimum. |
| 8 | `op:max` | one-hot | Maximum. |
| 9 | `op:clamp` | one-hot | Clamp to bounds. |
| 10 | `op:equal` | one-hot | Equality comparison. |
| 11 | `op:not_equal` | one-hot | Inequality comparison. |
| 12 | `op:greater` | one-hot | Greater-than comparison. |
| 13 | `op:greater_equal` | one-hot | Greater-than-or-equal comparison. |
| 14 | `op:less` | one-hot | Less-than comparison. |
| 15 | `op:less_equal` | one-hot | Less-than-or-equal comparison. |
| 16 | `op:and` | one-hot | Logical AND. |
| 17 | `op:or` | one-hot | Logical OR. |
| 18 | `op:not` | one-hot | Logical NOT. |
| 19 | `op:begin` | one-hot | Expression/group opening marker. |
| 20 | `op:end` | one-hot | Expression/group closing marker. |
| 21 | `ref:none` | one-hot | No state reference (for example, a literal constant). |
| 22 | `ref:energy` | one-hot | Player's current energy. |
| 23 | `ref:spent_x` | one-hot | Energy spent for an X-cost action. |
| 24 | `ref:hp` | one-hot | Player's current HP. |
| 25 | `ref:max_hp` | one-hot | Player's maximum HP. |
| 26 | `ref:missing_hp` | one-hot | Player's missing HP. |
| 27 | `ref:block` | one-hot | Player's current Block. |
| 28 | `ref:strength` | one-hot | Player's Strength. |
| 29 | `ref:dexterity` | one-hot | Player's Dexterity. |
| 30 | `ref:target_hp` | one-hot | Target's current HP. |
| 31 | `ref:target_block` | one-hot | Target's current Block. |
| 32 | `ref:target_vulnerable` | one-hot | Target's Vulnerable amount. |
| 33 | `ref:hand_count` | one-hot | Number of cards in hand. |
| 34 | `ref:draw_count` | one-hot | Number of cards in the draw pile. |
| 35 | `ref:discard_count` | one-hot | Number of cards in discard. |
| 36 | `ref:exhaust_count` | one-hot | Number of cards in exhaust. |
| 37 | `ref:strike_count` | one-hot | Count of cards with the Strike tag. |
| 38 | `ref:attacks_in_hand` | one-hot | Number of attacks in hand. |
| 39 | `ref:non_attacks_in_hand` | one-hot | Number of non-attacks in hand. |
| 40 | `ref:powers_in_hand` | one-hot | Number of powers in hand. |
| 41 | `ref:cards_played_turn` | one-hot | Cards played this turn. |
| 42 | `ref:attacks_played_turn` | one-hot | Attacks played this turn. |
| 43 | `ref:skills_played_turn` | one-hot | Skills played this turn. |
| 44 | `ref:cards_drawn_turn` | one-hot | Cards drawn this turn. |
| 45 | `ref:extra_draws_turn` | one-hot | Extra draws this turn. |
| 46 | `ref:cards_drawn_combat` | one-hot | Cards drawn this combat. |
| 47 | `ref:cards_exhausted_turn` | one-hot | Cards exhausted this turn. |
| 48 | `ref:cards_exhausted_combat` | one-hot | Cards exhausted this combat. |
| 49 | `ref:hp_lost_turn` | one-hot | HP lost this turn. |
| 50 | `ref:unblocked_damage_turn` | one-hot | Unblocked damage taken this turn. |
| 51 | `ref:last_turn_card_count` | one-hot | Cards played last turn. |
| 52 | `ref:last_turn_matching_cards` | one-hot | Last-turn cards matching the expression's criteria. |
| 53 | `ref:instance_amount` | one-hot | Public primary amount of the source instance. |
| 54 | `ref:instance_secondary` | one-hot | Public secondary amount of the source instance. |
| 55 | `ref:instance_progress` | one-hot | Public progress/countdown of the source instance. |
| 56 | `ref:instance_growth` | one-hot | Public growth value of the source instance. |
| 57 | `ref:instance_replay` | one-hot | Public replay count of the source instance. |
| 58 | `ref:instance_cost` | one-hot | Public cost of the source instance. |
| 59 | `ref:turn` | one-hot | Current turn number. |
| 60 | `ref:qualifying_events` | one-hot | Count of events qualifying for the rule. |
| 61 | `ref:snapshot_count` | one-hot | Size of a previously captured collection. |
| 62 | `ref:play_count` | one-hot | Play/replay multiplicity. |
| 63 | `ref:target_count` | one-hot | Number of qualifying targets. |
| 64 | `ref:cost_paid` | one-hot | Actual cost paid by the triggering play. |
| 65 | `ref:damage_dealt` | one-hot | Damage dealt by the relevant event. |
| 66 | `ref:event_hp_loss` | one-hot | HP lost in the relevant event. |
| 67 | `ref:source_damage` | one-hot | Damage value of the source card/effect. |
| 68 | `ref:source_block` | one-hot | Block value of the source card/effect. |
| 69 | `ref:available_choices` | one-hot | Number of available selection choices. |
| 70 | `ref:hand_capacity` | one-hot | Available hand capacity. |
| 71 | `ref:potion_slots` | one-hot | Potion slot count. |
| 72 | `ref:cards_played_combat` | one-hot | Cards played this combat. |
| 73 | `ref:target_debuff_count` | one-hot | Number of debuffs on the target. |
| 74 | `ref:hp_loss_events_combat` | one-hot | Number of HP-loss events this combat. |
| 75 | `ref:enchant_amount` | one-hot | Public enchantment amount of the source card. |
| 76 | `value` | slog | Literal numeric value; defaults to zero. |
| 77 | `coefficient` | slog | State-term coefficient; state() defaults it to one, encode_term() alone to zero. |
| 78 | `arity` | raw | Number of arguments consumed by this expression operator; defaults to zero. |
| 79 | `argument` | raw | Argument marker/position when explicitly supplied; defaults to zero. |
| 80 | `known` | raw | Literal-known indicator; constant() sets one, state() leaves zero. Not a padding mask. |
| 81 | `snapshot` | raw | Explicit snapshot-read flag; defaults to zero. |
| 82 | `live` | raw | Explicit live-read flag; defaults to zero. |

## Candidate preview — 15 fields

| Index | Field | Encoding | Meaning |
| ---: | --- | --- | --- |
| 0 | `damage` | slog + known | Current preview damage; separate from hit count and later effects. |
| 1 | `damage_known` | 0/1 | Whether preview damage is available. |
| 2 | `block` | slog + known | Current preview Block. |
| 3 | `block_known` | 0/1 | Whether preview Block is available. |
| 4 | `hits` | slog + known | Current preview hit count. |
| 5 | `hits_known` | 0/1 | Whether preview hit count is available. |
| 6 | `play_count` | slog | Play count; the source adapter defaults a missing value to one. |
| 7 | `exact` | 0/1 | Preview marked known without conditional status. Can coexist with stochastic; not a full-action guarantee. |
| 8 | `conditional` | 0/1 | Preview status contains conditional. |
| 9 | `stochastic` | 0/1 | Preview status contains stochastic or target type is RandomEnemy. |
| 10 | `source_side` | 0/1 | Preview has no concrete target index (target_index < 0). |
| 11 | `all_enemies` | 0/1 | Preview target type is AllEnemies. |
| 12 | `random_target` | 0/1 | Preview target type is RandomEnemy. |
| 13 | `has_target` | 0/1 | Preview has a concrete target index (target_index >= 0). |
| 14 | `effect_position` | slog | Zero-based effect index reported by the preview; defaults to zero. |

## Selection purpose — 31 fields

| Index | Field | Encoding | Meaning |
| ---: | --- | --- | --- |
| 0 | `op:none` | one-hot | Selection operation: none. |
| 1 | `op:exhaust` | one-hot | Selection operation: exhaust. |
| 2 | `op:discard` | one-hot | Selection operation: discard. |
| 3 | `op:retrieve` | one-hot | Selection operation: retrieve. |
| 4 | `op:upgrade` | one-hot | Selection operation: upgrade. |
| 5 | `op:modify` | one-hot | Selection operation: modify. |
| 6 | `op:autoplay` | one-hot | Selection operation: autoplay. |
| 7 | `op:generate` | one-hot | Selection operation: generate. |
| 8 | `op:clone` | one-hot | Selection operation: clone. |
| 9 | `op:transform` | one-hot | Selection operation: transform. |
| 10 | `op:retain` | one-hot | Selection operation: retain. |
| 11 | `op:move` | one-hot | Selection operation: move. |
| 12 | `from:none` | one-hot | Source pile: none. |
| 13 | `from:hand` | one-hot | Source pile: hand. |
| 14 | `from:draw` | one-hot | Source pile: draw. |
| 15 | `from:discard` | one-hot | Source pile: discard. |
| 16 | `from:exhaust` | one-hot | Source pile: exhaust. |
| 17 | `from:generated` | one-hot | Source pile: generated. |
| 18 | `to:none` | one-hot | Destination pile: none. |
| 19 | `to:hand` | one-hot | Destination pile: hand. |
| 20 | `to:draw` | one-hot | Destination pile: draw. |
| 21 | `to:discard` | one-hot | Destination pile: discard. |
| 22 | `to:exhaust` | one-hot | Destination pile: exhaust. |
| 23 | `to:play` | one-hot | Destination pile: play. |
| 24 | `min` | slog | Minimum required selections; zero for a skippable prompt. |
| 25 | `max` | slog | Requested maximum selection count. |
| 26 | `selected` | slog | Number of cards already selected. |
| 27 | `skippable` | 0/1 | The prompt allows skipping. |
| 28 | `random` | 0/1 | Reserved slot: the source selection adapter does not populate it; remains zero. |
| 29 | `remaining` | slog | max(0, requested amount - selected count). |
| 30 | `source_present` | 0/1 | A source semantic node is available. |

## Continuation frame — 20 fields

| Index | Field | Encoding | Meaning |
| ---: | --- | --- | --- |
| 0 | `pending_play` | slog (also flags) | Public pending-card-play counter. |
| 1 | `owed_draws` | slog (also flags) | Public hand_draw_owed quantity. |
| 2 | `owed_plays` | slog (also flags) | Public havoc_play_top plus stampede_remaining quantities. |
| 3 | `selected` | slog (also flags) | Number already chosen in the open prompt. |
| 4 | `remaining` | slog (also flags) | Nonnegative remaining selection amount. |
| 5 | `extra_plays` | slog (also flags) | Parked card's extra-play counter. |
| 6 | `play_index` | slog (also flags) | Parked card's current play index. |
| 7 | `energy_spent` | slog (also flags) | Energy spent by the parked play. |
| 8 | `nested_depth` | slog (also flags) | Nested autoplay depth. |
| 9 | `before_draw` | slog (also flags) | Autoplay batch resumes at turn start. |
| 10 | `end_turn` | slog (also flags) | Autoplay batch resumes at turn end. |
| 11 | `after_selection` | slog (also flags) | An open selection is present. |
| 12 | `snapshot_count` | slog (also flags) | Number of snapshot cards retained by the prompt. |
| 13 | `source_present` | slog (also flags) | A source card/rule node is available for this frame. |
| 14 | `deferred_strength` | slog (also flags) | Public deferred card_rupture_strength quantity. |
| 15 | `frame_summary` | slog (also flags) | This row summarizes the suspended continuation. |
| 16 | `frame_chosen` | slog (also flags) | This row references an already-chosen card. |
| 17 | `frame_snapshot` | slog (also flags) | This row references a snapshot card. |
| 18 | `ordinal` | slog (also flags) | One-based order within chosen/snapshot cards; zero in the summary. |
| 19 | `card_known` | slog (also flags) | This detail frame has a known card-node reference. |

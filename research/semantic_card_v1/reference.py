"""Generate the feature reference from the actual ordered schema definitions."""

import argparse
from pathlib import Path

from . import schema as s


CURRENT_NUMERIC = {
    "current_cost": "Current payable energy cost supplied by a public preview.",
    "current_x": "Current X value; the live adapter uses current_cost for an X-cost card.",
    "damage": "Current effective damage from a preview; hit count and target scope are separate.",
    "block": "Current effective Block from a preview.",
    "hits": "Current number of hits from a preview.",
    "play_count": "Current number of plays/replays from a preview.",
    "upgrade_level": "Upgrade indicator exported by the card instance; the inspector uses 0 or 1.",
    "bonus_damage": "Instance BonusDamage modifier, before interpreting effective damage.",
    "bonus_block": "Instance BonusBlock modifier, before interpreting effective Block.",
    "growth": "Instance MomentumDamage growth counter.",
    "enchant_amount": "Instance enchantment amount/counter.",
    "cost_bump": "Instance CostBump modifier.",
    "cost_for_combat": "Combat-wide cost override; unknown when the engine sentinel is unset.",
    "cost_this_turn": "This-turn cost override; unknown when the engine sentinel is unset.",
    "cost_restore": "CostOverrideBaseline while a temporary override exists; otherwise unknown.",
    "known_top_position": "One-based position in the publicly known top of the draw pile.",
    "known_bottom_position": "One-based distance from the publicly known bottom of the draw pile.",
}
CURRENT_FLAGS = {
    "playable": "Public preview says playable; in the inspector this requires --playable.",
    "in_hand": "Card belongs to the hand; in the inspector this requires --in-hand.",
    "cost_x": "Card definition has an X energy cost.",
    "unplayable": "Unplayable keyword, distinct from temporary action illegality.",
    "exhaust": "Current Exhaust keyword.",
    "ethereal": "Current Ethereal keyword, including combat modifiers.",
    "retain": "Current Retain keyword/flag, including turn and combat modifiers.",
    "innate": "Current Innate keyword.",
    "sly": "Current Sly keyword/flag.",
    "eternal": "Current Eternal keyword.",
    "strike_tag": "Card has the shared Strike tag; not a card identity one-hot.",
    "defend_tag": "Card has the shared Defend tag; not a card identity one-hot.",
    "can_upgrade": "Definition permits upgrading and this variant is not already upgraded.",
    "free_this_turn": "Instance FreeThisTurn flag.",
    "free_until_played": "Instance FreeUntilPlayed flag.",
    "retained": "Reserved slot: the source live adapter does not populate it; zero in its output.",
    "played_this_turn": "Instance PlayedThisTurn flag.",
    "enchant_spent": "Instance enchantment-spent flag.",
    "dampened": "Instance DampenedUpgrade flag.",
    "bound": "Instance Bound flag.",
    "pending": "Card is in a suspended/pending play group.",
    "enemy_held": "Card is held by an enemy.",
    "offer": "Node was created as a selection/offer candidate outside a matched physical pile row.",
    "source_snapshot": "Node represents a selection source or an ordered chosen/snapshot card.",
    "temporary_copy": "Reserved slot: the source live adapter does not populate it; zero in its output.",
}
STEP_NUMERIC = {
    "amount": "Primary magnitude; absent when supplied by an expression or instance state.",
    "count": "Number of items, hits, repetitions, or selections, as determined by the operation.",
    "threshold": "Trigger/condition threshold.",
    "duration": "Duration in the units of the step's scope/timing.",
    "increment": "Counter or magnitude increment.",
    "initial": "Initial counter or magnitude value.",
    "limit": "Application/repetition cap.",
    "secondary": "Secondary magnitude used by the operation.",
    "minimum": "Lower bound.",
    "maximum": "Upper bound.",
    "probability": "Probability parameter, when explicitly supplied.",
}
STEP_FLAGS = {
    "powered": "Use the game's powered damage/Block modifier rules.",
    "snapshot": "Capture referenced values or a collection before subsequent changes.",
    "distinct": "Choose distinct results.",
    "with_replacement": "Random sampling permits repeated results.",
    "can_skip": "The selection/effect permits skipping.",
    "cost_free": "Play or generate using the described free-cost behavior.",
    "upgraded": "Effect generates, chooses, or applies an upgraded variant.",
    "temporary": "Modification has temporary lifetime.",
    "unpowered": "Use an unpowered amount, bypassing ordinary card damage/Block scaling.",
    "stochastic": "Effect includes randomness; the description is not a sampled outcome.",
    "conditional": "Effect depends on a condition.",
    "live": "Read the referenced value when the effect executes.",
    "repeat_each": "Apply the payload for each qualifying item/event.",
    "reset_turn": "Reset the counter/state each turn.",
    "reset_combat": "Reset the counter/state each combat.",
    "before_normal_draw": "Resolve before ordinary turn-start draws.",
    "skip_first": "Skip the first otherwise qualifying occurrence.",
    "self_exhaust": "Trigger/behavior refers specifically to this card exhausting.",
    "optional": "The effect or selection is optional.",
    "uses_instance_amount": "Magnitude uses the public instance_amount variable.",
    "uses_instance_secondary": "Magnitude uses the public instance_secondary variable.",
    "uses_instance_progress": "Behavior uses the public instance_progress counter.",
    "not_card_block": "Block comes from a rule/effect rather than an ordinary card-Block event.",
}
CATEGORY_MEANINGS = {
    "op": "Effect operation", "target": "Effect recipient", "event": "Trigger event",
    "phase": "Execution timing", "property": "Property being modified", "operator": "Update operator",
    "scope": "Lifetime or application scope", "selection": "Selection method", "filter": "Eligibility filter",
    "pool": "Generation/transformation pool", "source": "Source pile or entity",
    "destination": "Destination pile or location", "reference": "Object/value reference mode",
    "condition": "Condition gating the step",
}
TERM_OPS = {
    "constant": "Literal constant", "state": "Read a named state value", "count": "Count matching items",
    "add": "Add arguments", "subtract": "Subtract arguments in order", "multiply": "Multiply arguments",
    "divide": "Divide arguments in order", "min": "Minimum", "max": "Maximum", "clamp": "Clamp to bounds",
    "equal": "Equality comparison", "not_equal": "Inequality comparison", "greater": "Greater-than comparison",
    "greater_equal": "Greater-than-or-equal comparison", "less": "Less-than comparison",
    "less_equal": "Less-than-or-equal comparison", "and": "Logical AND", "or": "Logical OR",
    "not": "Logical NOT", "begin": "Expression/group opening marker", "end": "Expression/group closing marker",
}
TERM_REFS = {
    "none": "No state reference (for example, a literal constant).",
    "energy": "Player's current energy.", "spent_x": "Energy spent for an X-cost action.",
    "hp": "Player's current HP.", "max_hp": "Player's maximum HP.", "missing_hp": "Player's missing HP.",
    "block": "Player's current Block.", "strength": "Player's Strength.", "dexterity": "Player's Dexterity.",
    "target_hp": "Target's current HP.", "target_block": "Target's current Block.",
    "target_vulnerable": "Target's Vulnerable amount.", "hand_count": "Number of cards in hand.",
    "draw_count": "Number of cards in the draw pile.", "discard_count": "Number of cards in discard.",
    "exhaust_count": "Number of cards in exhaust.", "strike_count": "Count of cards with the Strike tag.",
    "attacks_in_hand": "Number of attacks in hand.", "non_attacks_in_hand": "Number of non-attacks in hand.",
    "powers_in_hand": "Number of powers in hand.", "cards_played_turn": "Cards played this turn.",
    "attacks_played_turn": "Attacks played this turn.", "skills_played_turn": "Skills played this turn.",
    "cards_drawn_turn": "Cards drawn this turn.", "extra_draws_turn": "Extra draws this turn.",
    "cards_drawn_combat": "Cards drawn this combat.", "cards_exhausted_turn": "Cards exhausted this turn.",
    "cards_exhausted_combat": "Cards exhausted this combat.", "hp_lost_turn": "HP lost this turn.",
    "unblocked_damage_turn": "Unblocked damage taken this turn.", "last_turn_card_count": "Cards played last turn.",
    "last_turn_matching_cards": "Last-turn cards matching the expression's criteria.",
    "instance_amount": "Public primary amount of the source instance.",
    "instance_secondary": "Public secondary amount of the source instance.",
    "instance_progress": "Public progress/countdown of the source instance.",
    "instance_growth": "Public growth value of the source instance.",
    "instance_replay": "Public replay count of the source instance.",
    "instance_cost": "Public cost of the source instance.", "turn": "Current turn number.",
    "qualifying_events": "Count of events qualifying for the rule.",
    "snapshot_count": "Size of a previously captured collection.", "play_count": "Play/replay multiplicity.",
    "target_count": "Number of qualifying targets.", "cost_paid": "Actual cost paid by the triggering play.",
    "damage_dealt": "Damage dealt by the relevant event.", "event_hp_loss": "HP lost in the relevant event.",
    "source_damage": "Damage value of the source card/effect.", "source_block": "Block value of the source card/effect.",
    "available_choices": "Number of available selection choices.", "hand_capacity": "Available hand capacity.",
    "potion_slots": "Potion slot count.", "cards_played_combat": "Cards played this combat.",
    "target_debuff_count": "Number of debuffs on the target.",
    "hp_loss_events_combat": "Number of HP-loss events this combat.",
    "enchant_amount": "Public enchantment amount of the source card.",
}
TERM_TRAILING = {
    "value": ("slog", "Literal numeric value; defaults to zero."),
    "coefficient": ("slog", "State-term coefficient; state() defaults it to one, encode_term() alone to zero."),
    "arity": ("raw", "Number of arguments consumed by this expression operator; defaults to zero."),
    "argument": ("raw", "Argument marker/position when explicitly supplied; defaults to zero."),
    "known": ("raw", "Literal-known indicator; constant() sets one, state() leaves zero. Not a padding mask."),
    "snapshot": ("raw", "Explicit snapshot-read flag; defaults to zero."),
    "live": ("raw", "Explicit live-read flag; defaults to zero."),
}
PREVIEW = {
    "damage": ("slog + known", "Current preview damage; separate from hit count and later effects."),
    "damage_known": ("0/1", "Whether preview damage is available."),
    "block": ("slog + known", "Current preview Block."),
    "block_known": ("0/1", "Whether preview Block is available."),
    "hits": ("slog + known", "Current preview hit count."),
    "hits_known": ("0/1", "Whether preview hit count is available."),
    "play_count": ("slog", "Play count; the source adapter defaults a missing value to one."),
    "exact": ("0/1", "Preview marked known without conditional status. Can coexist with stochastic; not a full-action guarantee."),
    "conditional": ("0/1", "Preview status contains conditional."),
    "stochastic": ("0/1", "Preview status contains stochastic or target type is RandomEnemy."),
    "source_side": ("0/1", "Preview has no concrete target index (target_index < 0)."),
    "all_enemies": ("0/1", "Preview target type is AllEnemies."),
    "random_target": ("0/1", "Preview target type is RandomEnemy."),
    "has_target": ("0/1", "Preview has a concrete target index (target_index >= 0)."),
    "effect_position": ("slog", "Zero-based effect index reported by the preview; defaults to zero."),
}
SELECTION = {
    "min": ("slog", "Minimum required selections; zero for a skippable prompt."),
    "max": ("slog", "Requested maximum selection count."),
    "selected": ("slog", "Number of cards already selected."),
    "skippable": ("0/1", "The prompt allows skipping."),
    "random": ("0/1", "Reserved slot: the source selection adapter does not populate it; remains zero."),
    "remaining": ("slog", "max(0, requested amount - selected count)."),
    "source_present": ("0/1", "A source semantic node is available."),
}
CONTINUATION = {
    "pending_play": "Public pending-card-play counter.",
    "owed_draws": "Public hand_draw_owed quantity.",
    "owed_plays": "Public havoc_play_top plus stampede_remaining quantities.",
    "selected": "Number already chosen in the open prompt.",
    "remaining": "Nonnegative remaining selection amount.",
    "extra_plays": "Parked card's extra-play counter.",
    "play_index": "Parked card's current play index.",
    "energy_spent": "Energy spent by the parked play.",
    "nested_depth": "Nested autoplay depth.",
    "before_draw": "Autoplay batch resumes at turn start.",
    "end_turn": "Autoplay batch resumes at turn end.",
    "after_selection": "An open selection is present.",
    "snapshot_count": "Number of snapshot cards retained by the prompt.",
    "source_present": "A source card/rule node is available for this frame.",
    "deferred_strength": "Public deferred card_rupture_strength quantity.",
    "frame_summary": "This row summarizes the suspended continuation.",
    "frame_chosen": "This row references an already-chosen card.",
    "frame_snapshot": "This row references a snapshot card.",
    "ordinal": "One-based order within chosen/snapshot cards; zero in the summary.",
    "card_known": "This detail frame has a known card-node reference.",
}


def describe(record, field):
    """Return (encoding, meaning); missing documentation is an explicit error."""
    if record == "card":
        if field.endswith("_known"):
            name = field.removesuffix("_known")
            return "0/1", f"Whether `{name}` is available; one for a known zero, zero for unavailable."
        if field in CURRENT_NUMERIC:
            return "slog + known", CURRENT_NUMERIC[field]
        if field in CURRENT_FLAGS:
            return "0/1", CURRENT_FLAGS[field]
        prefix, value = field.split(":", 1)
        return "one-hot", f"Card {prefix} is {value}."
    if record == "step":
        if ":" in field:
            category, value = field.split(":", 1)
            meaning = CATEGORY_MEANINGS[category]
            return "one-hot", (f"{meaning}: {value.replace('_', ' ')}." if value != "none"
                               else f"{meaning} unspecified or not applicable in this step.")
        if field.endswith("_known"):
            return "0/1", f"Whether `{field.removesuffix('_known')}` was explicitly supplied, including zero."
        if field in STEP_NUMERIC:
            return "slog + known", STEP_NUMERIC[field]
        return "0/1", STEP_FLAGS[field]
    if record == "term":
        if field.startswith("op:"):
            return "one-hot", TERM_OPS[field[3:]] + "."
        if field.startswith("ref:"):
            return "one-hot", TERM_REFS[field[4:]]
        return TERM_TRAILING[field]
    if record == "preview":
        return PREVIEW[field]
    if record == "selection":
        if ":" in field:
            prefix, value = field.split(":", 1)
            meaning = {"op": "Selection operation", "from": "Source pile", "to": "Destination pile"}[prefix]
            return "one-hot", f"{meaning}: {value.replace('_', ' ')}."
        return SELECTION[field]
    if record == "continuation":
        return "slog (also flags)", CONTINUATION[field]
    raise ValueError(f"Unknown record {record}")


def render_reference():
    lines = [
        "# Feature reference", "",
        f"Schema: `{s.SCHEMA}`. All indices below are **zero-based** and local to their record.", "",
        "Generated from [schema.py](../schema.py) and the reviewed descriptions in [reference.py](../reference.py).",
        "Regenerate from the repository root with `python -m research.semantic_card_v1.reference --write`.", "",
        "## Encoding conventions", "",
        "- `slog(x) = sign(x) × ln(1 + abs(x))`, stored as float32. This is not clipping or standardization.",
        "- `slog + known`: the value and its following availability flag are distinct fields. Known zero is `(0, 1)`; unavailable is `(0, 0)`.",
        "- `one-hot`: one field per shared category value. Effect categories default to `none`. These are mechanics categories, not card identity encodings.",
        "- `0/1`: a boolean indicator; omitted example flags default to zero and have no separate unknown channel.",
        "- `raw`: numeric value copied directly as float32; no log transform.",
        "- All 20 continuation fields use slog, including flags: an asserted flag is `ln(2)`, not `1`.",
        "- An effect or expression sequence uses separate boolean padding masks. A padded all-zero row is not a valid `none` step.",
        "- The inspector emits current-card, effect-step, and expression-term records. Preview, selection, and continuation tables document the surrounding live schema; their adapter is outside this module.", "",
        "The six records are separate arrays. They are not concatenated into one 511-dimensional card vector.", "",
    ]
    records = [
        ("card", "Current card", s.CARD_DENSE_FIELDS), ("step", "Effect step", s.STEP_FIELDS),
        ("term", "Expression term", s.TERM_FIELDS), ("preview", "Candidate preview", s.PREVIEW_FIELDS),
        ("selection", "Selection purpose", s.SELECTION_FIELDS),
        ("continuation", "Continuation frame", s.CONTINUATION_FIELDS),
    ]
    for record, title, fields in records:
        lines.extend([f"## {title} — {len(fields)} fields", "",
                      "| Index | Field | Encoding | Meaning |", "| ---: | --- | --- | --- |"])
        for index, field in enumerate(fields):
            encoding, meaning = describe(record, field)
            lines.append(f"| {index} | `{field}` | {encoding} | {meaning} |")
        lines.append("")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Regenerate docs/feature-reference.md")
    args = parser.parse_args()
    content = render_reference()
    if args.write:
        destination = Path(__file__).with_name("docs") / "feature-reference.md"
        destination.write_text(content, encoding="utf-8")
        print(f"Wrote {destination}")
    else:
        print(content, end="")


if __name__ == "__main__":
    main()

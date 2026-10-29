"""Field order for the frozen 30→32 enemy and 6→10 legal-action layouts."""

SCHEMA = "current-hand-lethality/v1"
PROFILES = ("masked_baseline", "masked_lethality")
SEARCH_LIMITS = {"total_edges": 128, "edges_per_first_action": 32, "max_plays": 8}

BASE_ENEMY_FIELDS = (
    "hp", "max_hp", "hp_fraction", "block", "targetable", "alive",
    "is_hex_applier", "is_shrink_applier", "slot_position", "n_intents",
    "curl_up_pending", "possessed_player_strength", "possessed_player_dexterity",
    "steam_eruption_about_to_blow", "steam_eruption_exploding", "steam_eruption_damage",
    "nemesis_intangible_on", "at_player_back", "is_dampen_caster", "stolen_gold",
    "has_stolen_card", "escaped", "off_balance", "powered_hits_this_turn", "has_slot",
    "slot", "strangle_before_play", "has_secondary_intent", "is_parked_play_target",
    "is_parked_autoplay_target",
)
BASE_ACTION_FIELDS = (
    "auto_target", "has_target", "sel_hand", "sel_discard", "sel_draw", "sel_generated",
)
ENEMY_FIELDS = ("lethal_sequence_found", "lethal_search_complete")
ACTION_FIELDS = ("immediate_kill", "lethal_starting_here", "immediate_known", "continuation_complete")
MASKED_ENEMY_FIELDS = ("max_hp", "hp_fraction")
MASKED_ENEMY_INDICES = tuple(BASE_ENEMY_FIELDS.index(name) for name in MASKED_ENEMY_FIELDS)
FULL_ENEMY_FIELDS = BASE_ENEMY_FIELDS + ENEMY_FIELDS
FULL_ACTION_FIELDS = BASE_ACTION_FIELDS + ACTION_FIELDS

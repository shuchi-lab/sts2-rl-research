"""Visible-state adapter: raw worker observation -> typed numpy arrays -> padded torch batch.

Schema ``crl.features/v2``. What goes in, exactly:

* **Player block** (49 after the network's effect pooling, as proposed): 7 basic scalars,
  9 history counters, and the player's effect rows pooled to 32 + count.
* **Effects** (player and every enemy; separate owners, shared row encoder): one row per
  logical BuffState after helper folding, id from the 253-entry BuffId codebook plus the 10
  proposed fields, every column defined in :data:`EFFECT_FIELDS` / :func:`_effect_rows`.
  Effects whose engine side state lives outside BuffState and is NOT mapped here are listed in
  :data:`UNMAPPED_SIDE_STATE` and raise :class:`UnsupportedState` (never silently truncated).
* **Cards**: every card instance in hand, draw, discard, exhaust and the incoming queue, as
  card-definition id + public per-instance fields + context (effective cost/playability for
  hand cards, known-position for the part of the draw pile the player placed knowingly). The
  unknown part of the draw pile arrives from the worker already canonically sorted, and the
  network pools piles order-free, so hidden draw order cannot reach the features.
* **Enemies**: id, public HP/max HP/block, targetability, declared intents with the engine's
  announced damage, Hex/Shrink applier links, and their own effect rows. No AI move index,
  move history, RNG or future intents.
* **Relics** (id + counter), **potion inventory** (ids; use is disabled), selection prompt
  (kind, source card, amount, picks so far, skippable).
* **Options**: every legal option: kind, hand slot (play) or candidate card (select), target
  enemy slot, and whether the engine auto-targets.

Schema ``crl.features/v3`` (effect scopes ``three-act-v1`` and ``three-act-214-v1``; chosen for
both v3 vocabularies) keeps every v2 column in place and appends:

* global: player-turn/selection phase, foreign resources (Stars, orb slots, Osty), the selection
  screen's own flags, the Dampen-downgraded card count, and every public combat counter the
  worker classifies as ``counter`` (:data:`EXPECTED_COUNTERS`, verified against hello);
* cards: two more piles with seven context flags. ``pending`` (the game's Play pile and queues):
  the chosen card a selection interrupted, parked automatic children (Havoc's stack; a
  Stampede/Mayhem/Distilled Chaos batch's waiting child and staged cards), the auto-play
  queue, and cards a selection will hand over. ``enemy_held``: a Thieving Hopper's card;
* enemies: :data:`ENEMY_EXTRA_V3` relations/counters (Curl Up pending, Surrounded back side,
  Dampen caster, Possess tallies, Steam Eruption phase/damage, Nemesis flip, stolen gold, ...);
* orbs: one row per channelled orb (type, evoke, passive, queue position);
* effect rows: the side state that v2 rejected (:data:`UNMAPPED_SIDE_STATE`) is folded into
  that row's columns (:data:`SIDE_STATE_FOLDS_V3`), so those effects are encoded, not refused.

v3 also rejects any decision whose worker-reported boundary invariants fail, a History Course
encore (its card memory is not encoded), and a non-Ironclad character. With the 214-card
vocabulary (``crl.vocab/v3-three-act-214``) the layout and dimensions are identical; only the
card identity table is smaller, and any card outside it raises :class:`ForeignCardState`.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .vocab import V3_SCHEMAS, VOCAB_SCHEMA_V3_214, Vocab, slog

FEATURE_SCHEMA = "crl.features/v2"
INT_MIN = -2147483648

EXPECTED_CARD_FIELDS = (
    "DefId", "Upgraded", "FreeThisTurn", "Retain", "Enchantment", "EnchantAmount", "CostBump",
    "ReplayCount", "EnchantSpent", "CostForCombat", "BonusDamage", "MomentumDamage", "TinkerType",
    "TinkerRider", "PlayedThisTurn", "SlyThisTurn", "SlyForCombat", "EtherealForCombat",
    "RetainForCombat", "DampenedUpgrade", "Bound", "RetainThisTurn", "CostThisTurn", "FreeUntilPlayed",
    "BonusBlock", "CostOverrideThisTurn", "CostOverrideBaseline", "CostOverrideSetUpgraded",
)
F = {name: i for i, name in enumerate(EXPECTED_CARD_FIELDS)}

CARD_FEATURES = (
    "upgraded", "free_this_turn", "retain", "enchant_amount", "cost_bump", "replay_count",
    "enchant_spent", "cost_for_combat_set", "cost_for_combat", "bonus_damage", "momentum_damage",
    "tinker_type_set", "tinker_type_value", "tinker_rider_value", "played_this_turn", "sly_this_turn",
    "sly_for_combat", "ethereal_for_combat", "retain_for_combat", "dampened_upgrade", "bound",
    "retain_this_turn", "cost_this_turn_set", "cost_this_turn", "free_until_played", "bonus_block",
    "cost_override_set", "cost_override", "cost_override_baseline", "cost_override_set_upgraded",
    # context
    "has_hand_view", "effective_cost", "playable", "known_top", "known_top_position",
    "known_bottom", "known_bottom_position",
)
PILES = ("hand", "draw", "discard", "exhaust", "incoming")

PLAYER_BASIC = ("hp", "max_hp", "block", "energy", "energy_capacity", "gold", "turn")
PLAYER_HISTORY = (
    "card_starts_turn", "attack_starts_turn", "attack_skill_starts_turn", "attack_finishes_turn",
    "exhausted_any_turn", "damaged_any_turn", "damage_events_combat",
    "eligible_block_gains_turn", "card_finishes_combat",
)
# The entry-HP reward context is separate from the agreed 49-dimensional player block.
GLOBAL_EXTRA = ("entry_hp", "hp_over_max_hp", "hp_over_entry_hp", "in_selection", "selection_amount", "selection_selected", "selection_skippable",
                "selection_candidates", "potions_held")
GLOBAL_FEATURES = PLAYER_BASIC + PLAYER_HISTORY + GLOBAL_EXTRA

EFFECT_FIELDS = (
    "amount", "secondary_value", "progress_1", "progress_2", "skip_next_duration_tick",
    "has_source_relation", "application_order", "has_secondary_value", "has_progress_1", "has_progress_2",
)

# Raw helper rows folded into their parent's row: helper -> (parent, column).
HELPERS = {
    "InfernoSelfDamage": ("Inferno", "secondary_value"),
    "CrimsonMantleSelfDamage": ("CrimsonMantleBlock", "secondary_value"),
    "TheBombDamage": ("TheBombPower", "secondary_value"),
    "PanacheArmed": ("PanachePower", "progress_2"),
}
DURATION_DEBUFFS = ("Vulnerable", "Weak", "Frail", "Shrink")  # BuffSystem.IsDurationDebuff

# Effects with behaviour-relevant engine state outside BuffState that this adapter does not
# map. Seeing one is an explicit UnsupportedState, so coverage gaps cannot pass silently.
UNMAPPED_SIDE_STATE = {
    "Orbit": "CombatState.OrbitEnergySpent",
    "VoidForm": "CombatState.VoidFormCardsPlayedThisTurn",
    "PossessSpeed": "EnemyState possessed-player tallies",
    "PossessStrength": "EnemyState.PossessedPlayerStrength/Dexterity",
    "SteamEruption": "EnemyState.SteamEruptionPhase/Damage",
    "Nemesis": "EnemyState.NemesisIntangibleOn",
    "Dampen": "caster links (cards carry DampenedUpgrade)",
    "Surrounded": "facing kept outside the buff",
    "CurlUp": "EnemyState.CurlUpCardSourceId",
    "Skittish": "EnemyState.SkittishFirstHit*",
}

OPTION_KINDS = ("play_card", "end_turn", "select", "skip_selection")
OPTION_FEATURES = ("auto_target", "has_target", "sel_hand", "sel_discard", "sel_draw", "sel_generated")
ENEMY_FEATURES = ("hp", "max_hp", "hp_fraction", "block", "targetable", "alive", "is_hex_applier",
                  "is_shrink_applier", "slot_position", "n_intents")
MAX_DECLARED_INTENTS = 3  # EnemyAI.DeclaredIntent(e, i) is only defined for i < 3 (engine API)
INTENT_FEATURES = ("magnitude", "hits", "carries_damage", "announced_total", "announced_per_hit")


# ---------------------------------------------------------------- v3 additions
FEATURE_SCHEMA_V3 = "crl.features/v3"
PILES_V3 = PILES + ("pending", "enemy_held")
CARD_FEATURES_V3 = CARD_FEATURES + (
    "pending_autoplay", "pending_after_selection", "held_by_enemy", "parked_play",
    "parked_nested_autoplay", "parked_batch_autoplay", "batch_staged",
)
EXPECTED_COUNTERS = (
    "ascension_level", "max_energy", "max_potion_slots", "is_elite_combat", "is_elite_room", "fur_coat_bites",
    "end_turn_awaiting_selection", "pending_card_play", "parked_play_extra_plays", "parked_play_index",
    "parked_play_energy_spent", "nested_autoplay_depth", "autoplay_batch_pending",
    "autoplay_batch_resumes_turn_start", "autoplay_batch_resumes_end_turn", "stampede_remaining", "defer_card_rupture", "card_rupture_strength",
    "completed_card_rupture_strength", "afterimage_before_play", "storm_before_play", "subroutine_before_play",
    "serpent_form_snapshot_count", "serpent_form_snapshot_innermost", "status_cards_drawn_this_turn",
    "resolving_defend_card", "played_card_cost_for_combat_set", "played_card_cost_for_combat",
    "played_card_bonus_damage", "played_card_momentum_damage", "played_card_bonus_block",
    "played_card_enchant_spent", "played_card_enchant_grew", "played_card_cost_bump", "played_card_stars_spent",
    "lethal_attack_play", "end_turn_after_play", "energy_spent_this_turn", "orbit_energy_spent",
    "void_form_cards_played_this_turn", "cards_played_this_turn", "attack_or_skill_cards_played_this_turn",
    "card_plays_this_turn", "skill_cards_played_this_turn", "shivs_played_this_turn", "card_plays_last_turn",
    "cards_drawn_this_combat", "extra_cards_drawn_this_turn", "cards_generated_this_combat",
    "cards_exhausted_this_turn", "cards_discarded_this_turn", "ethereal_exhausts_this_turn",
    "ethereal_exhaust_count", "ethereal_card_plays_this_combat", "player_hp_lost_this_turn",
    "unblocked_damage_this_turn", "took_unblocked_damage", "lightning_orbs_channeled_this_combat",
    "doom_applied_this_turn", "osty_attacks_this_turn", "stars_gained_this_turn", "skill_played_while_smoggy",
    "pen_nib_armed", "vambrace_spent", "vambrace_claimed_by_play", "paels_legion_cooldown",
    "paels_legion_claimed_by_play", "rainbow_ring_attacks", "rainbow_ring_skills", "rainbow_ring_powers",
    "rainbow_ring_paid_this_turn", "unsettling_lamp_spent", "unsettling_lamp_card", "gambling_chip_discarded",
    "gambling_chip_owed", "burning_sticks_used", "belt_buckle_applied", "whispering_earring_spent",
    "music_box_used_this_turn",
)
EXPECTED_ENEMY_EXTRA = (
    "curl_up_pending", "possessed_player_strength", "possessed_player_dexterity", "steam_eruption_phase",
    "steam_eruption_damage", "nemesis_intangible_on", "at_player_back", "is_dampen_caster", "stolen_gold",
    "has_stolen_card", "escaped", "off_balance", "powered_hits_this_turn", "slot", "strangle_before_play",
    "has_secondary_intent", "is_parked_play_target", "is_parked_autoplay_target",
)
RESOURCE_FEATURES = ("stars", "orb_capacity", "base_orb_slots", "n_orbs", "osty_hp", "osty_max_hp", "osty_alive")
SELECTION_EXTRA_V3 = ("sel_from_potion", "sel_generated_upgraded", "sel_generated_retained", "sel_after_selection_cards")
GLOBAL_EXTRA_V3 = (GLOBAL_EXTRA + ("player_turn", "phase_selection") + RESOURCE_FEATURES + SELECTION_EXTRA_V3
                   + ("dampened_cards",) + tuple(f"ctr_{n}" for n in EXPECTED_COUNTERS))
GLOBAL_FEATURES_V3 = PLAYER_BASIC + PLAYER_HISTORY + GLOBAL_EXTRA_V3
ENEMY_EXTRA_V3 = (
    "curl_up_pending", "possessed_player_strength", "possessed_player_dexterity", "steam_eruption_about_to_blow",
    "steam_eruption_exploding", "steam_eruption_damage", "nemesis_intangible_on", "at_player_back",
    "is_dampen_caster", "stolen_gold", "has_stolen_card", "escaped", "off_balance", "powered_hits_this_turn",
    "has_slot", "slot", "strangle_before_play", "has_secondary_intent", "is_parked_play_target",
    "is_parked_autoplay_target",
)
ENEMY_FEATURES_V3 = ENEMY_FEATURES + ENEMY_EXTRA_V3
ORB_FEATURES = ("evoke", "passive", "position", "is_front")
# Where each v2-rejected side state goes in v3 (owner, column <- worker value).
SIDE_STATE_FOLDS_V3 = {
    "Orbit": "player: progress_1 <- energy spent mod 4 (payout every 4th), progress_2 <- total spent",
    "VoidForm": "player: progress_1 <- VoidForm's own plays this turn (free while < amount)",
    "PossessSpeed": "enemy: secondary_value <- Dexterity taken from the player (returned on death)",
    "PossessStrength": "enemy: secondary_value <- Strength taken from the player (returned on death)",
    "SteamEruption": "enemy: progress_1 <- phase (1 about to blow); damage/phase also enemy columns",
    "Nemesis": "enemy: progress_1 <- last flip applied Intangible (next flip removes it)",
    "Dampen": "player: has_source_relation <- a living Magi Knight caster, progress_1 <- downgraded cards; "
              "enemy is_dampen_caster column; per-card DampenedUpgrade stays a card feature",
    "Surrounded": "player: amount <- 1 (display), progress_1 <- facing Left (magnitude 2); "
                  "enemy at_player_back column (1.5x damage, and targeting it turns the player)",
    "CurlUp": "enemy: progress_1 <- a card's hit is recorded and its block lands when that card finishes",
    "Skittish": "enemy: no fold; the first-hit record is inert at every decision (ActiveAttackCommand == 0 "
                "checked by the worker), and SkittishSpent is its own row",
}
FLAG_COUNTERS = frozenset((
    "is_elite_combat", "is_elite_room", "fur_coat_bites", "end_turn_awaiting_selection", "pending_card_play",
    "defer_card_rupture", "resolving_defend_card", "played_card_cost_for_combat_set", "played_card_enchant_spent",
    "played_card_enchant_grew", "lethal_attack_play", "end_turn_after_play", "took_unblocked_damage",
    "doom_applied_this_turn", "skill_played_while_smoggy", "pen_nib_armed", "vambrace_spent",
    "autoplay_batch_pending", "autoplay_batch_resumes_turn_start", "autoplay_batch_resumes_end_turn",
    "vambrace_claimed_by_play", "paels_legion_claimed_by_play", "rainbow_ring_paid_this_turn",
    "unsettling_lamp_spent", "unsettling_lamp_card", "gambling_chip_owed", "burning_sticks_used",
    "belt_buckle_applied", "whispering_earring_spent", "music_box_used_this_turn",
))
FACING_RIGHT, FACING_LEFT = 1, 2  # RunConstants.FacingRight / FacingLeft


class UnsupportedState(Exception):
    """A visible state this adapter cannot encode faithfully."""


class ForeignCardState(UnsupportedState):
    """A card definition outside a restricted card vocabulary (e.g. the 214-card roster)."""


class EncoderLimitation(UnsupportedState):
    """A reachable engine state whose rule the engine implements but crl.features/v3 cannot encode."""


# Worker ``boundary_violations`` entries (worker/RlWorkerBuild08 PublicState.BoundaryViolations)
# that are NOT in-flight engine context but explicit crl.features/v3 encoder limitations of the
# build08 engine. The engine plays the actual rule; v3 has no feature for the state, so the
# decision is refused (episode status "unsupported") instead of being encoded wrongly, answered
# automatically, or removed from the card pool. They are reachable: Stratagem (engine card 470)
# is one of the 214 retained cards and can be generated in combat, so no template restriction is
# claimed. Not part of describe(), so v3 feature-schema digests do not depend on this text.
V3_ENCODER_LIMITATIONS = {
    "RoundNumber": "an extra turn left CombatState.RoundNumber behind Turn + 1 (round-keyed rules such as "
                   "Plating's decay read it); v3 encodes only Turn",
    "DupeCard": "a History Course dupe is visible; it leaves combat after its play, which a v3 card row cannot say",
    "HandDrawOwed": "Stratagem's screen paused the turn-start hand draw with draws still owed; the owed draws "
                    "are not a v3 feature",
    "HavocPlayTop": "Stratagem's screen paused Havoc; the owed top-card play is not a v3 feature",
}


@dataclass
class Obs:
    """One encoded decision state. Hand cards are the first ``n_hand`` card rows, in hand order."""

    glob: np.ndarray  # [G] float32
    sel_kind: int
    sel_source_card: int
    card_idx: np.ndarray  # [C] int32 (vocab index)
    card_ench: np.ndarray  # [C] int32 (enchantment value + 1; 0 padding)
    card_feat: np.ndarray  # [C, Fc] float32
    card_pile: np.ndarray  # [C] int8 (index into PILES)
    n_hand: int
    eff_idx: np.ndarray  # [E] int32 (BuffId + 1)
    eff_feat: np.ndarray  # [E, 10] float32
    eff_owner: np.ndarray  # [E] int16 (0 = player, 1 + j = enemy j)
    enemy_idx: np.ndarray  # [M] int32
    enemy_feat: np.ndarray  # [M, Fe] float32
    intent_type: np.ndarray  # [M, 3] int32 (IntentType + 1; 0 none)
    intent_feat: np.ndarray  # [M, 3, Fi] float32
    relic_idx: np.ndarray  # [R] int32
    relic_feat: np.ndarray  # [R, 1] float32
    potion_idx: np.ndarray  # [P] int32 (held potions only)
    opt_kind: np.ndarray  # [O] int32
    opt_hand: np.ndarray  # [O] int32 (-1 none)
    opt_target: np.ndarray  # [O] int32 (-1 none)
    opt_feat: np.ndarray  # [O, Fo] float32
    opt_card_idx: np.ndarray  # [O] int32 (select candidates; 0 otherwise)
    opt_card_ench: np.ndarray  # [O] int32
    opt_card_feat: np.ndarray  # [O, Fc] float32
    out_of_roster_cards: int = 0
    orb_idx: np.ndarray | None = None  # v3: [Q] int32 (OrbType + 1)
    orb_feat: np.ndarray | None = None  # v3: [Q, len(ORB_FEATURES)] float32
    # crl.features/v3-potion-v2 only (crl.potion_v1); None for every other schema.
    opt_potion_idx: np.ndarray | None = None  # [O] int32: candidate potion identity (0 = not a potion use)
    opt_potion_slot: np.ndarray | None = None  # [O] int32: physical inventory slot (-1 = not a potion use)
    sel_source_potion: int | None = None  # potion identity of an open potion selection screen (0 = none)
    # crl.features/v3-potion-v2 policy admissibility ([O] bool; None = every legal option admissible).
    # A legal option marked False stays legal in the game and keeps its engine ordinal; the policy
    # never scores, explores or bootstraps through it.
    opt_admissible: np.ndarray | None = None

    @property
    def n_options(self) -> int:
        return len(self.opt_kind)


class Adapter:
    def __init__(self, vocab: Vocab, hello: dict, schema: str | None = None):
        if tuple(hello["card_fields"]) != EXPECTED_CARD_FIELDS:
            raise UnsupportedState(f"CardInstance fields changed: {hello['card_fields']}")
        if tuple(hello.get("player_history_fields", ())) != PLAYER_HISTORY:
            raise UnsupportedState("player history contract mismatch; rebuild the v2 worker")
        if schema is None:
            schema = "v3" if getattr(vocab, "schema", "") in V3_SCHEMAS else "v2"
        if schema not in ("v2", "v3"):
            raise ValueError(f"unknown feature schema {schema}")
        if (schema == "v3") != (getattr(vocab, "schema", "") in V3_SCHEMAS):
            raise ValueError(f"feature schema {schema} needs the matching vocabulary, not {vocab.schema}")
        self.schema = schema
        self.v3 = schema == "v3"
        self.v = vocab
        self.restricted_cards = getattr(vocab, "schema", "") == VOCAB_SCHEMA_V3_214
        self.card_entries = {int(i): e for i, e in hello.get("engine_cards", ())}  # error messages only
        self.buff_names = hello["buff_ids"]
        self.ench_values = set(hello["enchantments"].values())
        self.none_card_type = hello["card_types"]["None"]
        self.none_rider = hello["tinker_riders"]["None"]
        self.unmapped_ids = set() if self.v3 else {
            self.buff_names.index(n) for n in UNMAPPED_SIDE_STATE if n in self.buff_names}
        self.helper_ids = {self.buff_names.index(h): (self.buff_names.index(p), c) for h, (p, c) in HELPERS.items()}
        self.duration_ids = {self.buff_names.index(n) for n in DURATION_DEBUFFS}
        self.ids = {n: i for i, n in enumerate(self.buff_names)}
        self.card_features = CARD_FEATURES_V3 if self.v3 else CARD_FEATURES
        self.piles = PILES_V3 if self.v3 else PILES
        self.global_features = GLOBAL_FEATURES_V3 if self.v3 else GLOBAL_FEATURES
        self.global_extra = GLOBAL_EXTRA_V3 if self.v3 else GLOBAL_EXTRA
        self.enemy_features = ENEMY_FEATURES_V3 if self.v3 else ENEMY_FEATURES
        self.hello_v3 = {}
        # Hooks for crl.potion_v1's subclass; the values here are the v2/v3 contract unchanged.
        self.option_kinds = OPTION_KINDS
        self.encoded_boundaries: frozenset[str] = frozenset()
        if self.v3:
            if tuple(hello.get("player_counter_fields", ())) != EXPECTED_COUNTERS:
                raise UnsupportedState("worker player counter contract differs from crl.features/v3")
            if tuple(hello.get("enemy_extra_fields", ())) != EXPECTED_ENEMY_EXTRA:
                raise UnsupportedState("worker enemy extra contract differs from crl.features/v3")
            for name in SIDE_STATE_FOLDS_V3:
                if name not in self.ids:
                    raise UnsupportedState(f"BuffId {name} missing from the worker codebook")
            self.orb_types = list(hello["orb_types"])
            self.hello_v3 = {
                "state_field_classes": hello["state_field_classes"],
                "orb_types": self.orb_types,
                "magi_knight_def_id": hello["magi_knight_def_id"],
                "history_course_relic_id": hello["history_course_relic_id"],
            }

    # ------------------------------------------------------------------ cards
    def _card_idx(self, def_id: int) -> int:
        if def_id <= 0:
            raise UnsupportedState(f"card DefId {def_id} (non-positive ids are not mapped)")
        try:
            return self.v.card_index[def_id]
        except KeyError:
            if self.restricted_cards:
                raise ForeignCardState(f"foreign card DefId {def_id} {self.card_entries.get(def_id, '?')} "
                                       "outside the 214-card roster") from None
            raise UnsupportedState(f"card DefId {def_id} not in catalog") from None

    def card_row(self, raw: list[float]) -> tuple[int, int, np.ndarray]:
        r = raw
        if len(r) != len(EXPECTED_CARD_FIELDS):
            raise UnsupportedState(f"card row has {len(r)} fields")
        if int(r[F["Enchantment"]]) not in self.ench_values:
            raise UnsupportedState(f"enchantment value {r[F['Enchantment']]}")

        def opt(name: str) -> tuple[float, float]:
            x = r[F[name]]
            return (0.0, 0.0) if x == INT_MIN else (1.0, float(slog(x)))

        cfc_set, cfc = opt("CostForCombat")
        ctt_set, ctt = opt("CostThisTurn")
        cot_set, cot = opt("CostOverrideThisTurn")
        f = np.zeros(len(self.card_features), dtype=np.float32)
        f[:30] = (
            r[F["Upgraded"]], r[F["FreeThisTurn"]], r[F["Retain"]], slog(r[F["EnchantAmount"]]),
            slog(r[F["CostBump"]]), slog(r[F["ReplayCount"]]), r[F["EnchantSpent"]], cfc_set, cfc,
            slog(r[F["BonusDamage"]]), slog(r[F["MomentumDamage"]]),
            float(r[F["TinkerType"]] != self.none_card_type), r[F["TinkerType"]] / 10.0,
            (r[F["TinkerRider"]] - self.none_rider) / 10.0,
            r[F["PlayedThisTurn"]], r[F["SlyThisTurn"]], r[F["SlyForCombat"]], r[F["EtherealForCombat"]],
            r[F["RetainForCombat"]], r[F["DampenedUpgrade"]], r[F["Bound"]], r[F["RetainThisTurn"]],
            ctt_set, ctt, r[F["FreeUntilPlayed"]], slog(r[F["BonusBlock"]]), cot_set, cot,
            slog(r[F["CostOverrideBaseline"]]), r[F["CostOverrideSetUpgraded"]],
        )
        return self._card_idx(int(r[F["DefId"]])), int(r[F["Enchantment"]]) + 1, f

    # ---------------------------------------------------------------- effects
    def _effect_rows(self, buffs: list[list[int]], owner: int, ctx: dict) -> list[tuple[int, list[float]]]:
        """Fold helpers, then build the 10 fields for one owner's BuffState list."""
        player = owner == 0
        ids = self.ids
        logical: list[list] = []  # [buff_id, row dict]
        by_id: dict[int, dict] = {}
        helpers: list[tuple[int, int]] = []
        for raw_position, (bid, mag) in enumerate(buffs):
            if bid < 0 or bid >= len(self.buff_names):
                raise UnsupportedState(f"BuffId {bid} outside the codebook")
            if bid in self.unmapped_ids:
                raise UnsupportedState(
                    f"effect {self.buff_names[bid]} has unmapped side state ({UNMAPPED_SIDE_STATE[self.buff_names[bid]]})")
            if bid == ids["TheBombDamage"]:
                # The engine stores each Bomb timer immediately before its own damage row.
                if raw_position == 0 or buffs[raw_position - 1][0] != ids["TheBombPower"]:
                    raise UnsupportedState("TheBombDamage without its adjacent timer instance")
                logical[-1][1]["secondary_value"] = mag
                continue
            if bid in self.helper_ids:
                helpers.append((bid, mag))
                continue
            row = {"amount": mag}
            logical.append([bid, row])
            # Repeated ids stay separate rows; only a helper fold onto them is ambiguous.
            by_id[bid] = None if bid in by_id else row
        for hid, mag in helpers:
            parent, col = self.helper_ids[hid]
            if by_id.get(parent) is None:
                raise UnsupportedState(
                    f"helper {self.buff_names[hid]} without exactly one {self.buff_names[parent]} row")
            if col in by_id[parent]:
                raise UnsupportedState(f"two {self.buff_names[hid]} helper rows")
            by_id[parent][col] = mag

        aux = ctx["aux"]
        if player:
            start = {b: m for b, m in ctx["debuffs_at_round_start"]}
            for bid, row in logical:
                name = self.buff_names[bid]
                if bid in self.duration_ids:
                    row["secondary_value"] = start.get(bid, 0)
                    row["skip_next_duration_tick"] = float(row["amount"] >= 0 and bid not in start)
                elif name == "ToricToughness":
                    row["secondary_value"] = aux["toric_toughness_block"]
                elif name == "Sloth":
                    row["progress_1"] = aux["sloth_cards_played_this_turn"]
                elif name == "Tender":
                    row["progress_1"] = aux["tender_cards_played_this_turn"]
                elif name == "ChainsOfBinding":
                    row["progress_1"] = aux["bound_cards_this_turn"]
                    row["progress_2"] = float(aux["bound_card_played_this_turn"])
                elif name == "PanachePower":
                    row["progress_1"] = aux["cards_played_since_panache_proc"]
                    row.setdefault("progress_2", 0)
                elif name == "AutomationPower":
                    row["progress_1"] = aux["drawn_cards_since_automation_proc"]
                elif self.v3 and name == "Orbit":
                    row["progress_1"] = aux["orbit_energy_spent"] % 4
                    row["progress_2"] = aux["orbit_energy_spent"]
                elif self.v3 and name == "VoidForm":
                    row["progress_1"] = aux["void_form_cards_played_this_turn"]
                elif self.v3 and name == "Dampen":
                    row["has_source_relation"] = float(aux["dampen_caster_alive"])
                    row["progress_1"] = aux["dampened_cards"]
                elif self.v3 and name == "Surrounded":
                    if row["amount"] not in (FACING_RIGHT, FACING_LEFT):
                        raise UnsupportedState(f"Surrounded facing {row['amount']} is neither Right nor Left")
                    row["progress_1"] = float(row["amount"] == FACING_LEFT)
                    row["amount"] = 1  # SurroundedPower's displayed Amount; the facing is progress_1
                if name in ("Inferno", "CrimsonMantleBlock", "TheBombPower"):
                    row.setdefault("secondary_value", 0)
                if name == "Hex":
                    row["has_source_relation"] = float(aux["hex_applier_alive"])
                elif name == "Shrink":
                    row["has_source_relation"] = float(aux["shrink_applier_alive"])
        else:
            enemy = ctx["enemy"]
            extra = ctx.get("extra")
            for bid, row in logical:
                name = self.buff_names[bid]
                if name == "HardenedShell":
                    row["secondary_value"] = enemy["hardened_shell_hp_lost_this_side"]
                elif extra is None:
                    continue
                elif name == "CurlUp":
                    row["progress_1"] = extra["curl_up_pending"]
                elif name == "PossessStrength":
                    row["secondary_value"] = extra["possessed_player_strength"]
                elif name == "PossessSpeed":
                    row["secondary_value"] = extra["possessed_player_dexterity"]
                elif name == "SteamEruption":
                    row["progress_1"] = extra["steam_eruption_phase"]
                elif name == "Nemesis":
                    row["progress_1"] = extra["nemesis_intangible_on"]

        n = len(logical)
        out = []
        for rank, (bid, row) in enumerate(logical):
            sec, p1, p2 = row.get("secondary_value"), row.get("progress_1"), row.get("progress_2")
            out.append((bid + 1, [
                float(slog(row["amount"])),
                float(slog(sec)) if sec is not None else 0.0,
                float(slog(p1)) if p1 is not None else 0.0,
                float(slog(p2)) if p2 is not None else 0.0,
                row.get("skip_next_duration_tick", 0.0),
                row.get("has_source_relation", 0.0),
                rank / max(n - 1, 1),
                float(sec is not None), float(p1 is not None), float(p2 is not None),
            ]))
        return out

    # ------------------------------------------------------------ observation
    def _check_v3_boundary(self, obs: dict) -> None:
        if obs.get("character") != "Ironclad":
            raise UnsupportedState(f"character {obs.get('character')} (crl.features/v3 is Ironclad-only)")
        violations = [v for v in obs["boundary_violations"] if v not in self.encoded_boundaries]
        in_flight = [v for v in violations if v not in V3_ENCODER_LIMITATIONS]
        if in_flight:
            raise UnsupportedState(f"decision inside an engine call: {violations}")
        if violations:
            raise EncoderLimitation("crl.features/v3 encoder limitation (engine rule implemented, state not encodable): "
                                    + "; ".join(f"{v}: {V3_ENCODER_LIMITATIONS[v]}" for v in violations))
        if obs["history_course_encore_pending"]:
            raise EncoderLimitation("crl.features/v3 encoder limitation: History Course encore pending "
                                    "(its remembered card is not encoded)")

    def encode(self, obs: dict, entry_hp: int) -> Obs:
        v = self.v
        p = obs["player"]
        view = obs["view"]
        aux = p["effect_aux"]
        v3 = self.v3
        if v3:
            self._check_v3_boundary(obs)
        Fc = len(self.card_features)

        # Cards: hand first (in hand order, so option hand_index == card row), then the rest.
        card_idx, card_ench, card_feat, card_pile = [], [], [], []
        if len(view["hand"]) != len(obs["hand"]):
            raise UnsupportedState("hand/view length mismatch")
        oor = 0
        for i, raw in enumerate(obs["hand"]):
            ci, ce, f = self.card_row(raw)
            hv = view["hand"][i]
            if hv["def_id"] != int(raw[0]):
                raise UnsupportedState("hand/view card mismatch")
            f[30], f[31], f[32] = 1.0, slog(hv["effective_cost"]), float(hv["playable"])
            card_idx.append(ci); card_ench.append(ce); card_feat.append(f); card_pile.append(0)
        top, bottom = obs["draw_known_top"], obs["draw_known_bottom"]
        for i, raw in enumerate(top):
            ci, ce, f = self.card_row(raw)
            f[33], f[34] = 1.0, 1.0 / (1 + i)
            card_idx.append(ci); card_ench.append(ce); card_feat.append(f); card_pile.append(1)
        for j, raw in enumerate(bottom):
            ci, ce, f = self.card_row(raw)
            f[35], f[36] = 1.0, 1.0 / (len(bottom) - j)
            card_idx.append(ci); card_ench.append(ce); card_feat.append(f); card_pile.append(1)
        for pile_name, pile_id in (("draw_unknown_sorted", 1), ("discard", 2), ("exhaust", 3), ("incoming", 4)):
            for raw in obs[pile_name]:
                ci, ce, f = self.card_row(raw)
                card_idx.append(ci); card_ench.append(ce); card_feat.append(f); card_pile.append(pile_id)
        sel = obs["selection"]
        if v3:
            extra_piles = [(raw, 5, 40) for raw in obs["parked_play_card"]]
            extra_piles += [(raw, 5, 41) for raw in obs["parked_nested_autoplays"]]
            extra_piles += [(raw, 5, 42) for raw in obs["parked_batch_autoplay"]]
            extra_piles += [(raw, 5, 43) for raw in obs["batch_staged"]]
            extra_piles += [(raw, 5, 37) for raw in obs["autoplay_queue"]]
            if sel is not None:
                extra_piles += [(raw, 5, 38) for raw in sel["after_selection_to_hand"]]
            extra_piles += [(e["stolen_card"], 6, 39) for e in obs["enemies"] if e["stolen_card"] is not None]
            for raw, pile_id, col in extra_piles:
                ci, ce, f = self.card_row(raw)
                f[col] = 1.0
                card_idx.append(ci); card_ench.append(ce); card_feat.append(f); card_pile.append(pile_id)
        if not v3 or v.roster_card_ids:
            for ci in card_idx:
                if v.card_ids[ci - 1] not in v.roster_card_ids:
                    oor += 1

        # Enemies.
        enemies = obs["enemies"]
        if len(view["enemies"]) != len(enemies):
            raise UnsupportedState("enemy/view length mismatch")
        M = len(enemies)
        n_base = len(ENEMY_FEATURES)
        enemy_idx = np.zeros(M, dtype=np.int32)
        enemy_feat = np.zeros((M, len(self.enemy_features)), dtype=np.float32)
        intent_type = np.zeros((M, MAX_DECLARED_INTENTS), dtype=np.int32)
        intent_feat = np.zeros((M, MAX_DECLARED_INTENTS, len(INTENT_FEATURES)), dtype=np.float32)
        eff: list[tuple[int, list[float], int]] = []
        for bid, row in self._effect_rows(p["buffs"], 0, {"aux": aux, "debuffs_at_round_start": p["debuffs_at_round_start"]}):
            eff.append((bid, row, 0))
        for j, (e, ev) in enumerate(zip(enemies, view["enemies"])):
            if e["def_id"] != ev["def_id"]:
                raise UnsupportedState("enemy/view mismatch")
            try:
                enemy_idx[j] = v.enemy_index[e["def_id"]]
            except KeyError:
                raise UnsupportedState(f"enemy {e['def_id']} not in catalog") from None
            intents = ev["declared_intents"]
            if len(intents) > MAX_DECLARED_INTENTS:
                raise UnsupportedState("more declared intents than the engine API exposes")
            enemy_feat[j, :n_base] = (
                slog(e["hp"]), slog(e["max_hp"]), e["hp"] / max(e["max_hp"], 1), slog(e["block"]),
                float(ev["targetable"]), float(e["hp"] > 0), float(aux["hex_applier"] == j),
                float(aux["shrink_applier"] == j), j / max(M - 1, 1), len(intents),
            )
            extra = None
            if v3:
                if len(e["extra"]) != len(EXPECTED_ENEMY_EXTRA):
                    raise UnsupportedState("enemy extra length mismatch")
                extra = dict(zip(EXPECTED_ENEMY_EXTRA, e["extra"]))
                if extra["steam_eruption_phase"] not in (0, 1, 2):
                    raise UnsupportedState(f"Steam Eruption phase {extra['steam_eruption_phase']}")
                if bool(extra["has_stolen_card"]) != (e["stolen_card"] is not None):
                    raise UnsupportedState("stolen card flag/row mismatch")
                enemy_feat[j, n_base:] = (
                    extra["curl_up_pending"], slog(extra["possessed_player_strength"]),
                    slog(extra["possessed_player_dexterity"]), float(extra["steam_eruption_phase"] == 1),
                    float(extra["steam_eruption_phase"] == 2), slog(extra["steam_eruption_damage"]),
                    extra["nemesis_intangible_on"], extra["at_player_back"], extra["is_dampen_caster"],
                    slog(extra["stolen_gold"]), extra["has_stolen_card"], extra["escaped"], extra["off_balance"],
                    slog(extra["powered_hits_this_turn"]), float(extra["slot"] >= 0),
                    max(extra["slot"], 0) / 5.0, slog(extra["strangle_before_play"]), extra["has_secondary_intent"],
                    extra["is_parked_play_target"], extra["is_parked_autoplay_target"],
                )
            for k, it in enumerate(intents):
                intent_type[j, k] = self._intent(it["type"]) + 1
                tot = it.get("announced_total_damage")
                intent_feat[j, k] = (
                    slog(it["magnitude"]), slog(it["hits"]), float(tot is not None),
                    slog(tot) if tot is not None else 0.0,
                    slog(it["announced_damage_per_hit"]) if tot is not None else 0.0,
                )
            for bid, row in self._effect_rows(e["buffs"], j + 1, {"aux": aux, "enemy": e, "extra": extra}):
                eff.append((bid, row, j + 1))

        # Relics / potions.
        relic_idx, relic_feat = [], []
        for rid, counter in obs["relics"]:
            if rid not in v.relic_index:
                raise UnsupportedState(f"relic {rid} not in catalog")
            relic_idx.append(v.relic_index[rid]); relic_feat.append([float(slog(counter))])
        potions = [pid for pid in obs["potions"] if pid != 0]
        for pid in potions:
            if pid not in v.potion_index:
                raise UnsupportedState(f"potion {pid} not in catalog")

        # Global scalars.
        hist = p["history"]
        if len(hist) != len(PLAYER_HISTORY):
            raise UnsupportedState("history length mismatch")
        glob = [slog(p["hp"]), slog(p["max_hp"]), slog(p["block"]), slog(p["energy"]),
                slog(p["energy_capacity"]), slog(p["gold"]), slog(obs["turn"])]
        glob += [float(h) if i in (4, 5) else float(slog(h)) for i, h in enumerate(hist)]
        glob += [slog(entry_hp), p["hp"] / max(p["max_hp"], 1), p["hp"] / entry_hp, float(sel is not None),
                 slog(sel["amount"]) if sel else 0.0, slog(sel["selected_count"]) if sel else 0.0,
                 float(sel["skippable"]) if sel else 0.0, slog(sel["candidate_count"]) if sel else 0.0,
                 slog(len(potions))]
        orb_idx = orb_feat = None
        if v3:
            res = p["resources"]
            orbs = res["orbs"]
            glob += [float(obs["player_turn"]), float(obs["phase"] == "selection"),
                     slog(res["stars"]), slog(res["orb_capacity"]), slog(res["base_orb_slots"]), slog(len(orbs)),
                     slog(res["osty_hp"]), slog(res["osty_max_hp"]), float(res["osty_hp"] > 0),
                     float(bool(sel) and sel["source_potion_id"] != 0), float(bool(sel) and sel["generated_upgraded"]),
                     float(bool(sel) and sel["generated_retained"]),
                     slog(len(sel["after_selection_to_hand"])) if sel else 0.0, slog(aux["dampened_cards"])]
            ctr = p["counters"]
            if len(ctr) != len(EXPECTED_COUNTERS):
                raise UnsupportedState("counter length mismatch")
            glob += [float(x) if name in FLAG_COUNTERS else float(slog(x)) for name, x in zip(EXPECTED_COUNTERS, ctr)]
            Q = len(orbs)
            orb_idx = np.zeros(Q, dtype=np.int32)
            orb_feat = np.zeros((Q, len(ORB_FEATURES)), dtype=np.float32)
            for q, (otype, evoke, passive) in enumerate(orbs):
                if not 0 <= otype < len(self.orb_types):
                    raise UnsupportedState(f"orb type {otype}")
                orb_idx[q] = otype + 1
                orb_feat[q] = (slog(evoke), slog(passive), q / max(Q - 1, 1), float(q == 0))
        glob = np.array(glob, dtype=np.float32)
        if len(glob) != len(self.global_features):
            raise AssertionError("global feature layout drifted")
        sel_kind = sel["kind"] + 1 if sel else 0
        sel_src = self._card_idx(sel["source_card_def_id"]) if sel and sel["source_card_def_id"] > 0 else 0

        # Options.
        legal = obs["legal"]
        O = len(legal)
        opt_kind = np.zeros(O, dtype=np.int32)
        opt_hand = np.full(O, -1, dtype=np.int32)
        opt_target = np.full(O, -1, dtype=np.int32)
        opt_feat = np.zeros((O, len(OPTION_FEATURES)), dtype=np.float32)
        opt_card_idx = np.zeros(O, dtype=np.int32)
        opt_card_ench = np.zeros(O, dtype=np.int32)
        opt_card_feat = np.zeros((O, Fc), dtype=np.float32)
        n_hand = len(obs["hand"])
        for i, o in enumerate(legal):
            if o["o"] != i:
                raise UnsupportedState("option ordinal mismatch")
            kind = o["kind"]
            if kind not in self.option_kinds:
                raise UnsupportedState(f"option kind {kind}")
            opt_kind[i] = self.option_kinds.index(kind)
            if kind == "play_card":
                if not 0 <= o["hand_index"] < n_hand:
                    raise UnsupportedState("hand index out of range")
                opt_hand[i] = o["hand_index"]
            if o["target"] >= 0:
                if o["target"] >= M:
                    raise UnsupportedState("target index out of range")
                opt_target[i] = o["target"]
                opt_feat[i, 1] = 1.0
            if o.get("note") == "no_targetable_enemy_engine_auto_target":
                opt_feat[i, 0] = 1.0
            if kind == "select":
                opt_feat[i, 2 + ("hand", "discard", "draw", "generated").index(o["pile"])] = 1.0
                ci, ce, f = self._select_candidate_row(o)
                opt_card_idx[i], opt_card_ench[i], opt_card_feat[i] = ci, ce, f

        E = len(eff)
        return Obs(
            glob=glob, sel_kind=sel_kind, sel_source_card=sel_src,
            card_idx=np.array(card_idx, dtype=np.int32), card_ench=np.array(card_ench, dtype=np.int32),
            card_feat=np.array(card_feat, dtype=np.float32).reshape(len(card_idx), Fc),
            card_pile=np.array(card_pile, dtype=np.int8), n_hand=n_hand,
            eff_idx=np.array([e[0] for e in eff], dtype=np.int32),
            eff_feat=np.array([e[1] for e in eff], dtype=np.float32).reshape(E, len(EFFECT_FIELDS)),
            eff_owner=np.array([e[2] for e in eff], dtype=np.int16),
            enemy_idx=enemy_idx, enemy_feat=enemy_feat, intent_type=intent_type, intent_feat=intent_feat,
            relic_idx=np.array(relic_idx, dtype=np.int32),
            relic_feat=np.array(relic_feat, dtype=np.float32).reshape(len(relic_idx), 1),
            potion_idx=np.array([v.potion_index[x] for x in potions], dtype=np.int32),
            opt_kind=opt_kind, opt_hand=opt_hand, opt_target=opt_target, opt_feat=opt_feat,
            opt_card_idx=opt_card_idx, opt_card_ench=opt_card_ench, opt_card_feat=opt_card_feat,
            out_of_roster_cards=oor, orb_idx=orb_idx, orb_feat=orb_feat,
        )

    def _select_candidate_row(self, o: dict) -> tuple[int, int, np.ndarray]:
        """Encoding of one selection candidate's card (hook; v2/v3: exactly ``card_row``)."""
        return self.card_row(o["card"])

    def _intent(self, name: str) -> int:
        try:
            return self.v.intent_types.index(name)
        except ValueError:
            raise UnsupportedState(f"intent type {name}") from None

    @property
    def dims(self) -> dict:
        """Input widths the network needs (QNet ``feature_dims``)."""
        return {
            "card": len(self.card_features), "piles": len(self.piles), "global_extra": len(self.global_extra),
            "enemy": len(self.enemy_features), "orbs": len(ORB_FEATURES) if self.v3 else 0,
            "orb_types": len(self.orb_types) + 1 if self.v3 else 0,
        }

    def describe(self) -> dict:
        if not self.v3:  # the v2 document is unchanged, so its digest is too
            return {
                "schema": FEATURE_SCHEMA,
                "card_fields": list(EXPECTED_CARD_FIELDS),
                "card_features": list(CARD_FEATURES),
                "global_features": list(GLOBAL_FEATURES),
                "effect_fields": list(EFFECT_FIELDS),
                "helpers_folded": HELPERS,
                "duration_debuffs": list(DURATION_DEBUFFS),
                "unmapped_side_state_rejected": UNMAPPED_SIDE_STATE,
                "enemy_features": list(ENEMY_FEATURES),
                "intent_features": list(INTENT_FEATURES),
                "option_kinds": list(OPTION_KINDS),
                "option_features": list(OPTION_FEATURES),
                "piles": list(PILES),
                "n_enchantments": max(self.ench_values) + 2,
            }
        return {
            "schema": FEATURE_SCHEMA_V3,
            "card_fields": list(EXPECTED_CARD_FIELDS),
            "card_features": list(CARD_FEATURES_V3),
            "global_features": list(GLOBAL_FEATURES_V3),
            "flag_counters": sorted(FLAG_COUNTERS),
            "effect_fields": list(EFFECT_FIELDS),
            "helpers_folded": HELPERS,
            "duration_debuffs": list(DURATION_DEBUFFS),
            "side_state_folded": SIDE_STATE_FOLDS_V3,
            "enemy_features": list(ENEMY_FEATURES_V3),
            "intent_features": list(INTENT_FEATURES),
            "orb_features": list(ORB_FEATURES),
            "option_kinds": list(OPTION_KINDS),
            "option_features": list(OPTION_FEATURES),
            "piles": list(PILES_V3),
            "n_enchantments": max(self.ench_values) + 2,
            "dims": self.dims,
            "worker_contract": self.hello_v3,
            "rejected": ["worker boundary_violations", "history_course_encore_pending", "non-Ironclad character"],
        }


# ------------------------------------------------------------------ batching
def collate(batch: list[Obs], device=None) -> dict:
    """Pad to the batch's own maxima; every padded row is masked."""
    import torch

    B = len(batch)
    C = max(1, max(len(o.card_idx) for o in batch))
    E = max(1, max(len(o.eff_idx) for o in batch))
    M = max(1, max(len(o.enemy_idx) for o in batch))
    R = max(1, max(len(o.relic_idx) for o in batch))
    P = max(1, max(len(o.potion_idx) for o in batch))
    O = max(1, max(o.n_options for o in batch))
    Fc = batch[0].card_feat.shape[1]
    Fo = batch[0].opt_feat.shape[1]
    Fe = batch[0].enemy_feat.shape[1]
    Fi = batch[0].intent_feat.shape[2]
    T = batch[0].intent_type.shape[1] if batch[0].intent_type.ndim == 2 else MAX_DECLARED_INTENTS

    glob = np.stack([o.glob for o in batch])
    sel_kind = np.array([o.sel_kind for o in batch], dtype=np.int64)
    sel_src = np.array([o.sel_source_card for o in batch], dtype=np.int64)
    card_idx = np.zeros((B, C), np.int64); card_ench = np.zeros((B, C), np.int64)
    card_feat = np.zeros((B, C, Fc), np.float32); card_pile = np.full((B, C), -1, np.int64)
    eff_idx = np.zeros((B, E), np.int64); eff_feat = np.zeros((B, E, len(EFFECT_FIELDS)), np.float32)
    eff_owner = np.full((B, E), -1, np.int64)
    enemy_idx = np.zeros((B, M), np.int64); enemy_feat = np.zeros((B, M, Fe), np.float32)
    enemy_mask = np.zeros((B, M), bool)
    intent_type = np.zeros((B, M, T), np.int64); intent_feat = np.zeros((B, M, T, Fi), np.float32)
    relic_idx = np.zeros((B, R), np.int64); relic_feat = np.zeros((B, R, 1), np.float32)
    potion_idx = np.zeros((B, P), np.int64)
    opt_kind = np.zeros((B, O), np.int64); opt_hand = np.full((B, O), -1, np.int64)
    opt_target = np.full((B, O), -1, np.int64); opt_feat = np.zeros((B, O, Fo), np.float32)
    opt_card_idx = np.zeros((B, O), np.int64); opt_card_ench = np.zeros((B, O), np.int64)
    opt_card_feat = np.zeros((B, O, Fc), np.float32); opt_mask = np.zeros((B, O), bool)
    for b, o in enumerate(batch):
        c, e, m, r, p, n = len(o.card_idx), len(o.eff_idx), len(o.enemy_idx), len(o.relic_idx), len(o.potion_idx), o.n_options
        card_idx[b, :c] = o.card_idx; card_ench[b, :c] = o.card_ench; card_feat[b, :c] = o.card_feat
        card_pile[b, :c] = o.card_pile
        eff_idx[b, :e] = o.eff_idx; eff_feat[b, :e] = o.eff_feat; eff_owner[b, :e] = o.eff_owner
        enemy_idx[b, :m] = o.enemy_idx; enemy_feat[b, :m] = o.enemy_feat; enemy_mask[b, :m] = True
        intent_type[b, :m] = o.intent_type; intent_feat[b, :m] = o.intent_feat
        relic_idx[b, :r] = o.relic_idx; relic_feat[b, :r] = o.relic_feat
        potion_idx[b, :p] = o.potion_idx
        opt_kind[b, :n] = o.opt_kind; opt_hand[b, :n] = o.opt_hand; opt_target[b, :n] = o.opt_target
        opt_feat[b, :n] = o.opt_feat; opt_card_idx[b, :n] = o.opt_card_idx
        opt_card_ench[b, :n] = o.opt_card_ench; opt_card_feat[b, :n] = o.opt_card_feat
        opt_mask[b, :n] = True
    orbs = {}
    if batch[0].orb_idx is not None:  # v3: every observation in a batch shares one schema
        if any(o.orb_idx is None for o in batch):
            raise ValueError("cannot collate v2 and v3 observations together")
        Q = max(1, max(len(o.orb_idx) for o in batch))
        Fq = batch[0].orb_feat.shape[1]
        orb_idx = np.zeros((B, Q), np.int64); orb_feat = np.zeros((B, Q, Fq), np.float32)
        for b, o in enumerate(batch):
            q = len(o.orb_idx)
            orb_idx[b, :q] = o.orb_idx; orb_feat[b, :q] = o.orb_feat
        orbs = dict(orb_idx=orb_idx, orb_feat=orb_feat)
    elif any(o.orb_idx is not None for o in batch):
        raise ValueError("cannot collate v2 and v3 observations together")
    potions = {}
    if batch[0].opt_potion_idx is not None:  # crl.features/v3-potion-v2
        if any(o.opt_potion_idx is None or o.sel_source_potion is None for o in batch):
            raise ValueError("cannot collate potion and non-potion observations together")
        opt_potion_idx = np.zeros((B, O), np.int64)
        for b, o in enumerate(batch):
            opt_potion_idx[b, :o.n_options] = o.opt_potion_idx
        potions = dict(opt_potion_idx=opt_potion_idx,
                       sel_src_potion=np.array([o.sel_source_potion for o in batch], dtype=np.int64))
    elif any(o.opt_potion_idx is not None for o in batch):
        raise ValueError("cannot collate potion and non-potion observations together")
    if batch[0].opt_admissible is not None:  # crl.features/v3-potion-v2 policy mask
        if any(o.opt_admissible is None for o in batch):
            raise ValueError("cannot collate observations with and without an admissibility mask")
        opt_admissible = np.zeros((B, O), bool)
        for b, o in enumerate(batch):
            opt_admissible[b, :o.n_options] = o.opt_admissible
        potions["opt_admissible"] = opt_admissible
    elif any(o.opt_admissible is not None for o in batch):
        raise ValueError("cannot collate observations with and without an admissibility mask")
    out = dict(
        glob=glob, sel_kind=sel_kind, sel_src=sel_src, card_idx=card_idx, card_ench=card_ench,
        card_feat=card_feat, card_pile=card_pile, eff_idx=eff_idx, eff_feat=eff_feat, eff_owner=eff_owner,
        enemy_idx=enemy_idx, enemy_feat=enemy_feat, enemy_mask=enemy_mask, intent_type=intent_type,
        intent_feat=intent_feat, relic_idx=relic_idx, relic_feat=relic_feat, potion_idx=potion_idx,
        opt_kind=opt_kind, opt_hand=opt_hand, opt_target=opt_target, opt_feat=opt_feat,
        opt_card_idx=opt_card_idx, opt_card_ench=opt_card_ench, opt_card_feat=opt_card_feat, opt_mask=opt_mask,
        **orbs, **potions,
    )
    return {k: torch.from_numpy(np.ascontiguousarray(x)).to(device) if device else torch.from_numpy(np.ascontiguousarray(x))
            for k, x in out.items()}

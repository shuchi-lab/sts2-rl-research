"""Identity-free, versioned shared mechanics vocabulary.

The categorical values describe mechanics; card names and IDs are lookup metadata only.
Numeric values use signed log1p. Exact zero and absence have separate masks.
"""
from __future__ import annotations
import numpy as np

SCHEMA = 'semantic_card.schema/v1'
CATEGORIES = {
    'op': ('none','damage','block','draw','energy','max_energy','lose_hp','heal','max_hp','gold','potion','status','move','exhaust','generate','clone','transform','upgrade','modify','autoplay','replay','rule_begin','rule_end','group_begin','group_end','condition_begin','condition_end','kill','remove_rule'),
    'target': ('none','self','enemy','all_enemies','random_enemy','all_creatures','source_card','event_card','chosen_card','all_matching_cards','random_card','top_card','last_card','target_owner'),
    'event': ('none','play','turn_start','turn_end','draw','card_play','attack_play','skill_play','power_play','exhaust','self_exhaust','block_gain','hp_loss','unblocked_damage','incoming_attack','vulnerable_apply','shuffle','kill','retain','discard','combat_start','card_finished','self_draw','self_discard','attack_finished'),
    'phase': ('none','immediate','before','after','before_draw','after_draw','card_completion','end_turn_flush','next_turn_start','next_turn_end','combat_end','after_selection','before_damage','after_damage'),
    'property': ('none','strength','dexterity','vulnerable','weak','frail','damage','block','cost','max_energy','draw_permission','block_permission','block_retention','hand_retention','play_count','destination','max_hp','damage_received','cost_restore','exhaust_permission','upgrade','ethereal','retain','innate','unplayable','intangible','artifact','plating','thorns','damage_multiplier','vulnerable_multiplier','escape_progress','stunned'),
    'operator': ('none','add','subtract','multiply','set','prevent','preserve','remove','double','reset','clamp'),
    'scope': ('none','event','hit','card','turn','combat','permanent','next_card','next_attack','next_skill','next_turn','until_played','until_exhausted','per_matching_card'),
    'selection': ('none','chosen','random','all','top','bottom','last','choose_from_random'),
    'filter': ('none','any','attack','skill','power','non_attack','non_power','exhaust','non_exhaust','strike','defend','rare','common','uncommon','zero_cost','playable','upgradeable','unupgraded','generated','status','curse','non_basic','ethereal','not_source','attack_or_skill','attack_or_power','same_definition'),
    'pool': ('none','ironclad','colorless','neutral','foreign'),
    'source': ('none','hand','draw','discard','exhaust','all_piles','current_card','event_card','last_card','ironclad_pool','colorless_pool','neutral_pool','enemies'),
    'destination': ('none','hand','draw_top','draw_bottom','draw_random','discard','exhaust','removed','play','inventory'),
    'reference': ('none','self','event','chosen','last_played','last_turn','snapshot','live'),
    'condition': ('none','always','target_vulnerable','target_survives','target_attacking','killed_target','hp_lost_turn','no_attack_last_turn','same_type_last_turn','draw_has_match','hand_has_match','counter_reached','card_is_exhausted','card_from_draw','not_initial_draw','first_event','every_n','no_hp_loss','all_enemies_dead','target_blocked','is_in_hand','owner_turn'),
}
STEP_NUMERIC = ('amount','count','threshold','duration','increment','initial','limit','secondary','minimum','maximum','probability')
STEP_FLAGS = ('powered','snapshot','distinct','with_replacement','can_skip','cost_free','upgraded','temporary','unpowered','stochastic','conditional','live','repeat_each','reset_turn','reset_combat','before_normal_draw','skip_first','self_exhaust','optional','uses_instance_amount','uses_instance_secondary','uses_instance_progress','not_card_block')
STEP_FIELDS = tuple(f'{k}:{v}' for k,vals in CATEGORIES.items() for v in vals) + tuple(x for n in STEP_NUMERIC for x in (n,n+'_known')) + STEP_FLAGS
TERM_OPS = ('constant','state','count','add','subtract','multiply','divide','min','max','clamp','equal','not_equal','greater','greater_equal','less','less_equal','and','or','not','begin','end')
TERM_REFS = ('none','energy','spent_x','hp','max_hp','missing_hp','block','strength','dexterity','target_hp','target_block','target_vulnerable','hand_count','draw_count','discard_count','exhaust_count','strike_count','attacks_in_hand','non_attacks_in_hand','powers_in_hand','cards_played_turn','attacks_played_turn','skills_played_turn','cards_drawn_turn','extra_draws_turn','cards_drawn_combat','cards_exhausted_turn','cards_exhausted_combat','hp_lost_turn','unblocked_damage_turn','last_turn_card_count','last_turn_matching_cards','instance_amount','instance_secondary','instance_progress','instance_growth','instance_replay','instance_cost','turn','qualifying_events','snapshot_count','play_count','target_count','cost_paid','damage_dealt','event_hp_loss','source_damage','source_block','available_choices','hand_capacity','potion_slots','cards_played_combat','target_debuff_count','hp_loss_events_combat','enchant_amount')
TERM_FIELDS = tuple('op:'+x for x in TERM_OPS)+tuple('ref:'+x for x in TERM_REFS)+('value','coefficient','arity','argument','known','snapshot','live')
CURRENT_NUMERIC = ('current_cost','current_x','damage','block','hits','play_count','upgrade_level','bonus_damage','bonus_block','growth','enchant_amount','cost_bump','cost_for_combat','cost_this_turn','cost_restore','known_top_position','known_bottom_position')
CURRENT_FLAGS = ('playable','in_hand','cost_x','unplayable','exhaust','ethereal','retain','innate','sly','eternal','strike_tag','defend_tag','can_upgrade','free_this_turn','free_until_played','retained','played_this_turn','enchant_spent','dampened','bound','pending','enemy_held','offer','source_snapshot','temporary_copy')
CARD_TYPES = ('Attack','Skill','Power','Status','Curse','Quest')
RARITIES = ('Basic','Common','Uncommon','Rare','Ancient','Token','Status','Curse','Event','Quest')
TARGETS = ('Self','AnyEnemy','AllEnemies','RandomEnemy','None','AnyPlayer','AnyAlly','AnyCreature','AllPlayers')
CARD_DENSE_FIELDS = tuple(x for n in CURRENT_NUMERIC for x in (n,n+'_known'))+CURRENT_FLAGS+tuple('type:'+x for x in CARD_TYPES)+tuple('rarity:'+x for x in RARITIES)+tuple('target:'+x for x in TARGETS)
PREVIEW_FIELDS = ('damage','damage_known','block','block_known','hits','hits_known','play_count','exact','conditional','stochastic','source_side','all_enemies','random_target','has_target','effect_position')
SELECTION_FIELDS = tuple('op:'+x for x in ('none','exhaust','discard','retrieve','upgrade','modify','autoplay','generate','clone','transform','retain','move'))+tuple('from:'+x for x in ('none','hand','draw','discard','exhaust','generated'))+tuple('to:'+x for x in ('none','hand','draw','discard','exhaust','play'))+('min','max','selected','skippable','random','remaining','source_present')
CONTINUATION_FIELDS = ('pending_play','owed_draws','owed_plays','selected','remaining','extra_plays','play_index','energy_spent','nested_depth','before_draw','end_turn','after_selection','snapshot_count','source_present','deferred_strength','frame_summary','frame_chosen','frame_snapshot','ordinal','card_known')
SEMANTIC_DIMS = dict(card=len(CARD_DENSE_FIELDS),step=len(STEP_FIELDS),term=len(TERM_FIELDS),preview=len(PREVIEW_FIELDS),continuation=len(CONTINUATION_FIELDS),selection=len(SELECTION_FIELDS))

def slog(value):
    value=np.asarray(value,dtype=np.float64)
    return np.sign(value)*np.log1p(np.abs(value))

def encode_step(step: dict) -> np.ndarray:
    out=np.zeros(len(STEP_FIELDS),np.float32); ix={n:i for i,n in enumerate(STEP_FIELDS)}
    for cat, vals in CATEGORIES.items():
        value=step.get(cat,'none')
        if value not in vals: raise ValueError(f'unknown shared {cat}: {value}')
        out[ix[cat+':'+value]]=1
    for n in STEP_NUMERIC:
        if n in step and step[n] is not None:
            out[ix[n]]=slog(float(step[n]));out[ix[n+'_known']]=1
    for n in STEP_FLAGS:out[ix[n]]=float(bool(step.get(n,False)))
    return out

def encode_term(term: dict) -> np.ndarray:
    out=np.zeros(len(TERM_FIELDS),np.float32); ix={n:i for i,n in enumerate(TERM_FIELDS)}
    op=term.get('op','state');ref=term.get('ref','none')
    if op not in TERM_OPS or ref not in TERM_REFS:raise ValueError(f'unknown formula term {term}')
    out[ix['op:'+op]]=1;out[ix['ref:'+ref]]=1
    for n in ('value','coefficient'):out[ix[n]]=slog(float(term.get(n,0)))
    for n in ('arity','argument','known','snapshot','live'):out[ix[n]]=float(term.get(n,0))
    return out

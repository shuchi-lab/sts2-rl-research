"""Offline native/reference comparison through the selected original policy runtime.

This module never constructs a game transport or controller. It compares the
actual collated model inputs, without correcting or normalizing observations.
Battle accounting is supplied explicitly; recorded scores can certify that it
reproduces the captured decision. Native execution bindings are outside this check.
"""
from __future__ import annotations

import argparse
import copy
from dataclasses import dataclass
import json
from pathlib import Path
from typing import Any

import numpy as np

from sts2_autoplay.policies import FrozenPolicies


@dataclass
class _Decision:
    tensors: dict[str, np.ndarray]
    candidate_ids: tuple[str, ...]
    q: np.ndarray
    allowed: np.ndarray
    chosen: int


def _encode(policies: FrozenPolicies, owner: str, side: dict) -> _Decision:
    payload = copy.deepcopy(side['payload'])
    if owner == 'strategy':
        from run_strategy_v1.config import ExplorationConfig
        from run_strategy_v1.contracts import PublicRunObservation
        from run_strategy_v1.features import StrategyEncoder, collate_strategy
        from run_strategy_v1.learner import StrategyPolicy

        # Independent original encoders keep the live controller's map cache untouched.
        policy = StrategyPolicy(StrategyEncoder(policies.metadata), policies.strategy_model,
                                'cpu', ExplorationConfig())
        public = PublicRunObservation.parse(payload)
        obs = policy.encode(public)
        batch = collate_strategy([obs], 'cpu')
        chosen_id, _ = policy.act([(obs, public, None, 'validation')])[0]
        if isinstance(chosen_id, Exception):
            raise ValueError(f'Original Choice inference: {chosen_id}') from chosen_id
        q = policy.q_values([obs])[0][:obs.n_candidates].numpy().copy()
        ids = obs.candidate_ids
        allowed = np.ones(len(ids), dtype=bool)
        chosen = obs.index(chosen_id)
    else:
        from act1_battle_finetune_v1.reward import AllBossFreeLedger
        from crl.features import collate
        from run_strategy_v1.frozen_battle import FrozenBattlePolicy, battle_option_id

        # A separate original policy/fight owns this probe, never the live fight ledger.
        context = copy.copy(policies.context)
        context.adapter = copy.deepcopy(policies.context.adapter)
        policy = FrozenBattlePolicy(context, policies.battle_model,
                                    policies.battle_blob, policies.battle_blob['model_identity'])
        entry, raw, accounting = payload['entry'], payload['raw'], side['accounting']
        fight = policy.begin_fight(payload['fight_id'], entry['act_ordinal'],
                                   entry['room'], entry['entry_hp'])
        fight.pstate.ledger = AllBossFreeLedger(accounting['potion_lambda'])
        fight.pstate.ledger.bill = accounting['bill']
        fight.pstate.ledger.uses = copy.deepcopy(accounting['uses'])
        fight.pstate._turn = accounting['turn']
        fight.pstate._lag = accounting['lag']
        fight.pstate._prev_turn_lag = accounting['previous_turn_lag']
        choice = policy.choose_safe([(raw, fight)])[0]
        if isinstance(choice, Exception):
            raise ValueError(f'Original Battle inference: {choice}') from choice
        obs = fight.last_obs
        batch = collate([obs], device='cpu')
        ids = tuple(battle_option_id(option) for option in raw['obs']['legal'])
        q = fight.last_q.copy()
        allowed = np.asarray(obs.opt_admissible, dtype=bool)
        chosen = choice.ordinal
    tensors = {key: value.detach().cpu().numpy().copy() for key, value in batch.items()}
    return _Decision(tensors, tuple(ids), q, allowed, chosen)


def _scalar(value: Any) -> Any:
    result = value.item() if isinstance(value, np.generic) else value
    if isinstance(result, float) and not np.isfinite(result):
        return str(result)
    return result


def _difference(field: str, reference: np.ndarray, native: np.ndarray) -> dict | None:
    if reference.dtype == native.dtype and np.array_equal(reference, native):
        return None
    result = {'field': field, 'reference_shape': list(reference.shape), 'native_shape': list(native.shape),
              'reference_dtype': str(reference.dtype), 'native_dtype': str(native.dtype)}
    if reference.shape == native.shape:
        positions = np.argwhere(reference != native)
        if len(positions):
            first = tuple(int(i) for i in positions[0])
            result.update(different_values=len(positions), first_coordinate=list(first),
                          reference_value=_scalar(reference[first]), native_value=_scalar(native[first]))
        if reference.size:
            result['max_abs_difference'] = _scalar(np.max(np.abs(reference.astype(float) - native.astype(float))))
    return result


def _summary(decision: _Decision, side: dict) -> dict:
    scores = [{'id': cid, 'q': _scalar(q)} for cid, q, ok in
              zip(decision.candidate_ids, decision.q, decision.allowed) if ok]
    recorded = side.get('recorded_scores')
    return {'candidate_count': len(decision.candidate_ids), 'candidate_ids': list(decision.candidate_ids),
            'chosen_ordinal': decision.chosen, 'chosen_id': decision.candidate_ids[decision.chosen],
            'scores': scores, 'recorded_scores_match': None if recorded is None else scores == recorded,
            'accounting': copy.deepcopy(side.get('accounting'))}


def compare_case(policies: FrozenPolicies, case: dict) -> dict:
    """Compare policy inputs and scores, without changing cases or live policy state."""
    if case.get('schema') != 'sts2.adapter_contract_case/v1':
        raise ValueError('Expected sts2.adapter_contract_case/v1')
    owner = case['owner']
    if owner not in ('strategy', 'battle'):
        raise ValueError(f'Unknown policy owner: {owner}')
    reference = _encode(policies, owner, case['reference'])
    native = _encode(policies, owner, case['native'])
    differences = []
    for field in sorted(reference.tensors.keys() | native.tensors.keys()):
        if field not in reference.tensors or field not in native.tensors:
            differences.append({'field': field, 'missing_from': 'reference' if field not in reference.tensors else 'native'})
        elif (diff := _difference(field, reference.tensors[field], native.tensors[field])) is not None:
            differences.append(diff)
    q_difference = _difference('q', reference.q, native.q)
    ref_summary, native_summary = _summary(reference, case['reference']), _summary(native, case['native'])
    same_choice = reference.chosen == native.chosen
    verified = all(side['recorded_scores_match'] is not False for side in (ref_summary, native_summary))
    checks = [side['recorded_scores_match'] for side in (ref_summary, native_summary)]
    certification = ('mismatch' if False in checks else 'both' if checks == [True, True]
                     else 'reference_only' if checks[0] is True else 'native_only' if checks[1] is True
                     else 'not_checked')
    return {'name': case.get('name', 'unnamed'), 'owner': owner, 'status': 'compared',
            'equivalent': not differences and q_difference is None and same_choice and verified,
            'score_context_certification': certification,
            'tensor_differences': differences, 'q_equal': q_difference is None,
            'q_difference': q_difference, 'same_chosen_ordinal': same_choice,
            'reference': ref_summary, 'native': native_summary, 'source': copy.deepcopy(case.get('source'))}


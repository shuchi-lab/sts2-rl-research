"""Frozen published Choice90k N1/Wide+deep Battle177.5k inference.

Battle payloads are {run_id, fight_id, entry:{act_ordinal,room,entry_hp,
entry_max_hp}, raw:<complete frozen Session reply>}. A decision is encoded once;
repeat advice returns cached scores. After an action settles, the controller calls
account_battle(before, executed_candidate_id, after) exactly once (retries are safe).
The next decision is refused until this happens. Starting mid-fight is refused.
"""
from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any

from .bootstrap import CHOICE_PRESETS, DEFAULT_CHOICE_PRESET, configure_runtime, initialize

# Default preset identity, retained for existing callers; scores use self.choice_id.
CHOICE_ID = CHOICE_PRESETS[DEFAULT_CHOICE_PRESET]['model_id']
BATTLE_ID = 'enemy_wide_deep_act3_ft_e032500'
REWARD_ID = 'combat-potion-turn100-boss-act-v2-victory-bill-all-boss-free-v1'


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False)


def _require(condition, message):
    if not condition:
        raise ValueError(message)


def _without_checksums(value):
    """Keep model/schema/parameter metadata; legacy digests are informational."""
    if isinstance(value, dict):
        return {k: _without_checksums(v) for k, v in value.items()
                if 'sha256' not in k and 'digest' not in k and 'checksum' not in k}
    if isinstance(value, list):
        return [_without_checksums(v) for v in value]
    return value


# Frozen BuffSystem.Apply keeps one row for these additive counters
# (BlockNextTurn81, TemporaryStrength83, TemporaryDexterity130). Native origins
# (e.g. SelfFormingClayPower + BlockNextTurnPower) arrive as several rows from a
# mod DLL without the matching BattlePowerFold; fold them identically here.
STACKED_COUNTER_BUFFS = (81, 83, 130)


# Native PanachePower is IsInstanced (one row + one PanacheArmed39 helper per
# application). Frozen build08 BuffSystem.Apply stacks Panache38 into one amount
# with one armed flag and one counter started by the first application, so the
# model view sums 38 at its first row and keeps only the oldest instance's 39.
PANACHE_POWER, PANACHE_ARMED = 38, 39


def _fold_stacked_counters(rows):
    """AgentBattlePowerFold.TemporaryCounters: sum at first appearance; drop zero 83/130;
    Panache38 summed, oldest PanacheArmed39 kept."""
    result, counters = [], {}
    for row in rows:
        if row[0] == PANACHE_ARMED:
            if PANACHE_ARMED not in counters:
                counters[PANACHE_ARMED] = list(row)
                result.append(counters[PANACHE_ARMED])
        elif row[0] in STACKED_COUNTER_BUFFS + (PANACHE_POWER,):
            if row[0] in counters:
                counters[row[0]][1] += row[1]
            else:
                counters[row[0]] = [row[0], row[1]]
                result.append(counters[row[0]])
        else:
            result.append(list(row))
    return [row for row in result if not (row[0] in (83, 130) and row[1] == 0)]


def _normalize_stacked_counters(raw):
    """Return raw with folded player/enemy buff rows; the caller's payload is never mutated."""
    def owners(obs):
        return [obs.get('player') or {}] + list(obs.get('enemies') or [])
    if all(_fold_stacked_counters(o.get('buffs') or []) == (o.get('buffs') or []) for o in owners(raw.get('obs') or {})):
        return raw
    raw = copy.deepcopy(raw)
    for owner in owners(raw['obs']):
        if owner.get('buffs'):
            owner['buffs'] = _fold_stacked_counters(owner['buffs'])
    return raw


def validate_battle_identity(blob: dict) -> None:
    """Closed identity checks independent of model construction and native execution."""
    meta = blob.get('meta') or {}
    ident = blob.get('experiment_model') or {}
    state = blob.get('bec1_state') or {}
    expected = {'profile': 'enemy_wide_deep', 'stage': 2, 'boundary': 'drained',
                'additional_fights': 32500, 'original_stage1_fights': 20000,
                'original_stage2_fights': 77500, 'prior_act1_finetune_fights': 20000,
                'prior_act2_finetune_fights': 27500, 'source_cumulative_online_fights': 145000,
                'cumulative_online_fights': 177500,
                'reward_id': REWARD_ID}
    for key, value in expected.items():
        _require(meta.get(key) == value, f'Battle meta.{key} differs from selected checkpoint')
    _require(blob.get('schema') == 'crl.checkpoint/v1', 'Battle checkpoint schema differs')
    _require(ident.get('profile') == 'enemy_wide_deep' and ident.get('parameters') == 929653
             and ident.get('enemy_layers') == [512, 512],
             'Battle architecture/profile differs')
    _require(meta.get('model') == ident, 'Battle recorded model identity differs')
    _require(state.get('schema') == 'battle-act3-ft50k/learner-state/v1', 'Battle fine-tune state schema differs')
    for k, v in [('in_flight', 0), ('stage', 2), ('boundary', 'drained'), ('completed', 32500),
                 ('dispatched', 32500), ('initialization', 'exact_closed_state_resume')]:
        _require(state.get(k) == v, f'Battle state.{k} differs')
    counters = meta.get('counters') or {}
    for k, v in [('stage1_online', 20000), ('stage2_online', 77500), ('act1_finetune_online', 20000),
                 ('act2_finetune_online', 27500), ('act3_finetune_online', 32500)]:
        _require(counters.get(k) == v, f'Battle counters.{k} differs')
    _require(state.get('counters') == counters, 'Battle state counters differ')
    _require((state.get('config') or {}).get('profile') == 'enemy_wide_deep', 'Battle state profile differs')
    _require((state.get('config') or {}).get('seed') == 2026101225, 'Battle fine-tune seed differs')
    _require((meta.get('reward_contract') or {}).get('training_reward') == REWARD_ID, 'Battle reward contract differs')


class FrozenPolicies:
    def __init__(self, project_root: Path, choice_preset: str = DEFAULT_CHOICE_PRESET):
        self.assets = initialize(project_root, choice_preset)
        configure_runtime()
        self.choice_preset = self.assets.choice_preset
        self.choice_spec = CHOICE_PRESETS[self.choice_preset]
        self.choice_id = self.choice_spec['model_id']
        self.label = f"{self.choice_spec['label']} + Wide+deep Battle177.5k"
        import torch
        torch.set_num_threads(1)
        from crl import paths
        from crl.expansion_turn100_v1 import turn100_env_config
        from act1_battle_finetune_v1.reward import AllBossFreePotionContext
        from battle_enemy_capacity_v1 import checkpoint as battle_checkpoint
        from run_strategy_v1.artifacts import load_strategy_checkpoint
        from run_strategy_v1.config import ModelConfig
        from run_strategy_v1.features import StrategyEncoder
        from run_strategy_v1.frozen_battle import FrozenBattlePolicy
        from run_strategy_v1.metadata import load_public_metadata
        from run_strategy_v1.model import StrategyQNet

        self.context = AllBossFreePotionContext([], turn100_env_config(100), potion_lambda=.5,
                                               allowed_turns=100, loss_profile='boss-act-v2')
        # Context performs actual manifest/hello/vocabulary validation. Only hello ran.
        try:
            from .native_potion import NativeAllBossFreeAdapter
            self.context.adapter = NativeAllBossFreeAdapter(
                self.context.vocab, self.context.worker.hello,
                frond_relic_id=self.context.frond_relic_id, sozu_relic_id=self.context.sozu_relic_id)
            self.provenance = self.context.provenance()
            self.metadata = load_public_metadata(paths.CATALOG, self.context.vocab.card_ids)
            self.strategy_encoder = StrategyEncoder(self.metadata)
            source = json.loads(self.assets.choice_manifest.read_text())
            config = json.loads(self.assets.choice_config.read_text())
            cb = load_strategy_checkpoint(self.assets.choice_checkpoint, weights_only=False)
            self._validate_choice(cb, config, source)
            self.strategy_model = StrategyQNet(self.metadata, ModelConfig(**config['model']))
            _require(self.strategy_model.config == cb['model_config'], 'Choice model architecture differs')
            self.strategy_model.load_state_dict(cb['state_dict'], strict=True)
            self._freeze(self.strategy_model)
            # The original loader constructs/checks exact profiles and strict tensor keys.
            # Compare the actual schema/vocabulary/reward values without legacy
            # checksum gates or a simulator execution facade.
            self.battle_model, self.battle_blob = battle_checkpoint.load(
                self.assets.battle_checkpoint, device='cpu', expected_profile='enemy_wide_deep')
            validate_battle_identity(self.battle_blob)
            for name in ('worker_protocol', 'reward', 'terminal_boundary', 'feature_schema',
                         'encoder_scope', 'potion_actions', 'final_boss_contract'):
                _require(_without_checksums(self.battle_blob['provenance'].get(name)) ==
                         _without_checksums(self.provenance.get(name)), f'Battle {name} differs')
            _require(_without_checksums(self.battle_blob['vocab']) == _without_checksums(self.context.vocab.describe()),
                     'Battle vocabulary/schema differs')
            _require(_without_checksums(self.battle_blob['meta']['reward_contract']) == _without_checksums(self.context.reward_contract()),
                     'Battle exact reward contract differs')
            self.battle_blob['compatibility_problems'] = []
            self._freeze(self.battle_model)
            self.battle_policy = FrozenBattlePolicy(self.context, self.battle_model, self.battle_blob,
                                                    self.battle_blob['model_identity'])
            strategy = {'model_id': self.choice_id, 'resolved_attempts': cb['meta']['train_resolved'],
                        'n_step': config['learner']['n_step'], 'reward_id': config['reward_id'],
                        'parameters': cb['model_config']['parameters'],
                        'checkpoint': str(self.assets.choice_checkpoint)}
            strategy.update(preset=self.choice_preset, pilot_id=source['pilot_id'],
                            learning_profile=config['learning_profile'], release=self.release)
            self.identities = {
                'strategy': strategy,
                'battle': {'model_id': BATTLE_ID, 'profile': 'enemy_wide_deep', 'parameters': 929653,
                           'additional_fights': 32500, 'cumulative_online_fights': 177500,
                           'reward_id': REWARD_ID, 'checkpoint': str(self.assets.battle_checkpoint)},
            }
            self.fights: dict[str, dict[str, Any]] = {}
        finally:
            self.context.worker.close()

    @staticmethod
    def _freeze(model):
        model.eval()
        for p in model.parameters():
            p.requires_grad_(False)

    def _validate_choice(self, blob, cfg, source):
        meta, spec = blob['meta'], self.choice_spec
        attempts = spec['resolved_attempts']
        for k, v in [('train_resolved', attempts), ('admitted_train', attempts), ('label', 'attempt_boundary'),
                     ('training_only', True), ('evaluation_enabled', False), ('drained', True),
                     ('learning_profile', spec['learning_profile']), ('updates', spec['updates'])]:
            _require(meta.get(k) == v, f'Choice meta.{k} differs from selected checkpoint')
        _require(source['pilot_id'] == cfg['pilot_id'] == spec['pilot_id'], 'Choice pilot differs')
        _require(source['learning_profile'] == meta['learning_profile'] == cfg['learning_profile'], 'Choice profile differs')
        _require(meta['learning_contract'] == source['learning_contract'], 'Choice learning contract differs')
        _require(source['learner']['n_step'] == cfg['learner']['n_step'] == source['learning_contract']['n_step']
                 == spec['n_step'], 'Choice N-step differs')
        _require(blob['model_config']['parameters'] == 344609, 'Choice parameter count differs')
        _require(source['reward_id'] == cfg['reward_id'] == 'run-strategy-act-win-v1', 'Choice reward differs')
        _require(source['frozen_e']['episodes'] == 90000, 'Choice original Blue90k producer differs')
        _require(_without_checksums(source['metadata']) == _without_checksums(self.metadata.describe()), 'Choice static metadata differs')
        _require(_without_checksums(source['encoder']) == _without_checksums(self.strategy_encoder.describe()), 'Choice encoder differs')
        self.release = self._validate_release(spec, cfg) if spec['release'] else None

    def _validate_release(self, spec, cfg):
        """The published release must name this Choice and the shared Battle (semantic fields only)."""
        root = self.assets.project / spec['release']
        summary = json.loads((root / 'results/summary.json').read_text())
        agents = json.loads((root / 'results/model_identities.json').read_text())
        _require(summary.get('choice_model_id') == spec['model_id'], 'release Choice model id differs from preset')
        _require(summary.get('battle_model_id') == BATTLE_ID, 'release Battle model id differs from shared Battle')
        choice, battle = agents.get('choice') or {}, agents.get('battle') or {}
        _require(choice.get('training_attempts') == spec['resolved_attempts'] and choice.get('n_step') == cfg['learner']['n_step'],
                 'release Choice description differs from preset')
        _require(battle.get('profile') == 'enemy_wide_deep' and battle.get('parameters') == 929653
                 and battle.get('cumulative_online_fights') == 177500 and battle.get('act3_finetune_fights') == 32500,
                 'release Battle description differs from shared Battle')
        version = (root / 'VERSION').read_text().strip() if (root / 'VERSION').is_file() else None
        return {'path': str(root), 'version': version, 'choice_model_id': summary['choice_model_id'],
                'battle_model_id': summary['battle_model_id']}

    def check_contract(self, path: Path) -> None:
        """Check the packaged mod's model identities and encoder field contracts offline."""
        contract = json.loads(path.read_text())
        _require(contract.get('schema') == 'sts2.live_contract/v1', 'mod contract schema differs')
        for owner, keys in [('strategy', ('model_id', 'resolved_attempts', 'n_step', 'parameters', 'reward_id')),
                            ('battle', ('model_id', 'profile', 'parameters', 'additional_fights',
                                        'cumulative_online_fights', 'reward_id'))]:
            for key in keys:
                _require(contract['models'][owner].get(key) == self.identities[owner][key],
                         f'mod contract {owner}.{key} differs')
        for owner, field, expected in [('battle', 'encoder', self.context.adapter.describe()),
                                       ('strategy', 'encoder', self.strategy_encoder.describe()),
                                       ('strategy', 'metadata', self.metadata.describe())]:
            _require(_canonical(_without_checksums(contract[owner][field])) == _canonical(_without_checksums(expected)),
                     f'mod contract {owner}.{field} differs')

    def score(self, owner: str, payload: dict) -> dict:
        _require(owner in ('strategy', 'battle'), f'unknown policy owner {owner!r}')
        _require(isinstance(payload, dict), 'policy payload must be an object')
        try:
            _canonical(payload)
            if owner == 'battle':
                return self._battle_score(payload)
            allowed_fields = {'schema', 'run_id', 'decision_id', 'phase', 'scalars', 'deck', 'relics',
                              'potions', 'map', 'history', 'candidates', 'screen', 'declined'}
            _require(not (set(payload) - allowed_fields), 'unknown/hidden Choice payload field(s)')
            from run_strategy_v1.contracts import PublicRunObservation
            from run_strategy_v1.features import collate_strategy
            import torch
            obs = PublicRunObservation.parse(payload)
            encoded = self.strategy_encoder.encode(obs)
            with torch.inference_mode():
                q = self.strategy_model(collate_strategy([encoded], 'cpu'))[0][:encoded.n_candidates].float().cpu()
            _require(bool(torch.isfinite(q).all()) and bool((q > -5e8).all()), 'nonfinite/admissibility Choice scores')
            chosen = int(q.argmax())
            return {'model_id': self.choice_id, 'chosen_id': encoded.candidate_ids[chosen],
                    'scores': [{'id': cid, 'q': float(value)} for cid, value in zip(encoded.candidate_ids, q)],
                    'decision_id': obs.decision_id, 'owner': owner, 'score_kind': 'discounted_reward',
                    'identity': self.identities[owner]}
        except ValueError:
            raise
        except Exception as e:
            raise ValueError(f'{owner} observation refused: {type(e).__name__}: {e}') from e

    def _battle_parts(self, p):
        _require(set(('run_id', 'fight_id', 'entry', 'raw')) <= p.keys(), 'missing Battle run/fight/entry/raw context')
        _require(isinstance(p['run_id'], str) and p['run_id'] and isinstance(p['fight_id'], str) and p['fight_id'],
                 'Battle run/fight ids must be nonempty strings')
        e, r = p['entry'], p['raw']
        _require(set(('act_ordinal', 'room', 'entry_hp', 'entry_max_hp')) <= e.keys(), 'missing Battle entry context')
        _require(e['act_ordinal'] in (1, 2, 3) and e['room'] in ('normal', 'event', 'elite', 'boss'), 'unknown Battle act/room')
        _require(e['entry_hp'] > 0 and e['entry_max_hp'] > 0, 'invalid Battle entry HP')
        _require(r.get('entry_hp') == e['entry_hp'] and r.get('entry_max_hp') == e['entry_max_hp'], 'Battle entry HP changed')
        _require(isinstance(r.get('decision'), int), 'Battle decision id missing')
        return f"{p['run_id']}|{p['fight_id']}", e, _normalize_stacked_counters(r)

    def _battle_score(self, payload):
        from act1_battle_finetune_v1.reward import AllBossFreeLedger
        from run_strategy_v1.frozen_battle import battle_option_id
        import torch
        key, entry, raw = self._battle_parts(payload)
        _require(raw.get('status') == 'running', 'Battle is not at a running decision')
        if key not in self.fights:
            _require(raw.get('steps') == 0 and raw.get('obs', {}).get('turn') == 0,
                     'cannot begin mid-fight without accumulated public accounting')
            f = self.battle_policy.begin_fight(payload['fight_id'], entry['act_ordinal'], entry['room'], entry['entry_hp'])
            f.pstate.ledger = AllBossFreeLedger(.5)
            self.fights[key] = {'fight': f, 'entry': copy.deepcopy(entry), 'scores': {}, 'accounted': {},
                                'last_decision': None, 'next_raw': None, 'closed': False}
        state = self.fights[key]
        _require(not state['closed'], 'Battle fight already closed')
        _require(state['entry'] == entry, 'Battle entry context changed')
        decision, canonical = raw['decision'], _canonical(raw)
        if decision in state['scores']:
            saved, result = state['scores'][decision]
            _require(saved == canonical, 'public snapshot changed for the same Battle decision')
            return copy.deepcopy(result)
        last = state['last_decision']
        if last is not None:
            _require(last in state['accounted'], 'unsettled Battle decision: account executed action before scoring next')
            _require(state['next_raw'] == canonical, 'Battle next reply differs from accounted settlement')
            _require(decision > last, 'Battle decision did not advance')
        result = self.battle_policy.choose_safe([(raw, state['fight'])])[0]
        if isinstance(result, Exception):
            raise ValueError(f'Battle observation refused: {result}')
        f = state['fight']
        allowed = f.last_obs.opt_admissible
        ids = [battle_option_id(o) for o in raw['obs']['legal']]
        _require(len(set(ids)) == len(ids), 'duplicate Battle option ids')
        scores = [{'id': cid, 'q': float(q)} for i, (cid, q) in enumerate(zip(ids, f.last_q))
                  if allowed is None or allowed[i]]
        response = {'model_id': BATTLE_ID, 'chosen_id': result.option_id, 'scores': scores,
                    'owner': 'battle', 'decision_id': decision, 'fight_id': payload['fight_id'],
                    'score_kind': 'discounted_reward', 'identity': self.identities['battle'],
                    'excluded_ids': [cid for i, cid in enumerate(ids) if allowed is not None and not allowed[i]]}
        # Preserve only the current advice snapshot; settled-action receipts retain
        # public equality signatures for duplicate acknowledgement.
        state['scores'] = {decision: (canonical, copy.deepcopy(response))}
        state['last_decision'] = decision
        return response

    def account_battle(self, before: dict, candidate_id: str, after: dict) -> dict:
        """Account a confirmed native action after settlement, including terminal replies.

        This method never executes anything. The caller must reconcile request status
        first, and supply the actual action id (manual stepping may choose another id).
        """
        from run_strategy_v1.frozen_battle import battle_option_id
        key, entry, previous = self._battle_parts(before)
        next_key, next_entry, subsequent = self._battle_parts(after)
        _require(key == next_key and entry == next_entry, 'Battle settlement fight/entry identity differs')
        _require(key in self.fights, 'Battle settlement has no scored fight')
        state, decision = self.fights[key], previous['decision']
        signature = _canonical([previous, candidate_id, subsequent])
        if decision in state['accounted']:
            saved, receipt = state['accounted'][decision]
            _require(signature == saved, 'conflicting duplicate Battle settlement')
            return copy.deepcopy(receipt)
        _require(decision in state['scores'], 'Battle settlement was never scored')
        _require(state['scores'][decision][0] == _canonical(previous), 'Battle settlement previous snapshot differs')
        _require(decision == state['last_decision'], 'Battle settlement is out of order')
        legal = [battle_option_id(o) for o in previous['obs']['legal']]
        _require(candidate_id in legal, 'Battle executed candidate id is not legal')
        ordinal = legal.index(candidate_id)
        mask = state['fight'].last_obs.opt_admissible
        _require(mask is None or mask[ordinal], 'Battle executed candidate is policy-inadmissible')
        _require(subsequent.get('status') in ('running', 'win', 'loss', 'truncated', 'error', 'unsupported'),
                 'Battle settlement missing actual status')
        _require(subsequent['decision'] > decision, 'Battle settlement did not advance decision')
        self.battle_policy.account(state['fight'], previous, ordinal, subsequent)
        state['next_raw'] = _canonical(subsequent)
        state['closed'] = subsequent['status'] != 'running'
        receipt = {'accounted': True, 'fight_id': before['fight_id'], 'decision_id': decision,
                   'closed': state['closed'], 'bill': state['fight'].pstate.ledger.bill}
        state['accounted'][decision] = (signature, receipt)
        return copy.deepcopy(receipt)

    def reset_run(self, run_id: str):
        """Discard one completed/abandoned run's inference context; never restore mid-fight."""
        for key in list(self.fights):
            if key.startswith(run_id + '|'):
                del self.fights[key]

    def close(self):
        self.context.close()
        self.fights.clear()

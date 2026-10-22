"""Strict frozen N1/N2 Mac original Choice and bec1 Act3 fine-tune metadata; original inference."""
from pathlib import Path
import dataclasses, json
import evaluator_base as base
import fullrun_core as core
from finetune_metadata import finetune_problems

def canonical(x):
    return json.dumps(x, sort_keys=True, separators=(',', ':'), allow_nan=False)

def same(a, b):
    """Type-strict JSON identity (1 != 1.0 != true); JSON lists and in-memory tuples compare equal."""
    return canonical(a) == canonical(b)

class Evaluation(base.Evaluation):
    def _initialize(self):
        super()._initialize()
        from run_strategy_v1 import config as rc
        fields = [f.name for f in dataclasses.fields(rc.AbortConfig)]
        if fields == ['undefined_rate', 'min_resolved']:
            @dataclasses.dataclass(frozen=True)
            class TrainingAbortConfig(rc.AbortConfig):
                stop_on_undefined_rate: bool = True
            rc.AbortConfig = TrainingAbortConfig
            rc._NESTED['abort'] = TrainingAbortConfig
        elif fields != ['undefined_rate', 'min_resolved', 'stop_on_undefined_rate']:
            raise ValueError('Frozen typed AbortConfig differs')
        specs = self.m['windows_additional_profiles']
        required = {'n1-half-blue90-original-mac':(1,'run-strategy-act-win-v1')}
        if set(specs) != set(required):
            raise ValueError('Exactly the selected N1 Choice source is required')
        self.source_profiles = {}
        for arm, spec in specs.items():
            literal, name = spec['literal_config'], spec['learning_profile']
            step, reward_id = required[arm]
            if (literal['learning_profile'] != name or literal['learner']['n_step'] != step
                    or spec['n_step'] != step or literal['pilot_id'] != spec['pilot_id']
                    or literal['replay']['batch_counts'] != [84,16,12,8,8]
                    or literal['reward_id'] != reward_id
                    or literal['abort']['stop_on_undefined_rate'] is not False):
                raise ValueError('Pinned N1/N2 source training literal differs')
            fields = {k:(rc._NESTED[k](**v) if k in rc._NESTED else v) for k,v in literal.items()
                      if k not in ('schema','learning_profile','learner','replay')}
            triple = (fields, step, rc.ReplayConfig.from_dict(literal['replay']))
            if name in rc.PROFILES and rc.PROFILES[name] != triple:
                raise ValueError('Profile conflict')
            rc.PROFILES[name] = triple
            if name not in rc.TARGET_CONTROLLED_PROFILES:
                rc.TARGET_CONTROLLED_PROFILES = (*rc.TARGET_CONTROLLED_PROFILES, name)
            cfg = rc.load_config(Path(self.m['_package'])/spec['config'], require_pilot=False)
            if canonical(rc.to_dict(cfg)) != canonical(literal):
                raise ValueError('Literal source config mismatch')
            self.source_profiles[arm] = (cfg, spec)

    def strategy(self, entry):
        if entry['id'] in self.strategy_cache:
            return self.strategy_cache[entry['id']]
        from run_strategy_v1.artifacts import load_strategy_checkpoint
        from run_strategy_v1.learner import StrategyPolicy
        from run_strategy_v1.model import StrategyQNet
        cfg, spec = self.source_profiles[entry['arm']]
        package = Path(self.m['_package'])
        source = core.read_json(package/spec['source_manifest'])
        blob = load_strategy_checkpoint(package/entry['path'], weights_only=False)
        meta, receipt, n = blob['meta'], entry['receipt'], entry['new_resolved']
        expected = dict(train_resolved=n, admitted_train=n, label='attempt_boundary', training_only=True,
            evaluation_enabled=False, drained=True, learning_profile=cfg.learning_profile,
            learning_contract=source['learning_contract'], updates=receipt['updates'])
        if any(meta.get(k) != v for k,v in expected.items()):
            raise ValueError('Saved numbered Choice metadata differs')
        if (n not in range(5000, cfg.max_training_attempts+1, 5000) or receipt['train_resolved'] != n
                or not receipt['drained'] or receipt['sha256'] != entry['file_sha256']
                or receipt['path'] != f'checkpoints/a{n:06d}.pt'
                or entry['cumulative_attempts'] != spec['source_offset_attempts']+n
                or source['pilot_id'] != spec['pilot_id'] or source['learning_profile'] != cfg.learning_profile
                or source['initial_weights_sha256'] != spec['initial_weights_sha256']
                or source['frozen_e']['episodes'] != 90000 or source['frozen_e']['source_arm'] != 'win_q25_r30'
                or source['frozen_e']['file_sha256'] != spec['training_battle_file_sha256']
                or source['reward_id'] != cfg.reward_id or not same(source['reward'], spec['training_reward'])
                or source['learning_contract']['n_step'] != spec['n_step']
                or not same(source['learner'], dataclasses.asdict(cfg.learner))
                or not same(source['replay'], dataclasses.asdict(cfg.replay))
                or not same(source['metadata'], self.metadata.describe())
                or not same(source['encoder'], self.encoder.describe())
                or cfg.limits.battle_turns != self.ctx.potion.allowed_turns):
            raise ValueError('Choice source/encoder/learner/reward/Blue90 provenance differs')
        if spec['source_offset_attempts'] == 25000:
            if (meta.get('source_offset_attempts') != 25000 or meta.get('additional_train_resolved') != n
                    or meta.get('cumulative_train_resolved') != 25000+n
                    or meta.get('cumulative_admitted_train') != 25000+n
                    or not same(meta.get('initialization'), source['initialization'])
                    or meta.get('resume_claim') is not False):
                raise ValueError('N3 warm-start/additional/cumulative checkpoint identity differs')
        model = StrategyQNet(self.metadata, cfg.model)
        if model.config != blob['model_config']:
            raise ValueError('Choice architecture differs')
        model.load_state_dict(blob['state_dict'], strict=True)
        base.freeze(model)
        self.strategy_cache[entry['id']] = (StrategyPolicy(self.encoder, model, 'cpu', cfg.exploration), cfg)
        return self.strategy_cache[entry['id']]

    def battle(self, entry):
        if entry['id'] in self.battle_cache:
            return self.battle_cache[entry['id']]
        from battle_enemy_capacity_v1 import checkpoint, profiles
        from act1_battle_finetune_v1 import evaluate, isolation
        from run_strategy_battle62p5_v1 import battle62
        from crl.potion_v1 import POTION_GLOBAL_FEATURES
        path = Path(self.m['_package']) / entry['path']
        if entry['profile'] not in ('enemy_hidden2x', 'enemy_wide_deep'):
            raise ValueError('Battle is outside the exact completed Act3 checkpoint inventory')
        guard = isolation.require(self.ctx)
        model, blob = checkpoint.load(path, self.ctx, device='cpu', expected_profile=entry['profile'])
        problems = finetune_problems(model, blob, entry['roster'], self.ctx.potion.reward_id)
        if blob.get('compatibility_problems') != [] or profiles.identity(model) != blob['experiment_model']:
            problems.append('strict rebuilt capacity model/context identity differs')
        if problems:
            raise core.ContractViolation(f"{entry['id']}: Battle fine-tune refused: {problems}")
        state = blob['bec1_state']
        if state['counters'] != entry['recorded_identity']['counters']:
            raise ValueError('Saved Battle inventory counters differ')
        identity = dict(**profiles.identity(model), kind='bec1_act3_finetune50k_battle',
            model_id=entry['id'], checkpoint=str(path), checkpoint_sha256=entry['file_sha256'],
            file_sha256=entry['file_sha256'], state_sha256=entry['state_sha256'],
            contract='all-boss-free', reward_id=self.ctx.potion.reward_id, boundary='drained',
            original_stage1_fights=entry['roster']['original_stage1_fights'],
            original_stage2_fights=entry['roster']['original_stage2_fights'],
            prior_act1_finetune_fights=entry['roster']['prior_act1_finetune_fights'], prior_act2_finetune_fights=entry['roster']['prior_act2_finetune_fights'], source_cumulative_online_fights=entry['roster']['source_cumulative_online_fights'], additional_fights=entry['additional_fights'], cumulative_online_fights=entry['cumulative_online_fights'],
            training_host='win-gpu', source_host=entry['source_host'], battle_guard=guard,
            published_identity_available=entry['file_sha256'] is not None,
            used='online state_dict only; no target/optimizer/replay', runtime_file_hashes_performed=False)
        policy = evaluate.FineTunedBattlePolicy.make(self.ctx, base.freeze(model), blob, identity)
        problems = battle62.policy_problems(policy)
        if problems:
            raise core.ContractViolation(f'Capacity Battle policy differs: {problems}')
        described = self.ctx.adapter.describe()
        base.install_boss_bill_check(policy, tuple(POTION_GLOBAL_FEATURES), len(described['global_features']))
        self.battle_cache[entry['id']] = policy
        return policy

"""Real checkpoints and retained public observations; no game connection."""
import copy
import json
from pathlib import Path

import pytest

PACKAGE = Path(__file__).resolve().parents[1]
FIXTURES = Path(__file__).parent / 'fixtures'


def fixture(name):
    return json.loads((FIXTURES / name).read_text())


@pytest.fixture(scope='module')
def policies():
    from sts2_autoplay.policies import FrozenPolicies
    instance = FrozenPolicies(PACKAGE.parent)
    yield instance
    instance.close()


def test_selected_models_are_frozen_and_worker_is_closed(policies):
    assert policies.identities['strategy']['resolved_attempts'] == 90000
    assert policies.identities['strategy']['n_step'] == 1
    assert policies.identities['battle']['cumulative_online_fights'] == 177500
    assert sum(p.numel() for p in policies.strategy_model.parameters()) == 344609
    assert sum(p.numel() for p in policies.battle_model.parameters()) == 929653
    for model in (policies.strategy_model, policies.battle_model):
        assert not model.training
        assert all(not p.requires_grad and p.grad is None for p in model.parameters())
    assert not hasattr(policies, 'optimizer')
    assert policies.context.worker.proc.poll() == 0, 'hello-only worker should already be closed'


def test_choice_scores_equal_original_frozen_inference(policies):
    from run_strategy_v1.contracts import PublicRunObservation
    from run_strategy_v1.learner import StrategyPolicy
    from run_strategy_v1.config import ExplorationConfig
    raw = fixture('choice_recorded.json')
    reference = StrategyPolicy(policies.strategy_encoder, policies.strategy_model, 'cpu', ExplorationConfig())
    encoded = reference.encode(PublicRunObservation.parse(raw))
    q = reference.q_values([encoded])[0][:encoded.n_candidates].tolist()
    actual = policies.score('strategy', raw)
    assert [row['id'] for row in actual['scores']] == list(encoded.candidate_ids)
    assert [row['q'] for row in actual['scores']] == q
    assert actual['chosen_id'] == encoded.candidate_ids[max(range(len(q)), key=q.__getitem__)]


def test_battle_scores_equal_original_frozen_inference_and_repeat_is_idempotent(policies):
    from run_strategy_v1.frozen_battle import FrozenBattlePolicy
    from act1_battle_finetune_v1.reward import AllBossFreeLedger
    raw = fixture('battle_recorded.json')
    reference = FrozenBattlePolicy(policies.context, policies.battle_model, policies.battle_blob, {})
    entry = raw['entry']
    fight = reference.begin_fight(raw['fight_id'], entry['act_ordinal'], entry['room'], entry['entry_hp'])
    fight.pstate.ledger = AllBossFreeLedger(.5)
    chosen = reference.choose([(raw['raw'], fight)])[0]
    actual = policies.score('battle', raw)
    assert actual['chosen_id'] == chosen.option_id
    assert [row['q'] for row in actual['scores']] == fight.last_q.tolist()
    assert policies.score('battle', raw) == actual


@pytest.mark.parametrize('name', ['calendar', 'hex', 'ravenous'])
def test_recorded_native_observations_match_reference_inputs_and_scores(policies, name):
    from reference import compare_case
    result = compare_case(policies, fixture(name + '.json'))
    assert result['equivalent'], result
    assert result['native']['recorded_scores_match'] is True


def test_packaged_contract_accepts_json_fields_and_rejects_different_feature_order(policies, tmp_path):
    policies.check_contract(PACKAGE / 'agent_contract.json')
    contract = json.loads((PACKAGE / 'agent_contract.json').read_text())
    contract['strategy']['encoder'] = {'schema': 'different-layout'}
    wrong = tmp_path / 'wrong.json'
    wrong.write_text(json.dumps(contract))
    with pytest.raises(ValueError, match='strategy.encoder differs'):
        policies.check_contract(wrong)


def test_wrong_battle_checkpoint_identity_is_rejected(policies):
    from sts2_autoplay.policies import validate_battle_identity
    blob = {**policies.battle_blob, 'meta': copy.deepcopy(policies.battle_blob['meta'])}
    blob['meta']['cumulative_online_fights'] = 120000
    with pytest.raises(ValueError, match='cumulative_online_fights'):
        validate_battle_identity(blob)


def test_hidden_choice_fields_are_rejected(policies):
    raw = fixture('choice_recorded.json')
    raw['future_shuffle_order'] = [1, 2, 3]
    with pytest.raises(ValueError, match='unknown/hidden'):
        policies.score('strategy', raw)

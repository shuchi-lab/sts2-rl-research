"""HTTP boundary tests: requests travel through a real localhost server."""
import copy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
from pathlib import Path
import threading
import time
import os
import signal
import subprocess
import sys
from urllib.parse import parse_qs, urlparse

import pytest


def test_controller_component_exists():
    assert importlib.util.find_spec('sts2_autoplay.controller') is not None, 'controller is missing'


def observation(owner='strategy', mode='advice', decision=1, ready=True, **extra):
    result = {'schema': 'sts2.live_agent/v1', 'run_id': 'live-run', 'decision_id': f'd{decision}',
              'revision': decision, 'owner': owner, 'phase': 'Combat' if owner == 'battle' else 'Map',
              'ready': ready, 'unsupported': [], 'payload': {'decision': decision},
              'candidates': [{'id': f'candidate-{decision}', 'kind': 'select', 'label': 'Choice', 'public': {}}],
              'battle_terminal': None, 'control': {'mode': mode, 'step_requested': mode == 'step',
                                                'delay_seconds': .1, 'connected': True, 'error': None,
                                                'authorization_revision': 0},
              'pending': None}
    if owner == 'battle':
        result['payload'] = {'run_id': 'live-run', 'fight_id': 'f1',
                             'entry': {'act_ordinal': 1, 'room': 'normal', 'entry_hp': 64, 'entry_max_hp': 80},
                             'raw': {'status': 'running', 'decision': decision, 'steps': decision - 1}}
    result.update(extra)
    return result


class FixturePolicies:
    """A deterministic fixture scorer; model equivalence is covered by test_policies."""
    identities = {'strategy': {'model_id': 'fixture-choice'}, 'battle': {'model_id': 'fixture-battle'}}
    def __init__(self):
        self.scored = []
        self.accounted = []
        self.reset = []
        self.fail = False

    def score(self, owner, payload):
        if self.fail:
            raise ValueError('unknown native card fixture')
        self.scored.append((owner, copy.deepcopy(payload)))
        n = payload['raw']['decision'] if owner == 'battle' else payload['decision']
        return {'model_id': self.identities[owner]['model_id'], 'chosen_id': f'candidate-{n}',
                'scores': [{'id': f'candidate-{n}', 'q': .75}], 'excluded_ids': []}

    def account_battle(self, before, candidate, after):
        self.accounted.append((copy.deepcopy(before), candidate, copy.deepcopy(after)))
        return {'accounted': True, 'closed': after['raw']['status'] != 'running', 'bill': 0}

    def reset_run(self, run_id):
        self.reset.append(run_id)


class ServerState:
    def __init__(self):
        self.current = observation()
        self.posts = []
        self.gets = []
        self.action_response = 'accepted'
        self.statuses = ['pending']
        self.action_delay = 0
        self.request_id = None
        self.score_response = 'ok'
        self.observation_failed = False
        self.receipt_id = None
        self.on_completed_observation = None


@pytest.fixture
def server():
    state = ServerState()
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args): pass
        def respond(self, value, status=200):
            data = json.dumps(value).encode()
            try:
                self.send_response(status); self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(data))); self.end_headers(); self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError): pass
        def do_GET(self):
            route = urlparse(self.path)
            state.gets.append(self.path)
            if route.path.endswith('/observation'):
                if state.observation_failed:self.respond({'message':'temporary game API failure'},status=503)
                else:self.respond(copy.deepcopy(state.current))
            elif route.path.endswith('/action'):
                rid = parse_qs(route.query).get('request_id', [''])[0]
                status = state.statuses.pop(0) if len(state.statuses) > 1 else state.statuses[0]
                if status == 'completed' and state.on_completed_observation is not None:
                    state.current = copy.deepcopy(state.on_completed_observation)
                self.respond({'status': status, 'request_id': state.receipt_id or rid})
            elif route.path.endswith('/control'):
                self.respond(state.current['control'])
            else:self.respond({'status': 'error'})
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            state.posts.append((urlparse(self.path).path.split('/')[-1], body))
            if self.path.endswith('/action'):
                state.request_id = body['request_id']
                if state.action_delay:time.sleep(state.action_delay)
                self.respond({'status': state.action_response, 'request_id': body['request_id']})
            elif self.path.endswith('/scores'):
                self.respond({'status': state.score_response})
            elif self.path.endswith('/control'):
                state.current['control'].update(body)
                if 'mode' in body:
                    state.current['control']['step_requested'] = body['mode'] == 'step'
                    state.current['control']['authorization_revision'] += 1
                self.respond(state.current['control'])
    http = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=http.serve_forever, daemon=True);thread.start()
    yield state, f'http://127.0.0.1:{http.server_port}/api/v1/agent'
    http.shutdown();http.server_close();thread.join(timeout=1)


def make_controller(server, tmp_path, timeout=.5):
    from sts2_autoplay.controller import Controller
    from sts2_autoplay.transport import AgentTransport
    from sts2_autoplay.recording import Journal
    state, endpoint = server
    policies = FixturePolicies()
    journal = Journal(tmp_path / 'fresh-journal', identities=policies.identities, endpoint=endpoint)
    return Controller(AgentTransport(endpoint, timeout=timeout), policies, journal), policies, journal


def posts(state, route):
    return [body for r, body in state.posts if r == route]


def test_advice_scores_without_actions_and_paused_waits(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    assert c.tick()['status'] == 'advice'
    assert len(posts(state, 'scores')) == 1 and not posts(state, 'action')
    c.tick()
    assert len(posts(state, 'scores')) == 1, 'same public revision should reuse advice'
    state.current = observation(mode='paused', decision=2)
    assert c.tick()['status'] == 'paused'
    assert len(p.scored) == 1 and not posts(state, 'action')
    j.close(0, 'fixture complete')

def test_repeated_identical_http_failure_after_recovery_pauses_again(server,tmp_path):
    c,p,j=make_controller(server,tmp_path);state,_=server
    assert c.tick()['status']=='advice'
    state.observation_failed=True
    assert c.tick()['status']=='error'
    state.observation_failed=False;state.current=observation(mode='advice')
    assert c.tick()['status']=='advice'
    assert len(posts(state,'scores'))==2
    state.observation_failed=True
    assert c.tick()['status']=='error'
    events=[json.loads(line) for line in (j.out/'events.jsonl').read_text().splitlines()]
    errors=[r['data']['message'] for r in events if r['event']=='error']
    assert len(errors)==2 and errors[0]==errors[1]
    assert len(posts(state,'control'))==2 and not posts(state,'action')
    j.close()


def test_waiting_and_unsupported_pause_without_action(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(mode='auto', ready=False)
    assert c.tick()['status'] == 'waiting'
    assert not p.scored and not posts(state, 'action')
    state.current['unsupported'] = ['public card side state unavailable']
    assert c.tick()['status'] == 'error'
    assert posts(state, 'control')[-1]['mode'] == 'paused'
    assert 'side state' in posts(state, 'control')[-1]['message']
    j.close(0, 'fixture complete')


def test_accepted_timeout_is_reconciled_without_duplicate_dispatch(server, tmp_path):
    c, p, j = make_controller(server, tmp_path, timeout=.02)
    state, _ = server
    state.current = observation(mode='auto')
    state.action_delay = .08
    state.statuses = ['pending', 'completed']
    assert c.tick()['status'] == 'uncertain'
    rid = posts(state, 'action')[0]['request_id']
    assert c.tick()['status'] == 'pending'
    state.current = observation(mode='advice', decision=2)
    c.tick();c.tick()
    assert len(posts(state, 'action')) == 1
    queries = [r for r in state.gets if '/action?' in r]
    assert queries and all(rid in q for q in queries)
    j.close(0, 'fixture complete')


def test_completed_owned_receipt_refetches_snapshot_before_foreign_pending_check_and_score(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='auto');c.tick()
    rid = posts(state, 'action')[-1]['request_id']
    state.current = observation(owner='battle', mode='auto', decision=2,
                                pending={'request_id': rid, 'status': 'pending', 'dispatched': True})
    fresh = copy.deepcopy(state.current)
    fresh['pending']['status'] = 'completed'
    fresh['revision'] = 3
    fresh['payload']['raw']['obs'] = {'player': {'hp': 63}}
    state.on_completed_observation = fresh
    state.statuses = ['completed']
    assert c.tick()['status'] == 'pending', 'the completed owned request must not be mistaken for foreign execution'
    assert not posts(state, 'control')
    assert len(p.accounted) == 1 and p.accounted[0][2] == fresh['payload']
    assert p.scored[-1][1] == fresh['payload']
    assert len(posts(state, 'action')) == 2 and posts(state, 'action')[-1]['revision'] == 3
    observations = [r for r in state.gets if r.endswith('/observation')]
    assert len(observations) == 3, 'receipt completion requires a fresh GET/observation before using readiness'
    j.close(0, 'fixture complete')


def test_fresh_pause_after_completed_receipt_revokes_saved_confirmation_permission(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step');c.tick()
    old = observation(owner='automatic', mode='advice', decision=2, phase='Confirmation', payload={},
                      candidates=[{'id': 'battle-selection-confirm', 'kind': 'confirm_selection',
                                   'label': 'Confirm', 'public': {}}])
    fresh = copy.deepcopy(old);fresh['control'].update(mode='paused', step_requested=False, authorization_revision=1)
    state.current = old;state.on_completed_observation = fresh;state.statuses = ['completed']
    assert c.tick()['status'] == 'paused'
    assert len(posts(state, 'action')) == 1 and not posts(state, 'control') and not p.accounted
    assert c.logical['authorization'] == 'revoked'
    j.close(0, 'fixture complete')


@pytest.mark.parametrize('response', ['stale', 'cancelled'])
def test_rejected_or_cancelled_request_never_accounts_an_action(server, tmp_path, response):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step')
    if response == 'stale':state.action_response = response
    else:state.statuses = [response]
    c.tick()
    state.current['control']['mode'] = 'paused';state.current['control']['step_requested'] = False
    c.tick()
    assert len(posts(state, 'action')) == 1 and not p.accounted
    j.close(0, 'fixture complete')


def test_native_predispatch_stale_auto_rescores_fresh_decision_once(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='strategy', mode='auto')
    c.tick()
    first = posts(state, 'action')[0]
    state.current = observation(owner='strategy', mode='auto', decision=2)
    state.statuses = ['stale']
    assert c.tick()['status'] == 'stale'
    assert c.pending is None and c.logical is None and not p.accounted
    assert len(posts(state, 'action')) == 1 and not posts(state, 'control')
    state.statuses = ['pending']
    c.tick()
    second = posts(state, 'action')[1]
    assert second['request_id'] != first['request_id']
    assert second['decision_id'] == 'd2' and second['candidate_id'] == 'candidate-2'
    assert [payload['decision'] for _, payload in p.scored] == [1, 2]
    assert state.current['control']['mode'] == 'auto' and not p.accounted
    j.close(0, 'fixture complete')


def test_step_selection_confirms_once_then_accounts_original_battle_choice(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step')
    c.tick()
    state.current = observation(owner='automatic', mode='advice', decision=2, phase='Confirmation',
                                payload={}, candidates=[{'id': 'battle-selection-confirm', 'kind': 'confirm_selection',
                                                         'label': 'Confirm', 'public': {}}])
    state.statuses = ['completed']
    c.tick()
    assert len(posts(state, 'action')) == 2
    assert posts(state, 'action')[-1]['candidate_id'] == 'battle-selection-confirm'
    assert posts(state, 'control')[-1]['mode'] == 'step'
    assert len(p.scored) == 1 and not p.accounted
    state.current = observation(owner='battle', mode='advice', decision=3)
    state.statuses = ['completed']
    c.tick();c.tick()
    assert len(p.accounted) == 1
    assert p.accounted[0][1] == 'candidate-1'
    assert p.accounted[0][2]['raw']['decision'] == 3
    assert len(posts(state, 'action')) == 2
    j.close(0, 'fixture complete')


def test_pause_prevents_internal_confirmation(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step');c.tick()
    state.current = observation(owner='automatic', mode='paused', decision=2, phase='Confirmation', payload={},
                                candidates=[{'id': 'battle-selection-confirm', 'kind': 'confirm_selection',
                                             'label': 'Confirm', 'public': {}}])
    state.statuses = ['completed'];c.tick();c.tick()
    assert len(posts(state, 'action')) == 1 and not p.accounted
    assert not posts(state, 'control')
    state.current['control']['mode'] = 'advice'
    c.tick()
    assert len(posts(state, 'action')) == 1, 'Advice after a user pause must not restore old Step authorization'
    j.close(0, 'fixture complete')


def test_explicit_advice_revokes_step_confirmation_but_native_consumption_does_not(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step');c.tick()
    state.current = observation(owner='automatic', mode='advice', decision=2, phase='Confirmation', payload={},
                                candidates=[{'id': 'battle-selection-confirm', 'kind': 'confirm_selection',
                                             'label': 'Confirm', 'public': {}}])
    state.current['control']['authorization_revision'] = 1  # User pressed Advice, not automatic Step consumption.
    state.statuses = ['completed'];c.tick();c.tick()
    assert len(posts(state, 'action')) == 1 and not posts(state, 'control')
    j.close(0, 'fixture complete')


def test_terminal_battle_accounted_once_before_next_strategy_score(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='auto');c.tick()
    terminal = copy.deepcopy(state.current['payload']);terminal['raw'].update(status='win', decision=2, steps=1)
    state.current = observation(mode='advice', decision=2, battle_terminal=terminal)
    state.statuses = ['completed'];c.tick();c.tick()
    assert len(p.accounted) == 1 and p.accounted[0][2]['raw']['status'] == 'win'
    assert p.scored[-1][0] == 'strategy'
    assert len(posts(state, 'action')) == 1
    j.close(0, 'fixture complete')


def test_old_run_terminal_accounts_before_fresh_run_resets_context(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='auto');c.tick()
    terminal = copy.deepcopy(state.current['payload']);terminal['raw'].update(status='win', decision=2, steps=1)
    state.current = observation(mode='advice', run_id='new-run', battle_terminal=terminal)
    state.statuses = ['completed'];c.tick();c.tick()
    assert len(p.accounted) == 1 and p.accounted[0][2]['run_id'] == 'live-run'
    assert p.reset == ['live-run'] and p.scored[-1][0] == 'strategy'
    assert len(posts(state, 'action')) == 1
    j.close(0, 'fixture complete')


@pytest.mark.parametrize('status,abandoned', [('victory', False), ('defeat', False), ('defeat', True)])
def test_explicit_run_outcome_follows_last_battle_accounting_once_and_allows_fresh_advice(server, tmp_path, status, abandoned):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='auto');c.tick()
    terminal = copy.deepcopy(state.current['payload'])
    terminal['raw'].update(status='win' if status == 'victory' else 'loss', decision=2, steps=1)
    outcome = {'run_id': 'live-run', 'status': status, 'abandoned': abandoned,
               'utc': '2026-10-12T01:02:03Z'}
    state.current = observation(owner='none', mode='paused', ready=False, decision=2, phase='GameOver',
                                battle_terminal=terminal, run_terminal=outcome)
    state.statuses = ['pending', 'completed']
    assert c.tick()['status'] == 'pending'
    assert not p.accounted
    assert c.tick()['status'] == 'complete'
    c.tick()
    assert len(p.accounted) == 1 and len(posts(state, 'action')) == 1
    state.current = observation(mode='advice', run_id='new-run', run_terminal=outcome)
    assert c.tick()['status'] == 'advice'
    assert p.reset == ['live-run'] and p.scored[-1][0] == 'strategy'
    assert not posts(state, 'control'), 'an already paused old outcome must not revoke Advice in the fresh run'
    j.close(0, 'fixture complete')
    events = [json.loads(line) for line in (tmp_path / 'fresh-journal/events.jsonl').read_text().splitlines()]
    outcomes = [e for e in events if e['event'] == 'run_outcome']
    assert len(outcomes) == 1 and outcomes[0]['data']['outcome'] == outcome
    assert outcomes[0]['run_id'] == 'live-run'
    assert next(e['sequence'] for e in events if e['event'] == 'battle_accounted') < outcomes[0]['sequence']


@pytest.mark.parametrize('owner', ['automatic', 'strategy'])
@pytest.mark.parametrize('status', ['victory', 'defeat'])
def test_completed_nonbattle_terminal_action_settles_once_before_natural_outcome(server, tmp_path, owner, status):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner=owner, mode='auto', phase='Event')
    assert c.tick()['status'] == 'pending'
    outcome = {'run_id': 'live-run', 'status': status, 'abandoned': False,
               'utc': '2026-10-12T01:02:03Z'}
    state.current = observation(owner='none', mode='paused', ready=False, decision=2,
                                phase='GameOver', run_terminal=outcome)
    state.statuses = ['pending', 'completed']
    assert c.tick()['status'] == 'pending', 'the explicit outcome cannot replace a missing completion receipt'
    assert c.logical is not None and not p.accounted
    assert c.tick()['status'] == 'complete'
    assert c.logical is None and c.abandoned is None
    assert c.tick()['status'] == 'complete'
    assert len(posts(state, 'action')) == 1 and not p.accounted
    j.close(0, 'fixture complete')
    events = [json.loads(line) for line in (tmp_path / 'fresh-journal/events.jsonl').read_text().splitlines()]
    settled = [e for e in events if e['event'] == 'logical_action_settled']
    outcomes = [e for e in events if e['event'] == 'run_outcome']
    assert len(settled) == len(outcomes) == 1
    assert settled[0]['sequence'] < outcomes[0]['sequence']
    assert settled[0]['data']['after']['run_terminal'] == outcome
    assert not any(e['event'] in ('logical_action_abandoned', 'battle_accounted') for e in events)


def test_completed_automatic_action_does_not_borrow_an_abandoned_outcome(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='automatic', mode='auto', phase='Event');c.tick()
    outcome = {'run_id': 'live-run', 'status': 'defeat', 'abandoned': True,
               'utc': '2026-10-12T01:02:03Z'}
    state.current = observation(owner='none', mode='paused', ready=False, decision=2,
                                phase='GameOver', run_terminal=outcome)
    state.statuses = ['completed']
    assert c.tick()['status'] == 'complete'
    assert c.abandoned is not None and not p.accounted
    j.close(0, 'fixture complete')
    events = [json.loads(line) for line in (tmp_path / 'fresh-journal/events.jsonl').read_text().splitlines()]
    assert not any(e['event'] == 'logical_action_settled' for e in events)
    assert len([e for e in events if e['event'] == 'logical_action_abandoned']) == 1


def test_malformed_run_outcome_pauses_without_scoring_or_action(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(mode='auto', run_terminal={'run_id': 'live-run', 'status': 'unknown',
                                'abandoned': False, 'utc': '2026-10-12T01:02:03Z'})
    assert c.tick()['status'] == 'error'
    assert not p.scored and not posts(state, 'action')
    assert posts(state, 'control')[-1]['mode'] == 'paused'
    j.close(0, 'fixture complete')


def test_abandoned_run_records_outcome_without_inventing_missing_battle_accounting(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step');c.tick()
    outcome = {'run_id': 'live-run', 'status': 'defeat', 'abandoned': True,
               'utc': '2026-10-12T01:02:03Z'}
    state.current = observation(owner='none', mode='paused', ready=False, decision=2, phase='GameOver',
                                battle_terminal=None, run_terminal=outcome)
    state.statuses = ['completed']
    assert c.tick()['status'] == 'complete'
    assert not p.accounted and c.logical is None
    state.current = observation(mode='advice', run_id='new-run')
    assert c.tick()['status'] == 'advice' and p.reset == ['live-run']
    j.close(0, 'fixture complete')
    events = [json.loads(line) for line in (tmp_path / 'fresh-journal/events.jsonl').read_text().splitlines()]
    assert len([e for e in events if e['event'] == 'run_outcome']) == 1
    assert len([e for e in events if e['event'] == 'logical_action_abandoned']) == 1


def test_unknown_execution_status_pauses_and_never_retries(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(mode='auto');c.tick()
    state.statuses = ['unknown'];c.tick();c.tick()
    assert posts(state, 'control')[-1]['mode'] == 'paused'
    assert len(posts(state, 'action')) == 1
    j.close(0, 'fixture complete')


def test_unrelated_completed_receipt_cannot_account_this_request(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='auto');c.tick()
    state.current = observation(owner='battle', mode='advice', decision=2)
    state.statuses = ['completed'];state.receipt_id = 'another-request'
    assert c.tick()['status'] == 'error'
    assert not p.accounted and len(posts(state, 'action')) == 1
    assert posts(state, 'control')[-1]['mode'] == 'paused'
    j.close(0, 'fixture complete')


def test_missing_native_authorization_revision_pauses(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(mode='auto')
    del state.current['control']['authorization_revision']
    assert c.tick()['status'] == 'error'
    assert not p.scored and not posts(state, 'action')
    j.close(0, 'fixture complete')


def test_cancelled_confirmation_can_resume_only_from_new_explicit_step(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step');c.tick()
    state.current = observation(owner='automatic', mode='advice', decision=2, phase='Confirmation', payload={},
                                candidates=[{'id': 'battle-selection-confirm', 'kind': 'confirm_selection',
                                             'label': 'Confirm', 'public': {}}])
    state.statuses = ['completed'];c.tick()  # Original Step includes one internal confirm.
    state.current['control']['mode'] = 'paused'
    state.statuses = ['cancelled'];c.tick()
    state.current['control'].update(mode='step', step_requested=True, authorization_revision=2)
    state.statuses = ['pending']
    c.tick()
    assert len(posts(state, 'action')) == 3, 'authoritative cancellation permits a newly authorized confirmation'
    assert not p.accounted
    j.close(0, 'fixture complete')


@pytest.mark.parametrize('continuation', [False, True])
def test_failed_execution_abandons_accounting_freezes_run_and_recovers_on_new_run(server, tmp_path, continuation):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step' if continuation else 'auto');c.tick()
    if continuation:
        state.current = observation(owner='automatic', mode='advice', decision=2, phase='Confirmation', payload={},
                                    candidates=[{'id': 'battle-selection-confirm', 'kind': 'confirm_selection',
                                                 'label': 'Confirm', 'public': {}}])
        state.statuses = ['completed'];c.tick()
    state.statuses = ['failed']
    assert c.tick()['status'] == 'error'
    action_count = len(posts(state, 'action'))
    scored_count = len(p.scored)
    state.current = observation(owner='battle', mode='auto', decision=3)
    assert c.tick()['status'] == 'error'
    assert not p.accounted and len(p.scored) == scored_count and len(posts(state, 'action')) == action_count
    state.current = observation(mode='advice', run_id='new-run', decision=1)
    assert c.tick()['status'] == 'advice', 'a failed old run must not stall fresh-run reset'
    assert p.reset == ['live-run'] and len(p.scored) == scored_count + 1
    assert not p.accounted and len(posts(state, 'action')) == action_count
    j.close(0, 'fixture complete')
    events = [json.loads(line) for line in (tmp_path / 'fresh-journal/events.jsonl').read_text().splitlines()]
    abandoned = [e for e in events if e['event'] == 'logical_action_abandoned']
    assert len(abandoned) == 1 and abandoned[0]['data']['accounting_performed'] is False
    assert abandoned[0]['data']['receipt']['status'] == 'failed'


@pytest.mark.parametrize('response', ['cancelled', 'stale', 'paused', 'waiting'])
def test_rejected_confirmation_does_not_block_fresh_run_reset(server, tmp_path, response):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='step');c.tick()
    state.current = observation(owner='automatic', mode='advice', decision=2, phase='Confirmation', payload={},
                                candidates=[{'id': 'battle-selection-confirm', 'kind': 'confirm_selection',
                                             'label': 'Confirm', 'public': {}}])
    state.statuses = ['completed'];c.tick()
    state.statuses = [response]
    assert c.tick()['status'] == response
    state.current = observation(mode='advice', run_id='new-run')
    assert c.tick()['status'] == 'advice'
    assert p.reset == ['live-run'] and not p.accounted
    assert len(posts(state, 'action')) == 2
    j.close(0, 'fixture complete')


def test_failed_admission_is_abandoned_without_reconciliation_or_resend(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='battle', mode='auto');state.action_response = 'failed'
    assert c.tick()['status'] == 'error'
    state.current = observation(owner='battle', mode='auto', decision=2)
    assert c.tick()['status'] == 'error'
    assert c.pending is None and c.logical is None and c.abandoned['run_id'] == 'live-run'
    assert not p.accounted and len(posts(state, 'action')) == 1
    assert not any('/action?' in route for route in state.gets)
    j.close(0, 'fixture complete')


def test_stale_scores_are_not_executed(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(mode='auto');state.score_response = 'stale'
    assert c.tick()['status'] == 'stale'
    assert not posts(state, 'action')
    j.close(0, 'fixture complete')


def test_policy_failure_visible_pause_and_fresh_journal_refuses_overwrite(server, tmp_path):
    from sts2_autoplay.recording import Journal
    c, p, j = make_controller(server, tmp_path)
    state, endpoint = server
    p.fail = True;state.current = observation(mode='auto')
    assert c.tick()['status'] == 'error'
    assert posts(state, 'control')[-1]['mode'] == 'paused' and not posts(state, 'action')
    with pytest.raises(FileExistsError):Journal(tmp_path / 'fresh-journal', identities={}, endpoint=endpoint)
    j.close(0, 'fixture complete')
    events = [json.loads(line) for line in (tmp_path / 'fresh-journal/events.jsonl').read_text().splitlines()]
    assert any(e['event'] == 'observation' for e in events)
    assert any(e['event'] == 'error' and 'unknown native card' in str(e) for e in events)
    assert events[-1]['event'] == 'service_exit'


def test_native_recorder_reference_retained_without_modifying_recorder(tmp_path):
    from sts2_autoplay.recording import Journal
    native = tmp_path / 'native-recorder';native.mkdir()
    marker = native / 'manifest.json';marker.write_text('{"native":true}')
    j = Journal(tmp_path / 'owned', identities={}, endpoint='fixture')
    j.link_native_recording(native)
    j.close(0, 'fixture complete')
    manifest = json.loads((tmp_path / 'owned/manifest.json').read_text())
    assert manifest['native_recording_directory'] == str(native.resolve())
    assert marker.read_text() == '{"native":true}'


def test_cli_signal_closes_journal_and_native_mode_using_real_frozen_assets(server, tmp_path):
    state, endpoint = server
    state.current = observation(owner='none', ready=False)
    owned = Path(__file__).resolve().parents[1]
    out = tmp_path / 'signal-journal'
    env = dict(os.environ, PYTHONPATH=str(owned / 'python'), PYTHONDONTWRITEBYTECODE='1')
    process = subprocess.Popen([sys.executable, '-m', 'sts2_autoplay', '--project-root',
        str(owned.parent), '--endpoint', endpoint, '--out', str(out),
        '--poll-seconds', '.02'], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        deadline = time.monotonic() + 10
        while not (out / 'manifest.json').exists() and process.poll() is None and time.monotonic() < deadline:
            time.sleep(.02)
        assert (out / 'manifest.json').exists(), 'real frozen service did not initialize'
        process.send_signal(signal.SIGTERM)
        stdout, stderr = process.communicate(timeout=5)
        assert process.returncode == 143, (stdout, stderr)
        closed = json.loads((out / 'SERVICE_CLOSED.json').read_text())
        assert closed['reason'] == 'signal 15' and closed['requested_exit_code'] == 143
        assert posts(state, 'control')[-1]['mode'] == 'paused'
        assert not posts(state, 'action')
    finally:
        if process.poll() is None:process.kill();process.wait(timeout=2)


def test_natural_terminal_pauses_autoplay_and_records_actual_snapshot(server, tmp_path):
    c, p, j = make_controller(server, tmp_path)
    state, _ = server
    state.current = observation(owner='none', mode='auto', ready=False, phase='Complete')
    assert c.tick()['status'] == 'complete'
    assert posts(state, 'control')[-1]['mode'] == 'paused'
    assert not posts(state, 'action')
    j.close(0, 'fixture complete')
    events = [json.loads(line) for line in (tmp_path / 'fresh-journal/events.jsonl').read_text().splitlines()]
    assert any(e['event'] == 'run_terminal' and e['data']['snapshot']['phase'] == 'Complete' for e in events)


def test_cli_initialization_failure_pauses_native_service(server, tmp_path):
    state, endpoint = server
    owned = Path(__file__).resolve().parents[1]
    env = dict(os.environ, PYTHONPATH=str(owned / 'python'), PYTHONDONTWRITEBYTECODE='1')
    process = subprocess.run([sys.executable, '-m', 'sts2_autoplay', '--project-root', str(tmp_path / 'missing-project'),
        '--endpoint', endpoint, '--out', str(tmp_path / 'failed-journal')], env=env, capture_output=True, text=True, timeout=5)
    assert process.returncode == 1
    assert posts(state, 'control')[-1]['mode'] == 'paused'
    assert not posts(state, 'action')

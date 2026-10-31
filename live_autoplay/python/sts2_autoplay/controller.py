"""One-request controller. Advice is the startup mode; the game owns action controls."""
from __future__ import annotations

import copy
import hashlib
import json
import math
import time
import uuid

from .transport import TransportError


def _fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, allow_nan=False, separators=(',', ':')).encode()).hexdigest()


def _check(condition, message):
    if not condition:raise ValueError(message)


class Controller:
    def __init__(self, transport, policies, journal, *, clock=time.monotonic, settlement_timeout=45.0):
        self.transport, self.policies, self.journal = transport, policies, journal
        self.clock, self.settlement_timeout = clock, float(settlement_timeout)
        self.pending = None
        self.logical = None
        self.abandoned = None
        self.score_key = self.score_payload = self.score_result = None
        self.last_observation = None
        self.run_id = None
        self.pause_message = None
        self.recorded_outcomes = {}

    def _record(self, name, data, durable=False):
        return self.journal.event(name, data, run_id=self.run_id, durable=durable)

    def _pause(self, message):
        message = str(message)
        self.score_key=None
        if message != self.pause_message:
            self._record('error', {'message': message, 'pending': self.pending}, True)
            self.pause_message = message
        try:
            self.transport.control(mode='paused', message=message)
        except TransportError as e:
            self._record('pause_delivery_failed', {'message': str(e)})
        return {'status': 'error', 'message': message}

    def _abandon(self, reason, *, receipt=None):
        """Retain uncertain effects as evidence without inventing reward accounting."""
        logical = self.logical
        if logical is None:return
        self.abandoned = {'run_id': logical['before']['run_id'], 'reason': str(reason),
                          'request_id': logical['request_id']}
        self._record('logical_action_abandoned', {'logical': logical, 'pending': self.pending,
                                                'receipt': receipt, 'reason': str(reason),
                                                'accounting_performed': False}, True)
        self.pending = self.logical = None
        self.score_key = self.score_payload = self.score_result = None

    @staticmethod
    def _validate_observation(obs):
        _check(obs.get('schema') == 'sts2.live_agent/v1', 'native agent schema differs')
        _check(isinstance(obs.get('run_id'), str) and bool(obs['run_id']), 'native run id missing')
        _check(isinstance(obs.get('decision_id'), str), 'native decision id missing')
        _check(type(obs.get('revision')) is int and obs['revision'] >= 0, 'native revision missing')
        _check(obs.get('owner') in ('battle', 'strategy', 'automatic', 'none'), 'unknown native decision owner')
        _check(type(obs.get('ready')) is bool, 'native readiness missing')
        _check(isinstance(obs.get('payload'), dict) and isinstance(obs.get('candidates'), list), 'native payload/candidates missing')
        _check(isinstance(obs.get('unsupported'), list), 'native unsupported reasons missing')
        control = obs.get('control') or {}
        _check(control.get('mode') in ('advice', 'step', 'auto', 'paused'), 'unknown native control mode')
        _check(type(control.get('authorization_revision')) is int and control['authorization_revision'] >= 0,
               'native control authorization_revision missing')
        ids = [row.get('id') for row in obs['candidates']]
        _check(all(isinstance(cid, str) and cid for cid in ids) and len(ids) == len(set(ids)), 'native candidate ids invalid/duplicated')
        terminal = obs.get('run_terminal')
        if terminal is not None:
            _check(isinstance(terminal, dict), 'native run outcome is not an object')
            _check(isinstance(terminal.get('run_id'), str) and bool(terminal['run_id']), 'native run outcome id missing')
            _check(terminal.get('status') in ('victory', 'defeat'), 'native run outcome must be victory or defeat')
            _check(type(terminal.get('abandoned')) is bool, 'native run outcome abandoned flag missing')
            _check(isinstance(terminal.get('utc'), str) and bool(terminal['utc']), 'native run outcome timestamp missing')

    def _run_outcome(self, obs):
        """Record the native outcome once, after any confirmed final Battle settles."""
        terminal = obs.get('run_terminal')
        if terminal is not None:
            run_id = terminal['run_id']
            signature = _fingerprint(terminal)
            previous = self.recorded_outcomes.get(run_id)
            _check(previous is None or previous == signature, 'native run outcome changed after it was recorded')
            if previous is None:
                self.journal.event('run_outcome', {'outcome': terminal, 'snapshot': obs}, run_id=run_id, durable=True)
                self.recorded_outcomes[run_id] = signature
        ended = obs['run_id'] in self.recorded_outcomes
        if ended and obs['control']['mode'] != 'paused':
            self.transport.control(mode='paused', message='Native run ended. Outcome recorded; autoplay stopped.')
        return ended

    @staticmethod
    def _confirmation(obs):
        if obs['owner'] != 'automatic' or not obs['ready'] or len(obs['candidates']) != 1:return False
        candidate = obs['candidates'][0]
        return (obs['phase'] in ('Confirmation', 'PhaseConfirmation')
                or candidate['kind'] in ('confirm_selection', 'confirm_bundle_selection')
                or candidate['id'] in ('select:confirm', 'bundle:confirm', 'battle-selection-confirm'))

    def _reconcile(self, obs):
        """Only query a dispatched request; never send it again after uncertainty."""
        if self.pending is None:return None
        pending = self.pending
        try:
            reply = self.transport.action_status(pending['request']['request_id'])
        except TransportError as e:
            self._record('reconcile_unavailable', {'request_id': pending['request']['request_id'], 'error': str(e)})
            return self._pause('Cannot reconcile the pending native action: ' + str(e))
        _check(reply.get('request_id') == pending['request']['request_id'], 'native action receipt request_id differs')
        status = reply.get('status')
        self._record('action_receipt', {'request': pending['request'], 'receipt': reply, 'after': obs}, status != 'pending')
        if status in ('accepted', 'pending'):
            if self.clock() - pending['started'] > self.settlement_timeout:
                return self._pause('Native action is still pending beyond the settlement timeout; no retry was sent.')
            return {'status': 'pending', 'request_id': pending['request']['request_id']}
        if status == 'unknown':
            return self._pause('Execution status is unknown for the pending request; inspect the game before recovery. No retry was sent.')
        if status in ('cancelled', 'stale', 'paused', 'waiting'):
            self.pending = None
            if pending['continuation']:self.logical['native_completed'] = True
            else:self.logical = None
            return {'status': status}
        if status == 'failed':
            message = 'Native action failed; this run is frozen because its effects cannot be accounted safely: ' + str(reply.get('message') or 'unknown cause')
            self._abandon(message, receipt=reply)
            return self._pause(message)
        _check(status == 'completed', f'unknown action receipt status {status!r}')
        self.pending = None
        if self.logical is not None:self.logical['native_completed'] = True
        return None

    def _battle_after(self, obs):
        if self.logical is None or self.logical['owner'] != 'battle':return None
        previous = self.logical['before']['payload']
        candidates = []
        if obs['owner'] == 'battle' and obs['ready']:candidates.append(obs['payload'])
        if isinstance(obs.get('battle_terminal'), dict):candidates.append(obs['battle_terminal'])
        for candidate in candidates:
            if (candidate.get('run_id'), candidate.get('fight_id')) != (previous.get('run_id'), previous.get('fight_id')):continue
            raw = candidate.get('raw') or {}
            if raw.get('status') == 'running' and not obs['ready']:continue
            if type(raw.get('decision')) is int and raw['decision'] > previous['raw']['decision']:
                return candidate
        return None

    def _settle_logical(self, obs):
        logical = self.logical
        if logical is None:return None
        if not logical['native_completed']:
            if self.pending is None:
                message = 'Logical action lost its native receipt; this run is frozen without Battle accounting.'
                self._abandon(message)
                return self._pause(message)
            return {'status': 'pending'}
        # A terminal reply retains the old run ID even when the UI is already in
        # the menu or a fresh run. Settle that exact reply before resetting context.
        if logical['owner'] == 'battle':
            after = self._battle_after(obs)
            if after is not None:
                receipt = self.policies.account_battle(logical['before']['payload'], logical['candidate_id'], after)
                self._record('battle_accounted', {'request_id': logical['request_id'], 'before': logical['before']['payload'],
                                                'candidate_id': logical['candidate_id'], 'after': after, 'receipt': receipt}, True)
                self.logical = None
                return None
        terminal = obs.get('run_terminal')
        if terminal is not None and terminal['run_id'] == logical['before']['run_id']:
            if logical['owner'] != 'battle' and terminal['abandoned'] is False:
                # A confirmed ordinary continuation can itself finish the run
                # (for example the Architect's final Proceed). Its explicit
                # matching natural outcome settles that non-Battle action;
                # Battle units still require the exact terminal snapshot above.
                self._record('logical_action_settled', {'request_id': logical['request_id'], 'after': obs}, True)
                self.logical = None
                return None
            self._abandon('Native run ended without a matching settled policy snapshot; no Battle accounting was performed.')
            return None
        if obs['run_id'] != logical['before']['run_id']:
            self._abandon('Native run changed before the original logical action supplied a matching settled snapshot.')
            return None
        if self._confirmation(obs):
            if obs['control']['mode'] == 'paused':return {'status': 'paused'}
            _check(obs['run_id'] == logical['before']['run_id'], 'selection confirmation belongs to a different run')
            if logical['authorization'] == 'step' and obs['control']['mode'] == 'advice':
                # The native Step button consumed its request on card selection. Its
                # already-authorized logical choice includes this one confirmation.
                reply = self.transport.control(mode='step', message='Confirming the selected card for the current Step.')
                _check(reply.get('mode') == 'step', 'native control refused the bound confirmation')
                logical['authorization_revision'] = reply.get('authorization_revision')
                obs = copy.deepcopy(obs);obs['control'] = reply
            if obs['control']['mode'] not in ('auto', 'step'):return {'status': 'advice'}
            if (obs['control']['mode'] == 'step' and logical['authorization'] != 'step'
                    and obs['control'].get('step_requested') is not True):return {'status': 'advice'}
            _check(logical['continuations'] < 8, 'too many internal confirmation stages')
            logical['continuations'] += 1
            return self._dispatch(obs, obs['candidates'][0]['id'], continuation=True)
        if logical['owner'] != 'battle' and obs['decision_id'] != logical['before']['decision_id'] and (obs['ready'] or obs['owner'] == 'none'):
            self._record('logical_action_settled', {'request_id': logical['request_id'], 'after': obs}, True)
            self.logical = None
            return None
        if obs['control']['mode'] == 'paused':return {'status': 'paused'}
        if self.clock() - logical['started'] > self.settlement_timeout:
            return self._pause('The completed action has no matching settled policy snapshot or terminal Battle reply.')
        return {'status': 'waiting_settlement'}

    def _dispatch(self, obs, candidate_id, *, continuation=False):
        _check(self.pending is None, 'another native request is already pending')
        request = {'request_id': 'autoplay-' + uuid.uuid4().hex, 'run_id': obs['run_id'],
                   'decision_id': obs['decision_id'], 'revision': obs['revision'], 'candidate_id': candidate_id}
        pending = {'request': request, 'before': copy.deepcopy(obs), 'started': self.clock(),
                   'continuation': continuation}
        if not continuation:
            self.logical = {'request_id': request['request_id'], 'before': copy.deepcopy(obs), 'owner': obs['owner'],
                            'candidate_id': candidate_id, 'authorization': obs['control']['mode'],
                            'authorization_revision': obs['control'].get('authorization_revision'),
                            'native_completed': False, 'started': pending['started'], 'continuations': 0}
        else:self.logical['native_completed'] = False
        # Persist intent before the network mutation. A timeout leaves this request
        # pending, with the same ID, until an authoritative GET/action receipt.
        self.pending = pending
        self._record('action_dispatch', {'request': request, 'before': obs, 'continuation': continuation}, True)
        try:
            reply = self.transport.submit_action(request)
        except TransportError as e:
            self._record('action_uncertain', {'request': request, 'error': str(e)}, True)
            return {'status': 'uncertain', 'request_id': request['request_id']}
        self._record('action_admission', {'request': request, 'receipt': reply}, True)
        _check(reply.get('request_id') == request['request_id'], 'native action admission request_id differs')
        status = reply.get('status')
        if status in ('accepted', 'pending', 'completed'):
            return {'status': 'pending', 'request_id': request['request_id']}
        if status in ('stale', 'paused', 'waiting', 'cancelled'):
            self.pending = None
            if continuation:self.logical['native_completed'] = True
            else:self.logical = None
            return {'status': status}
        if status == 'failed':
            message = 'Native action admission failed; this run is frozen because its effects cannot be accounted safely: ' + str(reply.get('message') or 'unknown cause')
            self._abandon(message, receipt=reply)
            return self._pause(message)
        return self._pause('Native action admission refused: ' + str(status) + ': ' + str(reply.get('message') or ''))

    def _scores(self, obs):
        key = (obs['run_id'], obs['decision_id'], obs['revision'], obs['owner'])
        payload = _fingerprint(obs['payload'])
        if key == self.score_key:
            _check(payload == self.score_payload, 'native public payload changed without a new revision')
            return self.score_result, False
        result = self.policies.score(obs['owner'], obs['payload'])
        scores = result.get('scores') or []
        ids = [row['id'] for row in scores]
        excluded = result.get('excluded_ids') or []
        _check(scores and len(ids) == len(set(ids)) and len(excluded) == len(set(excluded))
               and not set(ids) & set(excluded), 'policy score/exclusion IDs invalid')
        _check(set(ids) | set(excluded) == {c['id'] for c in obs['candidates']}, 'policy/native candidate identities differ')
        _check(all(isinstance(row['q'], (int, float)) and math.isfinite(row['q']) for row in scores), 'nonfinite policy score')
        _check(result['chosen_id'] in ids, 'policy selected candidate is not scored')
        qmap = {row['id']: row['q'] for row in scores}
        _check(qmap[result['chosen_id']] == max(qmap.values()), 'policy selected candidate is not greedy')
        body = {'decision_id': obs['decision_id'], 'revision': obs['revision'], 'model_id': result['model_id'],
                'chosen_id': result['chosen_id'], 'scores': scores, 'excluded_ids': excluded}
        self._record('scores', {'before': obs, 'result': result}, True)
        reply = self.transport.submit_scores(body)
        if reply.get('status') == 'stale':return None, True
        _check(reply.get('status') == 'ok', 'native score submission failed: ' + str(reply))
        self._record('scores_published', {'decision_id':obs['decision_id'], 'revision':obs['revision']})
        self.pause_message=None
        self.score_key, self.score_payload, self.score_result = key, payload, copy.deepcopy(result)
        return result, False

    def _observe(self):
        obs = self.transport.observation()
        self._validate_observation(obs)
        if obs['control']['mode'] == 'paused':self.score_key = None
        if self.logical is not None:
            control = obs['control']
            if (control['mode'] == 'paused'
                    or control.get('authorization_revision') != self.logical.get('authorization_revision')):
                # Pause/explicit Advice invalidates the saved permission. Native
                # Step consumption keeps its authorization_revision unchanged.
                self.logical['authorization'] = 'revoked'
        if self.run_id is None:self.run_id = obs['run_id']
        signature = _fingerprint(obs)
        if signature != self.last_observation:
            self._record('observation', obs)
            self.last_observation = signature
        return obs

    def tick(self):
        try:
            obs = self._observe()
            had_pending = self.pending is not None
            reconciled = self._reconcile(obs)
            if reconciled is not None:return reconciled
            if had_pending:
                # GET/action completed after the earlier observation was captured.
                # Refetch readiness, controls and public state before accounting or
                # dispatch; the old obs may still report our own request pending.
                obs = self._observe()
            settled = self._settle_logical(obs)
            if settled is not None:return settled
            ended_by_outcome = self._run_outcome(obs)
            if self.run_id != obs['run_id']:
                ended = obs['run_id'] == 'menu' and self.run_id != 'menu'
                self.policies.reset_run(self.run_id)
                self._record('run_changed', {'previous_run_id': self.run_id, 'new_run_id': obs['run_id']}, True)
                self.run_id = obs['run_id'];self.score_key = None
                self.abandoned = None
                if ended:
                    self._record('run_terminal', {'snapshot': obs, 'meaning': 'native run left play; consult recorder for outcome'}, True)
                    self.transport.control(mode='paused', message='Run left play. Autoplay stopped; records retained.')
                    return {'status': 'complete', 'phase': obs['phase']}
            if ended_by_outcome:return {'status': 'complete', 'phase': obs['phase']}
            if self.abandoned is not None:
                return self._pause(self.abandoned['reason'])
            if obs['unsupported']:
                return self._pause('Native projection refused: ' + '; '.join(map(str, obs['unsupported'])))
            if obs['owner'] == 'none' and obs['phase'] in ('Complete', 'GameOver', 'Victory', 'Defeat', 'RunComplete'):
                if obs['control']['mode'] != 'paused':
                    self._record('run_terminal', {'snapshot': obs}, True)
                    self.transport.control(mode='paused', message='Native run ended. Autoplay stopped; records retained.')
                return {'status': 'complete', 'phase': obs['phase']}
            if obs['control']['mode'] == 'paused':return {'status': 'paused'}
            if not obs['ready'] or obs['owner'] == 'none':return {'status': 'waiting'}
            foreign_pending = obs.get('pending') or {}
            if foreign_pending.get('status') == 'pending':
                return self._pause('Another native action is pending without this controller ownership.')
            result = None
            if obs['owner'] in ('strategy', 'battle'):
                result, stale = self._scores(obs)
                if stale:return {'status': 'stale'}
            else:
                _check(len(obs['candidates']) == 1, 'automatic decision must have exactly one deterministic candidate')
            control = obs['control']
            authorized = control['mode'] == 'auto' or (control['mode'] == 'step' and control.get('step_requested') is True)
            if not authorized:return {'status': 'advice'}
            return self._dispatch(obs, result['chosen_id'] if result else obs['candidates'][0]['id'])
        except (TransportError, ValueError, KeyError, TypeError) as e:
            return self._pause(f'{type(e).__name__}: {e}')

    def close(self, exit_code=0, reason='closed'):
        try:self.transport.control(mode='paused', message='Frozen inference service is stopping.')
        except TransportError:pass
        self.journal.close(exit_code, reason, pending=self.pending)

    def run(self, stop, *, poll_seconds=.15):
        _check(poll_seconds > 0, 'poll interval must be positive')
        # A fresh service begins in Advice even if a prior connection left Auto
        # selected. Until this succeeds it never enters the scoring/action loop.
        # The native heading is static; name a non-default Choice preset in the status line.
        label = getattr(self.policies, 'label', None) if getattr(self.policies, 'choice_preset', 'n2-65k') != 'n2-65k' else None
        connected = f'Frozen policies connected: {label}. Advice mode.' if label else 'Frozen policies connected. Advice mode.'
        while not stop.is_set():
            try:
                self.transport.control(mode='advice', message=connected)
                break
            except TransportError as e:
                self._record('startup_wait', {'error': str(e)})
                stop.wait(poll_seconds)
        while not stop.is_set():
            self.tick()
            stop.wait(poll_seconds)

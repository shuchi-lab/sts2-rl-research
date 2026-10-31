"""Local JSON transport. Mutating requests are never retried implicitly."""
from __future__ import annotations

import json
import socket
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener


class TransportError(RuntimeError):
    """A failed exchange; a POST/action may already have been accepted."""


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class AgentTransport:
    def __init__(self, endpoint='http://127.0.0.1:15526/api/v1/agent', *, timeout=2.0):
        endpoint = endpoint.rstrip('/')
        parsed = urlparse(endpoint)
        if parsed.scheme != 'http' or parsed.hostname not in ('127.0.0.1', 'localhost', '::1'):
            raise ValueError('Agent endpoint must be localhost HTTP')
        if parsed.query or parsed.fragment or parsed.username or parsed.password:
            raise ValueError('Agent endpoint must have no credentials, query or fragment')
        if timeout <= 0 or timeout > 30:
            raise ValueError('transport timeout must be in (0,30] seconds')
        self.endpoint, self.timeout = endpoint, float(timeout)
        self.opener = build_opener(_NoRedirect())

    def _request(self, route, body=None, *, query=None):
        url = self.endpoint + '/' + route
        if query:
            url += '?' + urlencode(query)
        data = None if body is None else json.dumps(body, allow_nan=False, separators=(',', ':')).encode()
        req = Request(url, data=data, headers={'Content-Type': 'application/json', 'Accept': 'application/json'},
                      method='GET' if body is None else 'POST')
        try:
            with self.opener.open(req, timeout=self.timeout) as response:
                raw = response.read(16 * 1024 * 1024 + 1)
            if len(raw) > 16 * 1024 * 1024:
                raise TransportError('Agent response exceeds16MiB')
            result = json.loads(raw, parse_constant=lambda x: (_ for _ in ()).throw(ValueError(f'nonfinite JSON {x}')))
            if not isinstance(result, dict):
                raise TransportError('Agent response must be a JSON object')
            return result
        except HTTPError as e:
            message = e.read(4096).decode(errors='replace')
            raise TransportError(f'{req.method} {route}: HTTP{e.code}: {message}') from e
        except (URLError, TimeoutError, socket.timeout, ConnectionError, OSError, ValueError) as e:
            raise TransportError(f'{req.method} {route}: {type(e).__name__}: {e}') from e

    def observation(self):
        return self._request('observation')

    def submit_scores(self, scores):
        return self._request('scores', scores)

    def submit_action(self, request):
        return self._request('action', request)

    def action_status(self, request_id):
        return self._request('action', query={'request_id': request_id})

    def control(self, **fields):
        return self._request('control', fields)

    def get_control(self):
        return self._request('control')

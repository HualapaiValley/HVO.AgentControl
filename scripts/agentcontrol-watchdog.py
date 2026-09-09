#!/usr/bin/env python3
"""Observe fleet incidents and optionally start repeatedly observed exited owned containers."""
import argparse
import fcntl
import hashlib
import html.parser
import http.cookiejar
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ACTIVE_RUNS = {'Ready', 'Waiting', 'Deciding', 'Recovering'}
OPEN_COMMANDS = {'Queued', 'Dispatching', 'AcceptedByRuntime', 'Running', 'DeliveryUnknown'}
CATEGORIES = {'AuthenticationRequired', 'Exhausted', 'Throttled', 'Unavailable',
              'InvalidRequest', 'Cancelled', 'ContextLimit', 'OutputLimit', 'NativeError'}
ERROR_NAMES = {'APIError', 'ProviderAuthError', 'FreeUsageLimitError', 'MessageAbortedError',
               'ContextOverflowError', 'MessageOutputLengthError', 'UnknownError'}


def identity(value):
    # Never copy arbitrary server text to the journal (even when labelled an ID).
    return hashlib.sha256(str(value).encode()).hexdigest()[:16]


def parsed(value, fallback):
    try:
        result = json.loads(value) if isinstance(value, str) else value
        return result if isinstance(result, type(fallback)) else fallback
    except (ValueError, TypeError):
        return fallback


def native_error(error):
    if not isinstance(error, dict):
        return {'name': 'UnknownError', 'category': 'NativeError', 'status': None}
    name = error.get('name')
    name = name if name in ERROR_NAMES else 'UnknownError'
    data = error.get('data') if isinstance(error.get('data'), dict) else {}
    status = data.get('statusCode')
    status = status if type(status) is int and 100 <= status <= 599 else None
    category = ('AuthenticationRequired' if status in (401, 403) or name == 'ProviderAuthError'
                else 'Throttled' if status == 429 or name == 'FreeUsageLimitError'
                else 'Unavailable' if status is not None and status >= 500
                else 'InvalidRequest' if status is not None and status >= 400
                else {'MessageAbortedError': 'Cancelled', 'ContextOverflowError': 'ContextLimit',
                      'MessageOutputLengthError': 'OutputLimit'}.get(name, 'NativeError'))
    if status == 429 and data.get('code') in ('insufficient_quota', 'quota_exceeded'):
        category = 'Exhausted'
    return {'name': name, 'category': category, 'status': status}


def analyze(snapshot, runs, now, stall_seconds):
    """Return only fixed classifications and hashed object locators; never transcript text."""
    incidents = []
    active = [run for run in runs if run.get('state') in ACTIVE_RUNS]
    workers = {worker['id']: worker for worker in snapshot.get('workers', [])}
    commands = snapshot.get('commands', [])
    by_id = {command['id']: command for command in commands}
    for runtime in snapshot.get('runtimes', []):
        if runtime.get('desiredConnected') and (runtime.get('transport') != 'Connected' or runtime.get('health') != 'Healthy'):
            incidents.append({'kind': 'runtime_unavailable', 'subject': identity(runtime['id'])})
    for request in snapshot.get('requests', []):
        if request.get('state') in ('Pending', 'ReplyUnknown'):
            incidents.append({'kind': 'pending_request', 'subject': identity(request.get('id'))})
    for access in snapshot.get('githubAccess', []):
        if access.get('state') != 'Disabled' and (access.get('state') != 'Ready' or
                not access.get('expiresAt') or access['expiresAt'] <= now * 1000):
            incidents.append({'kind': 'github_access_unavailable', 'subject': identity(access.get('id'))})
    for error in snapshot.get('nativeErrors', []):
        incidents.append({'kind': 'provider_failure', 'subject': identity(error.get('commandId')),
                          **native_error({'name': error.get('name'), 'data': {
                              'statusCode': error.get('status'), 'code': error.get('code')}})})
    # Persisted provider classification can include bounded structured response codes
    # omitted from the compact native error. Prefer it when both identify the same command.
    for failure in snapshot.get('providerFailures', []):
        category, status = failure.get('category'), failure.get('status')
        incidents.append({'kind': 'provider_failure', 'subject': identity(failure.get('commandId')),
                          'category': category if category in CATEGORIES else 'NativeError',
                          'status': status if type(status) is int and 100 <= status <= 599 else None})
    for run in active:
        run_key = identity(run['id'])
        context = parsed(run.get('inputJson', '{}'), {})
        failure = parsed(context.get('nativeFailure'), {})
        if failure.get('held'):
            category = failure.get('category')
            status = failure.get('status')
            incidents.append({'kind': 'provider_failure', 'subject': run_key,
                              'category': category if category in CATEGORIES else 'NativeError',
                              'status': status if type(status) is int and 100 <= status <= 599 else None})
        attempt = parsed(context.get('repair'), {}).get('attempt', 0)
        if type(attempt) is int and attempt >= 2:
            incidents.append({'kind': 'repeated_format_recovery', 'subject': run_key})
        recovery_attempt = parsed(context.get('recovery'), {}).get('attempt', 0)
        if type(recovery_attempt) is int and recovery_attempt >= 2:
            incidents.append({'kind': 'repeated_decision_recovery', 'subject': run_key})
        command = by_id.get(run.get('decisionCommandId'), {})
        result = parsed(command.get('resultJson', '{}'), {})
        for message in result.get('messages', []):
            error = message.get('info', {}).get('error')
            if error is not None:
                incidents.append({'kind': 'provider_failure', 'subject': run_key, **native_error(error)})
        ids = set(parsed(run.get('workerIdsJson', '[]'), []))
        active_commands = [command for command in commands if command.get('workerId') in ids
                           and command.get('state') in OPEN_COMMANDS]
        pending_requests = any(request.get('workerId') in ids and request.get('state') in ('Pending', 'ReplyUnknown')
                               for request in snapshot.get('requests', []))
        if ids and not active_commands and not pending_requests:
            idle = [workers.get(worker_id, {}) for worker_id in ids]
            if any(worker.get('activity') == 'Idle' and not worker.get('stale', True) for worker in idle):
                incidents.append({'kind': 'no_assignments', 'subject': run_key})
    # Independent worker commands are still monitored even when the owner pauses scheduling.
    for command in commands:
        if command.get('state') not in OPEN_COMMANDS:
            continue
        recent = max(command.get('createdAt') or 0, command.get('lastProgressAt') or 0)
        if command.get('state') == 'DeliveryUnknown' or now - recent / 1000 >= stall_seconds:
            incidents.append({'kind': 'unresolved_command', 'subject': identity(command['id']),
                              'state': command['state']})
    latest = {}
    for command in commands:
        worker_id = command.get('workerId')
        if command.get('kind') == 'Prompt' and worker_id and command.get('createdAt', 0) >= latest.get(worker_id, {}).get('createdAt', 0):
            latest[worker_id] = command
    for command in latest.values():
        if now - command.get('updatedAt', 0) / 1000 > 1800:
            continue
        for message in parsed(command.get('resultJson', '{}'), {}).get('messages', []):
            error = message.get('info', {}).get('error')
            if error is not None:
                incidents.append({'kind': 'provider_failure', 'subject': identity(command['id']), **native_error(error)})
    return incidents, bool(active), bool(runs) and not active


class TokenParser(html.parser.HTMLParser):
    token = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'input' and attrs.get('name') == '__RequestVerificationToken':
            self.token = attrs.get('value')


class SameOriginRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        old = urllib.parse.urlsplit(request.full_url)
        new = urllib.parse.urlsplit(newurl)
        if (old.scheme, old.netloc) != (new.scheme, new.netloc):
            raise ValueError('Cross-origin redirect refused')
        return super().redirect_request(request, fp, code, msg, headers, newurl)


class Controller:
    def __init__(self, base, password_file, timeout):
        self.base, self.password_file, self.timeout = base.rstrip('/'), password_file, timeout
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
                                                 SameOriginRedirect())
        self.authenticated = False

    def read(self, path, data=None):
        with self.opener.open(self.base + path, data=data, timeout=self.timeout) as response:
            body = response.read(8 * 1024 * 1024 + 1)
            if len(body) > 8 * 1024 * 1024:
                raise ValueError('Response limit exceeded')
            return body

    def login(self):
        parser = TokenParser()
        parser.feed(self.read('/login').decode())
        if not parser.token:
            raise ValueError('Missing login token')
        form = urllib.parse.urlencode({'__RequestVerificationToken': parser.token,
                                      'password': self.password_file.read_text().strip()}).encode()
        self.read('/auth/login', form)
        # An invalid password may redirect to a successful HTML login page.
        json.loads(self.read('/api/v1/csrf'))['token']
        self.authenticated = True

    def snapshot(self):
        try:
            if not self.authenticated:
                self.login()
            status = json.loads(self.read('/api/v1/watchdog'))
            if (not isinstance(status, dict) or status.get('version') != 1 or status.get('complete') is not True or
                    type(status.get('observedAt')) is not int or not -30 <= time.time() - status['observedAt'] / 1000 <= 120):
                raise ValueError('Incomplete or stale observation')
            snapshot, runs = status['snapshot'], status['coordinations']
            if (not isinstance(snapshot, dict) or not isinstance(runs, list) or
                    any(not isinstance(snapshot.get(key), list) for key in
                        ('workers', 'runtimes', 'commands', 'requests', 'providerFailures', 'githubAccess', 'nativeErrors'))):
                raise ValueError('Invalid observation shape')
            return snapshot, runs
        except Exception:
            self.authenticated = False
            raise


def atomic_write(path, state):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, name = tempfile.mkstemp(prefix=path.name + '.', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'w') as stream:
            json.dump(state, stream, separators=(',', ':'))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def update_incidents(state, observations, now, alert_after, emit):
    old = state.get('incidents', {})
    current = {}
    selected = {item['kind'] + ':' + item['subject']: item for item in observations}
    state['incidentOverflowCount'] = max(0, len(selected) - 256)
    if state['incidentOverflowCount']:
        if 'incidentOverflowReportedAt' not in state or now - state['incidentOverflowReportedAt'] >= 300:
            emit({'event': 'incident_tracking_overflow', 'severity': 'critical', 'observedAt': now,
                  'untrackedIncidents': state['incidentOverflowCount']})
            state['incidentOverflowReportedAt'] = now
    elif state.pop('incidentOverflowReportedAt', None) is not None:
        emit({'event': 'incident_tracking_restored', 'observedAt': now})
    # Preserve already tracked incidents first. Capacity eviction is never resolution.
    ordered = [key for key in old if key in selected] + [key for key in selected if key not in old]
    for key in ordered[:256]:
        observation = selected[key]
        record = dict(old.get(key, {'firstSeen': now, 'lastReported': 0}))
        record.update(observation, lastSeen=now)
        delayed = observation['kind'] in ('no_assignments', 'runtime_unavailable', 'pending_request') and now - record['firstSeen'] < alert_after
        if not delayed and (not record['lastReported'] or now - record['lastReported'] >= 300):
            emit({'event': 'incident', **observation, 'observedAt': now})
            record['lastReported'] = now
        current[key] = record
    for key, record in old.items():
        if key not in selected and record.get('lastReported'):
            emit({'event': 'resolved', 'kind': record['kind'], 'subject': record['subject'], 'observedAt': now})
    state['incidents'] = dict(list(current.items())[-256:])


def observe_controller(state, controller, now, stall_seconds, emit):
    """A running monitor is not healthy unless it can obtain a fresh complete observation."""
    try:
        snapshot, runs = controller.snapshot()
        observations, active, paused = analyze(snapshot, runs, now, stall_seconds)
        update_incidents(state, observations, now, stall_seconds, emit)
        if state.get('observationStatus') in ('Unavailable', 'Stale'):
            emit({'event': 'controller_observation_restored', 'observedAt': now})
        state.update(lastControllerSuccess=now, intentionalPause=paused, activeCoordination=active,
                     observationStatus='Limited' if state['incidentOverflowCount'] else 'Healthy', consecutiveObservationFailures=0)
        for key in ('observationFailureSince', 'controllerFailureAt', 'observationStaleReportedAt'):
            state.pop(key, None)
        return not state['incidentOverflowCount']
    except Exception as error:
        # Keep previously observed incidents; failure to read is not evidence of their resolution.
        state.setdefault('observationFailureSince', now)
        state['consecutiveObservationFailures'] = state.get('consecutiveObservationFailures', 0) + 1
        since = state.get('lastControllerSuccess', state['observationFailureSince'])
        state['observationStatus'] = 'Stale' if now - since >= stall_seconds else 'Unavailable'
        if 'controllerFailureAt' not in state or now - state['controllerFailureAt'] >= 300:
            kind = type(error).__name__
            emit({'event': 'controller_observation_failed', 'observedAt': now,
                  'category': kind if kind in ('HTTPError', 'URLError', 'TimeoutError', 'JSONDecodeError',
                                              'ValueError', 'KeyError', 'TypeError') else 'ObservationError'})
            state['controllerFailureAt'] = now
        if state['observationStatus'] == 'Stale' and (
                'observationStaleReportedAt' not in state or now - state['observationStaleReportedAt'] >= 300):
            emit({'event': 'controller_observation_stale', 'severity': 'critical', 'observedAt': now,
                  'unobservedSeconds': max(0, int(now - since))})
            state['observationStaleReportedAt'] = now
        return False


def recovery_is_paused(state, now, stall_seconds):
    # A recent explicit running observation allows recovery of a controller that just exited.
    # Startup, an owner pause, or extended blindness cannot authorize container starts.
    return (state.get('intentionalPause', True) or state.get('activeCoordination') is not True or
            state.get('incidentOverflowCount', 0) > 0 or
            state.get('observationStatus') not in ('Healthy', 'Unavailable') or 'lastControllerSuccess' not in state or
            now - state['lastControllerSuccess'] >= stall_seconds)


class Docker:
    def __init__(self, context, timeout):
        self.prefix = ['docker'] + (['--context', context] if context else [])
        self.timeout = timeout

    def call(self, arguments):
        return subprocess.run(self.prefix + arguments, check=True, capture_output=True, text=True,
                              timeout=self.timeout).stdout

    def inspect(self, name):
        return json.loads(self.call(['container', 'inspect', name]))[0]

    def start(self, container_id):
        self.call(['container', 'start', container_id])


def recover_containers(state, docker, names, labels, now, checks, cooldown, enabled, paused, emit):
    records = state.setdefault('containers', {})
    records = state['containers'] = {name: records.get(name, {}) for name in names}
    ownership = [label.split('=', 1) for label in labels]
    for name in names:
        record = records[name]
        try:
            container = docker.inspect(name)
            owned = (container.get('Name') == '/' + name and
                     any((container.get('Config', {}).get('Labels') or {}).get(key) == value
                         for key, value in ownership))
            exited = container.get('State', {}).get('Status') == 'exited'
            container_id = container.get('Id')
            if not owned or not exited or not container_id or paused:
                record['checks'] = 0
                continue
            if record.get('id') != container_id:
                record['checks'] = 0
            record['id'] = container_id
            record['checks'] = record.get('checks', 0) + 1
            record['attempts'] = [stamp for stamp in record.get('attempts', []) if now - stamp < 3600]
            if (not enabled or record['checks'] < checks or len(record['attempts']) >= 3 or
                    record['attempts'] and now - record['attempts'][-1] < cooldown):
                continue
            # Persist intent before the process invocation so a watchdog crash cannot bypass its cooldown.
            record['attempts'].append(now)
            emit({'event': 'container_start_requested', 'subject': identity(name), 'observedAt': now}, persist=True)
            docker.start(container_id)  # Exact observed container identity; never recreate or restart a running one.
            record['checks'] = 0
            emit({'event': 'container_start_accepted', 'subject': identity(name), 'observedAt': now})
        except Exception:
            record['checks'] = 0
            # Docker stderr/exception text may contain environment, endpoint or credential data.
            emit({'event': 'container_observation_or_start_failed', 'subject': identity(name), 'observedAt': now})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', required=True)
    parser.add_argument('--password-file', required=True, type=Path)
    parser.add_argument('--state-file', required=True, type=Path)
    parser.add_argument('--container', action='append', default=[])
    parser.add_argument('--owner-label', action='append', default=[], help='Accepted ownership label key=value; repeat for multiple owned projects')
    parser.add_argument('--docker-context', default='')
    parser.add_argument('--restart-exited', action='store_true')
    parser.add_argument('--interval', type=int, default=30)
    parser.add_argument('--timeout', type=int, default=10)
    parser.add_argument('--confirm-checks', type=int, default=3)
    parser.add_argument('--cooldown', type=int, default=600)
    parser.add_argument('--stall-seconds', type=int, default=300)
    parser.add_argument('--once', action='store_true')
    args = parser.parse_args()
    url = urllib.parse.urlsplit(args.url)
    if url.scheme not in ('http', 'https') or not url.netloc or url.username or url.password or url.query or url.fragment:
        parser.error('Use an HTTP(S) controller origin without embedded credentials or query parameters')
    if min(args.interval, args.timeout, args.cooldown, args.stall_seconds) < 1 or args.confirm_checks < 2:
        parser.error('Positive intervals and at least two confirming observations are required')
    if len(args.container) > 16 or any(not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}', name) for name in args.container):
        parser.error('Choose at most 16 exact Docker container names')
    if args.container and (not args.owner_label or any('=' not in label or not all(label.split('=', 1)) for label in args.owner_label)):
        parser.error('Container observation requires an explicit ownership label key=value')
    args.state_file.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with open(str(args.state_file) + '.lock', 'a') as lock:
        os.chmod(lock.name, 0o600)
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        # Corrupt state fails closed rather than forgetting persisted restart attempts.
        state = json.loads(args.state_file.read_text()) if args.state_file.exists() else {'version': 1}
        controller = Controller(args.url, args.password_file, args.timeout)
        docker = Docker(args.docker_context, args.timeout)

        def emit(event, persist=False):
            state['journal'] = (state.get('journal', []) + [event])[-256:]
            if persist:
                atomic_write(args.state_file, state)
            print(json.dumps(event, separators=(',', ':')), flush=True)

        while True:
            now = time.time()
            fresh = observe_controller(state, controller, now, args.stall_seconds, emit)
            recover_containers(state, docker, args.container, args.owner_label, now, args.confirm_checks,
                               args.cooldown, args.restart_exited, recovery_is_paused(state, now, args.stall_seconds), emit)
            atomic_write(args.state_file, state)
            if args.once:
                return 0 if fresh else 1
            time.sleep(args.interval)


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError):
        # Startup diagnostics intentionally omit filesystem paths and exception bodies.
        print('{"event":"watchdog_startup_failed"}', flush=True)
        raise SystemExit(1)

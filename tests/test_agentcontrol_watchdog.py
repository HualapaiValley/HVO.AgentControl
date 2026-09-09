import importlib.util
import json
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import Mock, patch
import urllib.request

SPEC = importlib.util.spec_from_file_location('watchdog', Path(__file__).parents[1] / 'scripts/agentcontrol-watchdog.py')
watchdog = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(watchdog)


class WatchdogTests(unittest.TestCase):
    def fleet(self, state='Deciding'):
        return ({'workers': [{'id': 'worker', 'activity': 'Idle', 'stale': False}], 'commands': []},
                [{'id': 'run', 'state': state, 'workerIdsJson': '["worker"]', 'inputJson': '{}'}])

    def test_provider_error_is_classified_without_copying_secrets(self):
        snapshot, runs = self.fleet()
        command = {'id': 'decision', 'workerId': 'worker', 'kind': 'Prompt', 'state': 'Finished',
                   'createdAt': 990000, 'updatedAt': 999000, 'resultJson': json.dumps({'messages': [{
                       'info': {'error': {'name': 'APIError', 'data': {'statusCode': 429, 'code': 'insufficient_quota',
                                                                    'headers': {'Authorization': 'SECRET'},
                                                                    'message': 'SECRET', 'responseBody': 'SECRET'}}},
                       'parts': [{'type': 'text', 'text': 'SECRET'}]}]})}
        runs[0]['decisionCommandId'] = command['id']
        snapshot['commands'].append(command)
        incidents, _, _ = watchdog.analyze(snapshot, runs, 1000, 300)
        failures = [item for item in incidents if item['kind'] == 'provider_failure']
        self.assertTrue(failures)
        self.assertEqual('Exhausted', failures[0]['category'])
        self.assertNotIn('SECRET', json.dumps(incidents))
        self.assertEqual('UnknownError', watchdog.native_error({'name': 'SECRET'})['name'])

    def test_paused_run_does_not_report_missing_assignments_but_tracks_unknown_delivery(self):
        snapshot, runs = self.fleet('Paused')
        snapshot['commands'] = [{'id': 'uncertain', 'state': 'DeliveryUnknown', 'createdAt': 999999}]
        incidents, active, paused = watchdog.analyze(snapshot, runs, 1000, 300)
        self.assertFalse(active)
        self.assertTrue(paused)
        self.assertEqual(['unresolved_command'], [item['kind'] for item in incidents])

    def test_repeated_format_repair_is_distinct_from_provider_failure(self):
        snapshot, runs = self.fleet()
        runs[0]['inputJson'] = '{"repair":{"attempt":3}}'
        incidents, _, _ = watchdog.analyze(snapshot, runs, 1000, 300)
        self.assertIn('repeated_format_recovery', [item['kind'] for item in incidents])
        self.assertNotIn('provider_failure', [item['kind'] for item in incidents])

    def test_missing_or_wrong_shaped_optional_native_data_does_not_hide_other_incidents(self):
        for context in ('null', '[]', '{"nativeFailure":[],"repair":{"attempt":"SECRET"}}'):
            snapshot, runs = self.fleet()
            runs[0]['inputJson'] = context
            snapshot['commands'] = [{'id': 'uncertain', 'state': 'DeliveryUnknown', 'createdAt': 999999,
                                     'kind': 'Prompt', 'workerId': 'worker', 'resultJson': 'null'}]
            incidents, _, _ = watchdog.analyze(snapshot, runs, 1000, 300)
            self.assertIn('unresolved_command', [item['kind'] for item in incidents])
            self.assertNotIn('SECRET', json.dumps(incidents))

    def test_repeated_action_rejection_or_scheduler_recovery_is_observable_without_reason_text(self):
        snapshot, runs = self.fleet('Recovering')
        runs[0]['inputJson'] = '{"recovery":{"attempt":2,"reason":"SECRET"}}'
        incidents, _, _ = watchdog.analyze(snapshot, runs, 1000, 300)
        self.assertIn('repeated_decision_recovery', [item['kind'] for item in incidents])
        self.assertNotIn('SECRET', json.dumps(incidents))

    def test_live_work_and_pending_permission_are_not_reported_as_missing_assignment(self):
        snapshot, runs = self.fleet()
        snapshot['commands'] = [{'id': 'work', 'workerId': 'worker', 'state': 'Running',
                                 'createdAt': 1000, 'lastProgressAt': 999000}]
        self.assertEqual([], watchdog.analyze(snapshot, runs, 1000, 300)[0])
        snapshot['commands'] = []
        snapshot['requests'] = [{'workerId': 'worker', 'state': 'Pending'}]
        self.assertEqual(['pending_request'], [item['kind'] for item in watchdog.analyze(snapshot, runs, 1000, 300)[0]])

    def test_unavailable_runtime_and_pending_requests_surface_after_grace_period(self):
        snapshot, runs = self.fleet('Paused')
        snapshot['runtimes'] = [{'id': 'remote', 'desiredConnected': True, 'transport': 'Reconnecting', 'health': 'Degraded'},
                                {'id': 'offline', 'desiredConnected': False}]
        snapshot['requests'] = [{'id': 'permission', 'state': 'ReplyUnknown'}]
        observations, _, _ = watchdog.analyze(snapshot, runs, 1000, 300)
        self.assertEqual({'runtime_unavailable', 'pending_request'}, {item['kind'] for item in observations})
        state, events = {}, []
        watchdog.update_incidents(state, observations, 1000, 300, events.append)
        self.assertEqual([], events)
        watchdog.update_incidents(state, observations, 1300, 300, events.append)
        self.assertEqual(2, len(events))

    def test_incidents_wait_before_alert_repeat_boundedly_and_resolve(self):
        state, events = {}, []
        observation = [{'kind': 'no_assignments', 'subject': 'run'}]
        watchdog.update_incidents(state, observation, 1000, 300, events.append)
        self.assertEqual([], events)
        watchdog.update_incidents(state, observation, 1300, 300, events.append)
        watchdog.update_incidents(state, observation, 1301, 300, events.append)
        self.assertEqual(1, len(events))
        watchdog.update_incidents(state, [], 1302, 300, events.append)
        self.assertEqual('resolved', events[-1]['event'])
        self.assertEqual({}, state['incidents'])

    def docker(self, status='exited', label='fleet'):
        docker = Mock()
        docker.inspect.return_value = {'Name': '/owned', 'Id': 'exact-container-id',
                                       'State': {'Status': status}, 'Config': {'Labels': {'owner': label}}}
        return docker

    def recover(self, state, docker, now, enabled=True, paused=False):
        events = []
        watchdog.recover_containers(state, docker, ['owned'], ['owner=fleet'], now, 3, 600,
                                    enabled, paused, lambda event, **kwargs: events.append((event, kwargs)))
        return events

    def test_only_confirmed_owned_exit_starts_exact_identity_and_obeys_cooldown(self):
        state, docker = {}, self.docker()
        self.recover(state, docker, 1000)
        self.recover(state, docker, 1030)
        docker.start.assert_not_called()
        events = self.recover(state, docker, 1060)
        docker.start.assert_called_once_with('exact-container-id')
        self.assertTrue(events[0][1]['persist'])
        for stamp in (1090, 1120, 1150):
            self.recover(state, docker, stamp)
        docker.start.assert_called_once()

    def test_running_unowned_paused_and_observe_only_never_start(self):
        for docker, enabled, paused in ((self.docker('running'), True, False),
                                       (self.docker(label='other'), True, False),
                                       (self.docker(), True, True), (self.docker(), False, False)):
            with self.subTest(enabled=enabled, paused=paused):
                state = {}
                for stamp in range(1000, 1300, 30):
                    self.recover(state, docker, stamp, enabled, paused)
                docker.start.assert_not_called()

    def test_multiple_owned_compose_projects_still_require_exact_name(self):
        state, docker = {}, self.docker(label='workers')
        for stamp in (1000, 1030, 1060):
            watchdog.recover_containers(state, docker, ['owned'], ['owner=controller', 'owner=workers'],
                                        stamp, 3, 600, True, False, lambda event, **kwargs: None)
        docker.start.assert_called_once_with('exact-container-id')
        docker.start.reset_mock()
        docker.inspect.return_value['Name'] = '/not-allowlisted'
        for stamp in (2000, 2030, 2060):
            watchdog.recover_containers(state, docker, ['owned'], ['owner=workers'],
                                        stamp, 3, 600, True, False, lambda event, **kwargs: None)
        docker.start.assert_not_called()

    def test_observation_failure_breaks_consecutive_exit_evidence(self):
        state, docker = {}, self.docker()
        self.recover(state, docker, 1000)
        self.recover(state, docker, 1030)
        docker.inspect.side_effect = RuntimeError('SECRET')
        events = self.recover(state, docker, 1060)
        docker.inspect.side_effect = None
        self.recover(state, docker, 1090)
        docker.start.assert_not_called()
        self.assertNotIn('SECRET', json.dumps(events))

    def test_failed_start_is_budgeted_across_persisted_state(self):
        state, docker = {}, self.docker()
        docker.start.side_effect = RuntimeError('SECRET')
        for stamp in (1000, 1030, 1060):
            self.recover(state, docker, stamp)
        state = json.loads(json.dumps(state))
        for stamp in (1090, 1120, 1150):
            self.recover(state, docker, stamp)
        self.assertEqual(1, docker.start.call_count)
        state['containers']['owned']['attempts'] = [1000, 1600, 2200]
        for stamp in (2800, 2830, 2860):
            self.recover(state, docker, stamp)
        self.assertEqual(1, docker.start.call_count)

    def test_atomic_ledger_is_private_and_observation_count_is_bounded(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'state.json'
            watchdog.atomic_write(path, {'version': 1})
            watchdog.atomic_write(path, {'version': 2})
            self.assertEqual({'version': 2}, json.loads(path.read_text()))
            self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            self.assertEqual(['state.json'], [item.name for item in path.parent.iterdir()])
        state = {}
        watchdog.update_incidents(state, [{'kind': 'unresolved_command', 'subject': str(n)} for n in range(500)],
                                  1000, 300, lambda event: None)
        self.assertEqual(256, len(state['incidents']))

    def test_login_rejects_html_success_and_next_poll_reauthenticates(self):
        with tempfile.TemporaryDirectory() as directory:
            password = Path(directory) / 'password'
            password.write_text('SECRET')
            controller = watchdog.Controller('http://127.0.0.1:5054', password, 2)
            controller.read = Mock(side_effect=[b'<input name="__RequestVerificationToken" value="token">',
                                               b'login page', b'<html>not authenticated</html>'])
            with self.assertRaises(ValueError):
                controller.snapshot()
            self.assertFalse(controller.authenticated)
            self.assertIn(b'password=SECRET', controller.read.call_args_list[1].args[1])

    def test_compact_status_is_used_and_stale_or_partial_observations_are_rejected(self):
        controller = watchdog.Controller('http://localhost', Path('/unused'), 7)
        payload = {'version': 1, 'observedAt': 1000000, 'complete': True, 'coordinations': [],
                   'snapshot': {key: [] for key in ('runtimes', 'workers', 'commands', 'requests', 'providerFailures', 'githubAccess', 'nativeErrors')}}
        with patch.object(watchdog.time, 'time', return_value=1000):
            controller.authenticated = True
            controller.read = Mock(return_value=json.dumps(payload).encode())
            self.assertEqual((payload['snapshot'], []), controller.snapshot())
            controller.read.assert_called_once_with('/api/v1/watchdog')
            for change in ({'complete': False}, {'version': 2}, {'observedAt': 1000}, {'observedAt': 2000000}, {'snapshot': {}}):
                with self.subTest(change=change):
                    controller.authenticated = True
                    controller.read = Mock(return_value=json.dumps({**payload, **change}).encode())
                    with self.assertRaises(ValueError):
                        controller.snapshot()
                    self.assertFalse(controller.authenticated)

    def test_blind_monitor_escalates_persists_and_recovers_without_false_resolution(self):
        controller, events = Mock(), []
        state = {'lastControllerSuccess': 1000, 'intentionalPause': False, 'activeCoordination': True,
                 'observationStatus': 'Healthy',
                 'incidents': {'pending_request:worker': {'kind': 'pending_request', 'subject': 'worker', 'lastReported': 1000}}}
        controller.snapshot.side_effect = ValueError('SECRET oversized response')
        self.assertFalse(watchdog.observe_controller(state, controller, 1030, 300, events.append))
        self.assertEqual('Unavailable', state['observationStatus'])
        self.assertFalse(watchdog.recovery_is_paused(state, 1030, 300))
        state = json.loads(json.dumps(state))  # watchdog process restart retains the outage age
        watchdog.observe_controller(state, controller, 1301, 300, events.append)
        self.assertEqual('Stale', state['observationStatus'])
        self.assertTrue(watchdog.recovery_is_paused(state, 1301, 300))
        self.assertIn('pending_request:worker', state['incidents'])
        self.assertEqual('critical', events[-1]['severity'])
        self.assertEqual(301, events[-1]['unobservedSeconds'])
        self.assertNotIn('SECRET', json.dumps(state) + json.dumps(events))
        controller.snapshot.side_effect = None
        controller.snapshot.return_value = self.fleet('Paused')
        self.assertTrue(watchdog.observe_controller(state, controller, 1302, 300, events.append))
        self.assertEqual('Healthy', state['observationStatus'])
        self.assertEqual('controller_observation_restored', events[-1]['event'])
        self.assertEqual(0, state['consecutiveObservationFailures'])
        self.assertTrue(watchdog.recovery_is_paused(state, 1302, 300))
        self.assertTrue(watchdog.recovery_is_paused({}, 1000, 300))

    def test_stopping_final_run_cannot_authorize_restarting_exited_containers(self):
        controller, state, docker = Mock(), {}, self.docker()
        snapshot, _ = self.fleet()
        controller.snapshot.return_value = (snapshot, [])  # compact API omits terminal runs
        self.assertTrue(watchdog.observe_controller(state, controller, 1000, 300, lambda event: None))
        for stamp in (1000, 1030, 1060):
            self.recover(state, docker, stamp, paused=watchdog.recovery_is_paused(state, stamp, 300))
        docker.start.assert_not_called()

    def test_incident_overflow_never_resolves_still_observed_work_and_is_not_healthy(self):
        state, events, controller = {}, [], Mock()
        snapshot, runs = self.fleet()
        snapshot['commands'] = [{'id': 'old', 'state': 'DeliveryUnknown'}]
        controller.snapshot.return_value = (snapshot, runs)
        self.assertTrue(watchdog.observe_controller(state, controller, 1000, 300, events.append))
        snapshot['commands'] += [{'id': str(i), 'state': 'DeliveryUnknown'} for i in range(256)]
        self.assertFalse(watchdog.observe_controller(state, controller, 1030, 300, events.append))
        self.assertEqual('Limited', state['observationStatus'])
        self.assertGreater(state['incidentOverflowCount'], 0)
        self.assertTrue(any(x['event'] == 'incident_tracking_overflow' for x in events))
        self.assertFalse(any(x['event'] == 'resolved' for x in events))
        self.assertIn('unresolved_command:' + watchdog.identity('old'), state['incidents'])
        self.assertTrue(watchdog.recovery_is_paused(state, 1030, 300))
        controller.snapshot.side_effect = TimeoutError()
        self.assertFalse(watchdog.observe_controller(state, controller, 1040, 300, events.append))
        self.assertEqual('Unavailable', state['observationStatus'])
        self.assertTrue(watchdog.recovery_is_paused(state, 1040, 300))
        controller.snapshot.side_effect = None
        snapshot['commands'] = []
        self.assertTrue(watchdog.observe_controller(state, controller, 1060, 300, events.append))
        self.assertEqual('Healthy', state['observationStatus'])
        self.assertTrue(any(x['event'] == 'incident_tracking_restored' for x in events))

    def test_github_expiry_and_structured_provider_failures_are_observed_during_pause(self):
        snapshot, runs = self.fleet('Paused')
        snapshot['githubAccess'] = [{'id': 'expired', 'state': 'Ready', 'expiresAt': 999000},
                                    {'id': 'blocked', 'state': 'Blocked'},
                                    {'id': 'disabled', 'state': 'Disabled'},
                                    {'id': 'good', 'state': 'Ready', 'expiresAt': 1001000}]
        snapshot['providerFailures'] = [{'commandId': 'prompt', 'category': 'AuthenticationRequired', 'status': 401}]
        observations, active, paused = watchdog.analyze(snapshot, runs, 1000, 300)
        self.assertEqual(2, sum(x['kind'] == 'github_access_unavailable' for x in observations))
        self.assertEqual(401, next(x for x in observations if x['kind'] == 'provider_failure')['status'])
        self.assertFalse(active)
        self.assertTrue(paused)

    def test_cross_origin_login_redirect_is_rejected(self):
        request = urllib.request.Request('http://127.0.0.1/auth/login', data=b'password=SECRET')
        with self.assertRaises(ValueError):
            watchdog.SameOriginRedirect().redirect_request(request, None, 302, '', {}, 'http://other.example/login')

    def test_http_reads_are_bounded_and_docker_commands_have_timeouts(self):
        controller = watchdog.Controller('http://127.0.0.1:5054', Path('/unused'), 7)
        response = Mock()
        response.read.return_value = b'x' * (8 * 1024 * 1024 + 1)
        controller.opener.open = Mock()
        controller.opener.open.return_value.__enter__ = Mock(return_value=response)
        controller.opener.open.return_value.__exit__ = Mock(return_value=False)
        with self.assertRaises(ValueError):
            controller.read('/api/v1/snapshot')
        self.assertEqual(7, controller.opener.open.call_args.kwargs['timeout'])
        response.read.assert_called_once_with(8 * 1024 * 1024 + 1)
        with patch.object(watchdog.subprocess, 'run', return_value=Mock(stdout='')) as command:
            watchdog.Docker('owned-context', 9).start('exact-id')
        self.assertEqual(['docker', '--context', 'owned-context', 'container', 'start', 'exact-id'], command.call_args.args[0])
        self.assertEqual(9, command.call_args.kwargs['timeout'])
        self.assertNotIn('shell', command.call_args.kwargs)


if __name__ == '__main__':
    unittest.main()

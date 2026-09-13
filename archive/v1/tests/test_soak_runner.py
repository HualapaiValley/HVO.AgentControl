"""Independent reviewer acceptance probes. Stub dotnet tests runner semantics, not .NET correctness."""
import json, os, pathlib, shutil, shlex, subprocess, sys, tempfile, time, unittest
SOURCE = pathlib.Path(sys.argv[1]).resolve()
sys.argv = sys.argv[:1]

class Contract(unittest.TestCase):

    def run_case(self, mode, batch=2, maximum=8, build=3):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = pathlib.Path(temp.name)
        (root / 'scripts').mkdir()
        (root / 'bin').mkdir()
        shutil.copyfile(SOURCE, root / 'scripts/run-coordination-soak.sh')
        fake = root / 'bin/dotnet'
        fake.write_text('#!/bin/sh\nif [ "$1" = build ]; then\n if [ "$FAKE_MODE" = build_timeout ]; then sleep 6; fi\n if [ "$FAKE_MODE" = slow_build ]; then sleep 2; fi\n exit 0\nfi\ncase "$FAKE_MODE" in\n timeout) sleep 6;;\n failure_exit) exit 9;;\n zero) echo \'Passed! - Failed: 0, Passed: 0, Skipped: 0, Total: 0\'; exit 0;;\n skipped) echo \'Passed! - Failed: 0, Passed: 0, Skipped: 7, Total: 7\'; exit 0;;\n partial_skipped) echo \'Passed! - Failed: 0, Passed: 1, Skipped: 6, Total: 7\'; exit 0;;\nesac\nsleep 0.2\necho \'Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7\'\n')
        fake.chmod(493)
        validator = root / 'bin/python3'
        validator.write_text('#!/bin/sh\nif [ "$FAKE_MODE" = validation_fail ]; then exit 7; fi\nexec ' + shlex.quote(sys.executable) + ' "$@"\n')
        validator.chmod(493)
        env = dict(os.environ, PATH=str(root / 'bin') + ':' + os.environ['PATH'], FAKE_MODE=mode, SOAK_BATCHES='1', SOAK_BATCH_SECONDS=str(batch), SOAK_MAX_TOTAL_SECONDS=str(maximum), SOAK_BUILD_SECONDS=str(build), SOAK_ITERATION_MIN_SECONDS='1', SOAK_ARTIFACT_ROOT=str(root / 'artifacts'))
        start = time.monotonic()
        p = subprocess.run(['bash', str(root / 'scripts/run-coordination-soak.sh')], env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=15)
        elapsed = time.monotonic() - start
        files = list((root / 'artifacts').rglob('evidence.jsonl'))
        self.assertEqual(1, len(files), p.stdout)
        events = [json.loads(line) for line in files[0].read_text().splitlines()]
        self.assertTrue(all((isinstance(x, dict) for x in events)))
        self.assertEqual('run_end', events[-1].get('event'))
        self.assertEqual(p.returncode, events[-1].get('exit'))
        return (p, events, elapsed)

    def test_success_runs_for_full_batch_and_records_real_positive_iterations(self):
        p, e, _ = self.run_case('success')
        self.assertEqual(0, p.returncode, p.stdout)
        ends = [x for x in e if x.get('event') == 'batch_end']
        self.assertEqual(1, len(ends))
        self.assertGreaterEqual(ends[0]['elapsedMs'], 2000)
        its = [x for x in e if x.get('event') == 'iteration']
        self.assertTrue(its)
        self.assertTrue(all((x['passed'] == 7 and x['failed'] == 0 and (x['total'] == 7) for x in its)))
        self.assertEqual(0, e[-1]['exit'])

    def test_zero_tests_fails(self):
        p, _, _ = self.run_case('zero')
        self.assertNotEqual(0, p.returncode, p.stdout)

    def test_only_skipped_tests_fails(self):
        for mode in ['skipped', 'partial_skipped']:
            with self.subTest(mode=mode):
                p, _, _ = self.run_case(mode)
                self.assertNotEqual(0, p.returncode, p.stdout)

    def test_nonzero_dotnet_exit_without_summary_fails(self):
        p, _, _ = self.run_case('failure_exit')
        self.assertNotEqual(0, p.returncode, p.stdout)

    def test_timeout_fails(self):
        p, _, _ = self.run_case('timeout', maximum=4)
        self.assertNotEqual(0, p.returncode, p.stdout)

    def test_build_respects_smaller_overall_deadline(self):
        p, _, elapsed = self.run_case('build_timeout', batch=1, maximum=2, build=5)
        self.assertNotEqual(0, p.returncode, p.stdout)
        self.assertLess(elapsed, 4, 'Build exceeded the configured overall deadline')

    def test_successful_build_leaving_insufficient_time_cannot_pass(self):
        p, _, _ = self.run_case('slow_build', batch=2, maximum=3, build=3)
        self.assertNotEqual(0, p.returncode, p.stdout)

    def test_validator_failure_cannot_pass(self):
        p, _, _ = self.run_case('validation_fail')
        self.assertNotEqual(0, p.returncode, p.stdout)
unittest.main(verbosity=2)

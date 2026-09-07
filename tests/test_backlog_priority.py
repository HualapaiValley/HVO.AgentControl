import datetime as dt
import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('backlog', Path(__file__).parents[1] / 'scripts/prioritize-backlog.py')
backlog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backlog)
NOW = dt.datetime(2026, 9, 7, tzinfo=dt.timezone.utc)


def issue(number, state='OPEN', created='2026-09-01T00:00:00Z'):
    return dict(number=number, title=str(number), state=state, createdAt=created, updatedAt="2026-09-01T00:00:00Z")


def item(number, dependencies=(), priority=1, disposition='candidate'):
    return dict(issue=number, dependsOn=list(dependencies), priority=priority, disposition=disposition, auditedUpdatedAt="2026-09-01T00:00:00Z")


def plan(*items):
    return dict(version='test', repository='Owner/Repo', items=list(items))


class PriorityTests(unittest.TestCase):
    def test_blocked_owner_priority_cannot_jump_prerequisite(self):
        result = backlog.prioritize([issue(1), issue(2)], plan(item(1), item(2, [1], 0)), now=NOW)
        self.assertEqual([1], [x['issue'] for x in result['ready']])
        self.assertEqual('blocked', result['issues'][1]['state'])

    def test_reopening_prerequisite_removes_candidate(self):
        p = plan(item(1), item(2, [1]))
        ready = backlog.prioritize([issue(1, 'CLOSED'), issue(2)], p, now=NOW)
        self.assertEqual([2], [x['issue'] for x in ready['ready']])
        reopened = backlog.prioritize([issue(1), issue(2)], p, now=NOW)
        self.assertEqual([1], [x['issue'] for x in reopened['ready']])

    def test_active_or_uncertain_claim_never_becomes_ready(self):
        result = backlog.prioritize([issue(1)], plan(item(1, [2], 0)), active=[1], now=NOW)
        self.assertEqual([], result['ready'])
        self.assertEqual('active', result['issues'][0]['state'])
        self.assertTrue(result['issues'][0]['blockers'])

    def test_cycles_missing_evidence_and_unplanned_issues_are_not_ready(self):
        result = backlog.prioritize([issue(n) for n in range(1, 5)], plan(item(1, [2]), item(2, [1]), item(3, [99])), now=NOW)
        self.assertEqual([], result['ready'])
        self.assertEqual('needs-triage', result['issues'][3]['state'])
        self.assertIn('cycle', result['issues'][0]['blockers'][-1]['reason'])

    def test_unlocks_then_age_break_equal_priority_ties(self):
        result = backlog.prioritize([issue(1), issue(2, created='2026-01-01T00:00:00Z'), issue(3), issue(4)],
                                    plan(item(1), item(2), item(3), item(4, [3])), now=NOW)
        self.assertEqual([3, 2, 1], [x['issue'] for x in result['ready']])

    def test_epic_and_review_are_not_duplicate_implementation_assignments(self):
        result = backlog.prioritize([issue(1), issue(2)], plan(item(1, disposition='epic'), item(2, [1], disposition='review')), now=NOW)
        self.assertEqual([], result['ready'])
        self.assertEqual(['epic', 'review'], [x['state'] for x in result['issues']])

    def test_changed_issue_requires_new_triage_without_interrupting_active_work(self):
        changed = issue(1)
        changed['updatedAt'] = '2026-09-07T00:00:00Z'
        result = backlog.prioritize([changed], plan(item(1)), now=NOW)
        self.assertEqual([], result['ready'])
        self.assertEqual('needs-triage', result['issues'][0]['state'])
        active = backlog.prioritize([changed], plan(item(1)), active=[1], now=NOW)
        self.assertEqual('active', active['issues'][0]['state'])
        self.assertTrue(active['issues'][0]['planStale'])

    def test_changed_or_missing_pr_evidence_invalidates_unchanged_issue(self):
        entry = item(1, disposition='review')
        entry['reviewedPulls'] = [dict(number=10, state='OPEN', headRefOid='old')]
        for actual in ([], [dict(number=10, state='MERGED', headRefOid='old')],
                       [dict(number=10, state='OPEN', headRefOid='new')]):
            with self.subTest(actual=actual):
                result = backlog.prioritize([issue(1)], plan(entry), now=NOW, pulls=actual)
                self.assertEqual('needs-triage', result['issues'][0]['state'])
                self.assertTrue(result['issues'][0]['planStale'])
        fresh = backlog.prioritize([issue(1)], plan(entry), now=NOW, pulls=entry['reviewedPulls'])
        self.assertEqual('review', fresh['issues'][0]['state'])

    def test_duplicate_identity_rejected(self):
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            backlog.prioritize([issue(1)], plan(item(1), item(1)), now=NOW)


if __name__ == '__main__':
    unittest.main()

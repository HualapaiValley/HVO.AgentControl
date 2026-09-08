#!/usr/bin/env python3
"""Read-only, dependency-aware backlog report. Never assigns work or closes issues."""
import argparse
import datetime as dt
import json
import subprocess
from pathlib import Path


def prioritize(issues, plan, active=(), now=None, pulls=()):
    now = now or dt.datetime.now(dt.timezone.utc)
    catalog = {x['number']: x for x in issues}
    entries = {x['issue']: x for x in plan['items']}
    if len(entries) != len(plan['items']) or len(catalog) != len(issues):
        raise ValueError('Duplicate issue identity in inputs')
    active = set(active)
    pull_catalog = {x['number']: x for x in pulls}
    visiting, visited, cyclic = [], set(), set()

    def visit(number):
        if number in visiting:
            cyclic.update(visiting[visiting.index(number):])
            return
        if number in visited:
            return
        visiting.append(number)
        for dependency in entries.get(number, {}).get('dependsOn', []):
            if dependency in entries:
                visit(dependency)
        visiting.pop()
        visited.add(number)

    for number in entries:
        visit(number)

    rows = []
    for number, issue in catalog.items():
        if issue['state'].upper() == 'CLOSED':
            continue
        entry = entries.get(number)
        blockers = []
        stale = bool(entry) and entry.get('auditedUpdatedAt') != issue.get('updatedAt')
        evidence_stale = bool(entry) and any(
            any(pull_catalog.get(reference['number'], {}).get(key) != reference.get(key)
                for key in ('state', 'headRefOid'))
            for reference in entry.get('reviewedPulls', []))
        stale = stale or evidence_stale
        disposition = 'needs-triage'
        if entry:
            if entry.get('priority') not in range(4):
                raise ValueError('Priority must be 0 through 3')
            disposition = entry['disposition']
            if disposition not in {'candidate', 'epic', 'needs-triage', 'review'}:
                raise ValueError('Invalid disposition for issue ' + str(number))
            for dependency in entry.get('dependsOn', []):
                if dependency not in catalog:
                    blockers.append({'issue': dependency, 'reason': 'missing prerequisite evidence'})
                elif catalog[dependency]['state'].upper() != 'CLOSED':
                    blockers.append({'issue': dependency, 'reason': 'prerequisite remains open'})
            if number in cyclic:
                blockers.append({'issue': number, 'reason': 'dependency cycle requires triage'})
        if stale:
            disposition = 'needs-triage'
        # Active/uncertain ownership always wins; reprioritization must not preempt it.
        state = 'active' if number in active else 'review' if disposition == 'review' else 'blocked' if blockers else 'ready' if disposition == 'candidate' else disposition
        age = max(0, (now - dt.datetime.fromisoformat(issue['createdAt'].replace('Z', '+00:00'))).days)
        unlocks = sum(1 for item in entries.values() if number in item.get('dependsOn', [])
                      and catalog.get(item['issue'], {}).get('state', '').upper() == 'OPEN')
        rows.append({'issue': number, 'title': issue['title'], 'state': state, 'priority': entry.get('priority', 2) if entry else 2,
                     'unblocks': unlocks, 'ageDays': age, 'updatedAt': issue.get('updatedAt'), 'planStale': stale, 'blockers': blockers,
                     'nextSlice': entry.get('nextSlice', '') if entry else '',
                     'reason': 'Issue or referenced PR evidence changed since audit; refresh plan before assignment' if stale else entry.get('reason', 'Inspect before assignment') if entry else 'Not in audited plan; inspect before assignment'})
    ready = sorted((row for row in rows if row['state'] == 'ready'),
                   key=lambda row: (row['priority'], -row['unblocks'], -row['ageDays'], row['issue']))
    return {'schemaVersion': 1, 'planVersion': plan['version'], 'repository': plan['repository'],
            'observedAt': now.isoformat(), 'ready': ready, 'issues': sorted(rows, key=lambda row: row['issue']),
            'limitations': ['Issue closure is prerequisite evidence, not proof of deployment.',
                            'Revalidate issue revisions, ownership, runtime capabilities and exact scope before dispatch.',
                            'No semantic duplicate inference or automatic closure; recommendations require evidence review.']}


def validate_plan_schema(plan, path=''):
    """Validate that a plan dict has the required input schema fields."""
    prefix = f'{path}: ' if path else ''
    for key in ('version', 'repository', 'items'):
        if key not in plan:
            raise ValueError(f'{prefix}Plan missing required input field "{key}"')
    if not isinstance(plan['items'], list):
        raise ValueError(f'{prefix}Plan "items" must be a list')
    if 'schemaVersion' in plan or 'ready' in plan or 'issues' in plan:
        raise ValueError(f'{prefix}Plan has output schema fields (schemaVersion/ready/issues); use an input plan with "items"')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--issues', type=Path, help='Offline snapshot with repository and issues keys; omit to fetch using gh')
    parser.add_argument('--plan', type=Path, default=Path('docs/backlog-plan.json'))
    parser.add_argument('--active', type=int, nargs='*', default=[], help='Issue IDs with active or uncertain ownership')
    args = parser.parse_args()
    plan = json.loads(args.plan.read_text())
    validate_plan_schema(plan, str(args.plan))
    if args.issues:
        snapshot = json.loads(args.issues.read_text())
        if snapshot['repository'].casefold() != plan['repository'].casefold():
            raise ValueError('Issue snapshot belongs to a different repository')
        issues = snapshot['issues']
        pulls = snapshot.get('pulls', [])
    else:
        issues = json.loads(subprocess.check_output(['gh', 'issue', 'list', '--repo', plan['repository'],
            '--state', 'all', '--limit', '1000', '--json', 'number,title,state,createdAt,updatedAt'], text=True))
        pulls = json.loads(subprocess.check_output(['gh', 'pr', 'list', '--repo', plan['repository'],
            '--state', 'all', '--limit', '1000', '--json', 'number,state,headRefOid'], text=True))
        if len(issues) >= 1000 or len(pulls) >= 1000:
            raise ValueError('Snapshot may be truncated; use a complete offline snapshot')
    print(json.dumps(prioritize(issues, plan, args.active, pulls=pulls), indent=2))


if __name__ == '__main__':
    main()

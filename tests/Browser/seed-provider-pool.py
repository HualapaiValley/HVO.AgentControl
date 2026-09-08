"""Seed one synthetic exhausted pool in a disposable published-UI database only."""
import sqlite3
import sys
import time

with sqlite3.connect(sys.argv[1]) as db:
    # Plain INSERT deliberately refuses to overwrite an existing fixture.
    db.execute('''INSERT INTO ProviderPool
               (Id, ProviderId, State, RetryAt, ObservedAt, Revision, ConsecutiveFailures, LastCommandId, RecoveryCommandId)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)''',
               ('provider:fixture-provider', 'fixture-provider', 'Exhausted', None,
                 int(time.time() * 1000), 1, 1, 'fixture-command', 'fixture-reserved-request'))

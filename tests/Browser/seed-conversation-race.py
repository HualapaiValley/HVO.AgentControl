"""Seed equal-revision workers in a disposable published-UI database."""
import sqlite3
import sys


def insert(db, table, values):
    for _, name, kind, required, default, _ in db.execute(f'PRAGMA table_info("{table}")'):
        if required and default is None and name not in values:
            values[name] = "" if kind == "TEXT" else 0
    fields = ",".join(f'"{name}"' for name in values)
    placeholders = ",".join("?" for _ in values)
    db.execute(f'INSERT INTO "{table}" ({fields}) VALUES ({placeholders})', tuple(values.values()))


with sqlite3.connect(sys.argv[1]) as db:
    runtime = "conversation-race-runtime"
    insert(db, "Runtimes", {"Id": runtime, "Name": "Conversation race fixture", "DesiredConnected": 0,
                            "ManagedServerId": "conversation-race-server", "ModelsJson": "[]", "CapabilitiesJson": "{}"})
    for worker, name in [("conversation-race-a", "Conversation race A"),
                         ("conversation-race-b", "Conversation race B")]:
        insert(db, "Workers", {"Id": worker, "Name": name, "RuntimeId": runtime,
                               "ManagedServerId": "conversation-race-server", "Role": "Worker",
                               "Activity": "Idle", "Stale": 1, "Revision": 7,
                               "CapabilitiesJson": "{}", "ModelsJson": "[]",
                               "NativeSessionId": "ses_" + worker, "Directory": "/fixture/" + worker})

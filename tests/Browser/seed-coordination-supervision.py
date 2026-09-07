"""Create a disconnected synthetic run in a disposable published-UI database."""
import json
import sqlite3
import sys
import time


def insert(db, table, values):
    # Fill required legacy columns without copying owner/runtime credentials.
    for _, name, kind, required, default, _ in db.execute(f'PRAGMA table_info("{table}")'):
        if required and default is None and name not in values:
            values[name] = "" if kind == "TEXT" else 0
    fields = ",".join(f'"{name}"' for name in values)
    placeholders = ",".join("?" for _ in values)
    db.execute(f'INSERT INTO "{table}" ({fields}) VALUES ({placeholders})', tuple(values.values()))


with sqlite3.connect(sys.argv[1]) as db:
    now = int(time.time() * 1000)
    insert(db, "Runtimes", {"Id": "supervision-runtime", "Name": "Disconnected browser fixture",
                            "DesiredConnected": 0, "ModelsJson": "[]", "CapabilitiesJson": "{}"})
    for worker, role in [("supervision-coordinator", "Coordinator"), ("supervision-worker", "Worker")]:
        insert(db, "Workers", {"Id": worker, "Name": worker, "RuntimeId": "supervision-runtime", "Role": role,
                               "Activity": "Idle", "Stale": 1, "CapabilitiesJson": "{}", "ModelsJson": "[]",
                               "NativeSessionId": "ses_" + worker, "Directory": "/fixture/" + worker})
    insert(db, "CoordinationRuns", {"Id": "supervision-browser-run", "CoordinatorWorkerId": "supervision-coordinator",
                                   "Instruction": "Existing bounded browser task", "WorkerIdsJson": json.dumps(["supervision-worker"]),
                                   "State": "Paused", "Round": 1, "MaxRounds": 1, "TurnsPerWindow": 1,
                                   "TurnWindowMinutes": 60, "InputJson": "{}", "DecisionJson": "{}", "CreatedAt": now})

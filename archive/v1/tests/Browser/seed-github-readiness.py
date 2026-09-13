"""Seed a stored-but-migration-required GitHub grant in a disposable UI database."""
import json
import sqlite3
import sys
import time


def insert(db, table, values):
    for _, name, kind, required, default, _ in db.execute(f'PRAGMA table_info("{table}")'):
        if required and default is None and name not in values:
            values[name] = "" if kind == "TEXT" else 0
    fields = ",".join(f'"{name}"' for name in values)
    placeholders = ",".join("?" for _ in values)
    db.execute(f'INSERT INTO "{table}" ({fields}) VALUES ({placeholders})', tuple(values.values()))


with sqlite3.connect(sys.argv[1]) as db:
    runtime_id = "github-readiness-runtime"
    insert(db, "Runtimes", {
        "Id": runtime_id,
        "Name": "GitHub readiness fixture",
        "DesiredConnected": 0,
        "ModelsJson": "[]",
        "CapabilitiesJson": "{}",
    })
    insert(db, "GitHubAccess", {
        "Id": runtime_id,
        "AppId": 1,
        "InstallationId": 2,
        "PrivateKeyReference": "unused-browser-fixture",
        "RepositoriesJson": json.dumps(["Owner/Repo"]),
        "State": "MigrationRequired",
        "CredentialState": "Delivered",
        "Detail": "Credential is stored, but the running owned server needs a fresh managed environment.",
        "ExpiresAt": int(time.time() * 1000) + 3_600_000,
        "ChecksPermission": "Granted",
        "CommitStatusesPermission": "Granted",
        "ActionsPermission": "Denied",
    })

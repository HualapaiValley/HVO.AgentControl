"""Seed control-service receipts only in an explicitly disposable UI database."""
import json
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
    runtime = "control-browser-service"
    worker = "control-browser-host"
    models = json.dumps([
        {"providerId": "fixture", "modelId": "original", "name": "Original model", "variants": ["medium"]},
        {"providerId": "fixture", "modelId": "replacement", "name": "Replacement model", "variants": ["high"]},
    ])
    insert(db, "Runtimes", {"Id": runtime, "ConnectionKind": "ControlHttp", "Name": "Browser control sidecar",
                            "DesiredConnected": 0, "Health": "Unknown", "Transport": "Disconnected",
                            "ManagedServerId": "browser-persistent-instance", "ModelsJson": models, "CapabilitiesJson": "{}"})
    insert(db, "ControlServices", {"Id": runtime, "Endpoint": "http://browser-sidecar.invalid:4096",
                                   "InstanceId": "browser-persistent-instance", "IncarnationId": "browser-process-instance",
                                   "StartedAt": "2026-09-08T00:00:00Z", "Revision": 1})
    insert(db, "Workers", {"Id": worker, "RuntimeId": runtime, "Name": "Browser host operations", "Project": "host",
                           "ManagedServerId": "browser-persistent-instance", "NativeSessionId": "ses_browser_persistent_control",
                           "Directory": "/var/lib/opencode/workspaces/control", "Role": "Coordinator", "Activity": "Active",
                           "Stale": 1, "ModelsJson": models, "CapabilitiesJson": "{}", "ProviderId": "fixture",
                           "ModelId": "original", "Variant": "medium", "Revision": 3, "SettingsRevision": 2})
    for scope, kind, state, native, target in [
        ("host", "HostOperations", "Ready", "ses_browser_persistent_control", worker),
        ("browser-workgroup", "Workgroup", "Queued", "", "control-browser-pending"),
        ("browser-rejected", "Workgroup", "Failed", "", "control-browser-failed"),
    ]:
        command = "control-browser-create-" + scope
        insert(db, "Commands", {"Id": command, "RuntimeId": runtime, "Kind": "CreateControlSession",
                                "State": "Finished" if state == "Ready" else "Failed" if state == "Failed" else "DeliveryUnknown",
                                "Payload": json.dumps({"id": command, "scopeKind": kind, "scopeId": scope, "name": scope}),
                                "Detail": "Native session creation unconfirmed; reconcile without replay."})
        insert(db, "ControlSessions", {"Id": "control-browser-binding-" + scope, "ControlServiceId": runtime,
                                       "ScopeKind": kind, "ScopeId": scope, "WorkerId": target, "State": state,
                                       "CreationCommandId": command, "NativeSessionId": native, "Revision": 1})
    insert(db, "Commands", {"Id": "control-browser-queued-prompt", "RuntimeId": runtime, "WorkerId": worker,
                            "Kind": "Prompt", "State": "Queued", "Payload": json.dumps({"id": "control-browser-queued-prompt",
                            "expectedRevision": 3, "text": "Preserve queued model settings"})})

import assert from "node:assert/strict";
import { stateKind, remoteStateKind, remoteStatePresentation } from "../../src/HVO.AgentControl/wwwroot/js/terminal.js";

// Independently enumerated server vocabulary, grouped by the owning C# source.
// This list is deliberately NOT derived from the implementation's STATE_SOURCES
// or from any terminal-array grouping: if the client map drifts, or a server
// value is added without a home in stateKind, this file fails.
//
// Each comment names the exact source of truth so the mapping can be re-checked
// without reading the whole implementation.
const serverStates = {
  // src/HVO.AgentControl/Runtime/ControlStatus.cs
  // enum ControlState + ControlStateExtensions.ToWireValue
  controlWire: {
    disabled: "idle",
    starting: "loading",
    ready: "ready",
    degraded: "ready",
    faulted: "faulted",
    stopped: "idle",
  },

  // src/HVO.AgentControl/Runtime/ControlStatus.cs
  // ControlStatus.SessionState ("idle", "busy" or null when unknown)
  session: {
    idle: "idle",
    busy: "ready",
  },

  // src/HVO.AgentControl/Organization/PortalOrganizationReadModel.cs
  // EmployeeAvailabilityCategories
  availability: {
    ready: "ready",
    provisioning: "loading",
    held: "held",
    "reconciliation-required": "held",
    interrupted: "faulted",
    "reload-required": "held",
    "orientation-failed": "faulted",
    "orientation-stale": "held",
    "runtime-unavailable": "faulted",
  },

  // src/HVO.AgentControl/Organization/RemoteWorkerStore.cs
  // RecordWorkerConnectionState allow-list
  remoteConnection: {
    disconnected: "idle",
    connecting: "loading",
    authenticated: "ready",
    expired: "faulted",
    held: "held",
  },

  // src/HVO.AgentControl.Worker/WorkerStore.cs
  // SetProcess / process_slot CHECK(state IN(...))
  process: {
    stopped: "idle",
    starting: "loading",
    running: "ready",
    exited: "faulted",
    "protocol-failed": "faulted",
    "transport-uncertain": "faulted",
  },

  // src/HVO.AgentControl.Worker/WorkerStore.cs
  // BeginSessionOperation / session_operation CHECK(state IN(...))
  sessionOperation: {
    none: "idle",
    creating: "loading",
    loading: "loading",
    uncertain: "faulted",
    bound: "ready",
  },
};

let known = 0;
for (const [source, states] of Object.entries(serverStates)) {
  for (const [state, kind] of Object.entries(states)) {
    assert.equal(stateKind(state), kind, `${source}: ${state} maps to ${kind}`);
    // Same value in every case/separator spelling the wire can carry.
    assert.equal(
      stateKind(`  ${state.replaceAll("-", "_").toUpperCase()}  `),
      kind,
      `${source}: ${state} normalizes to ${kind}`,
    );
    assert.notEqual(stateKind(state), "unknown", `${source}: ${state} must not be unknown`);
    known += 1;
  }
}

// Every enumerated known state maps to something other than unknown; only a
// literal/foreign value may be unknown.
for (const state of ["", "unknown", "runway", "not-running", "healthy-ish", "runtime-unavailable-now"]) {
  assert.equal(stateKind(state), "unknown", `${state || "empty"} remains unknown`);
}
assert.notEqual(stateKind("runtime-unavailable"), "ready");
assert.notEqual(stateKind("runtime-unavailable"), "unknown");

// Precedence: availability held/faulted overrides a benign control/session
// state. reconciliation-required is the reachable remote case (a leased-out or
// recovery-held worker whose cursor can still read authenticated).
assert.equal(
  remoteStateKind({ controlStatus: "authenticated" }, { availability: "reconciliation-required" }),
  "held",
  "remote reconciliation-required wins over authenticated",
);
assert.equal(
  remoteStateKind({ controlStatus: "ready" }, { availability: "interrupted" }),
  "faulted",
  "remote interrupted wins over ready",
);
assert.equal(
  remoteStateKind({ controlStatus: "ready" }, { availability: "held" }),
  "held",
  "availability held wins over ready",
);
// Availability that is merely benign does not override a more specific
// control/session signal.
assert.equal(
  remoteStateKind({ controlStatus: "degraded" }, { availability: "ready" }),
  "ready",
  "control degraded is used when availability is benign",
);
assert.equal(
  remoteStateKind({ controlStatus: "authenticated" }, { availability: "provisioning" }),
  "loading",
  "provisioning wins over an already authenticated bridge",
);
assert.equal(
  remoteStateKind({ controlStatus: "disconnected" }, { availability: "provisioning" }),
  "loading",
  "provisioning wins over a not-yet-connected bridge",
);
assert.equal(
  remoteStateKind({ sessionState: "busy" }, { availability: "ready" }),
  "ready",
  "busy session is a controllable ready",
);
// Mixed/foreign shapes fall back to the control/session value, then unknown.
assert.equal(remoteStateKind({}, { availability: "ready" }), "ready");
assert.equal(
  remoteStateKind({ controlStatus: "authenticated" }, { availability: "runtime-unavailable" }),
  "faulted",
  "runtime-unavailable wins over authenticated",
);
assert.deepEqual(
  remoteStatePresentation({ controlStatus: "authenticated" }, { availability: "quarantined" }),
  { kind: "unknown", rawState: "quarantined" },
  "a non-empty foreign availability fails closed and remains visible instead of becoming ready",
);
assert.equal(remoteStateKind({}, {}), "unknown");
assert.equal(remoteStateKind(null, null), "unknown");

console.log(`state-kind tests: ${known} known server states across ${Object.keys(serverStates).length} sources passed`);

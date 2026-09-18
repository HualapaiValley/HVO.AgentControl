// AgentControl V2 terminal portal runtime.
//
// Loaded as an external ES module after the locally bundled @xterm/xterm and
// @xterm/addon-fit UMD builds. There is intentionally no SignalR / Blazor
// circuit here: the static server render is enhanced by
//   - polling the selected employee's authoritative status (bounded to one
//     request every 2 seconds), and
//   - attaching xterm.js to the same-origin WebSocket at /terminal.
//
// Wire protocol (JSON text frames):
//   client -> server  { "type": "input",  "data": "..." }
//                     { "type": "resize", "cols": <n>, "rows": <n> }
//   server -> client  { "type": "output", "encoding": "base64", "data": "..." }
//                     { "type": "output", "encoding": "text",   "data": "..." }
//                     { "type": "error",  "message": "..." }
//
// Output is written straight into xterm's own bounded scrollback. No duplicate
// unbounded debug array is kept in this module.

const POLL_INTERVAL_MS = 2000;

const COLS_MIN = 20;
const COLS_MAX = 300;
// Rows are capped at 100 to match the server-side rules in
// Terminal/TerminalProtocol.cs and Terminal/pty_bridge.py. Sending a larger
// value would only be clamped again, leaving xterm and the PTY desynchronized.
const ROWS_MIN = 5;
const ROWS_MAX = 100;

const INTERRUPT_TIMEOUT_MS = 10000;

// Model selection is confirmed by the runtime, never optimistically. The POST
// is bounded like the interrupt request, and a short confirmation guard keeps a
// poll response that was already in flight from rolling the choice back before
// the runtime's own snapshot catches up.
const MODEL_TIMEOUT_MS = 10000;
const MODEL_CONFIRM_GUARD_MS = 8000;

// Auto reattach is bounded. A socket that had been attached retries from 2s;
// a handshake that never opened (for example 409 "one viewer" or 403) backs off
// faster and further. After MAX_AUTO_ATTEMPTS the client stops hammering and
// waits for the operator to use Reconnect.
const RECONNECT_BASE_MS = 2000;
const RECONNECT_MAX_MS = 30000;
const RECONNECT_HANDSHAKE_BASE_MS = 5000;
const RECONNECT_HANDSHAKE_MAX_MS = 60000;
const MAX_AUTO_ATTEMPTS = 5;

const STATUS_ENDPOINT = "/api/control";
const MODEL_ENDPOINT = "/api/control/model";
const CANCEL_ENDPOINT = "/api/control/cancel";

const DEFAULT_ORGANIZATION = "AgentControl Development";

const CONNECTION_LABELS = {
    idle: "Idle",
    connecting: "Attaching",
    attached: "Attached",
    detached: "Detached",
    disconnected: "Disconnected",
    unavailable: "Unavailable",
    faulted: "Faulted",
};

function clamp(value, min, max) {
    const rounded = Math.round(Number(value));
    if (!Number.isFinite(rounded)) {
        return null;
    }
    return Math.min(max, Math.max(min, rounded));
}

// Every reachable server-side state string, enumerated per source so a state
// added on the server cannot silently collapse to "unknown". The wire values
// are copied from the owning C# constants, not inferred from UI buckets:
//
//   control wire     Runtime/ControlStatus.cs ControlState / ToWireValue
//   session          Runtime/ControlStatus.cs ControlStatus.SessionState
//   availability     Organization/PortalOrganizationReadModel.cs
//                    EmployeeAvailabilityCategories (plus literal "unknown")
//   remote connection Organization/RemoteWorkerStore.cs
//                    RecordWorkerConnectionState allow-list
//   process          Worker/WorkerStore.cs SetProcess / process_slot CHECK
//   session op       Worker/WorkerStore.cs BeginSessionOperation /
//                    session_operation CHECK (state literal "none")
//
// The UI kind is a presentation bucket: ready=healthy/controllable,
// loading=in progress, held=paused pending operator action,
// faulted=terminal/recovery failure, idle=cleanly inactive.
const STATE_SOURCES = {
    control: {
        ready: ["ready", "degraded"],
        loading: ["starting"],
        faulted: ["faulted"],
        idle: ["disabled", "stopped"],
    },
    session: {
        ready: ["busy"],
        idle: ["idle"],
    },
    availability: {
        ready: ["ready"],
        loading: ["provisioning"],
        held: ["held", "reconciliation-required", "reload-required", "orientation-stale"],
        faulted: ["orientation-failed", "runtime-unavailable", "interrupted"],
    },
    remoteConnection: {
        ready: ["authenticated"],
        loading: ["connecting"],
        held: ["held"],
        faulted: ["expired"],
        idle: ["disconnected"],
    },
    process: {
        ready: ["running"],
        loading: ["starting"],
        faulted: ["exited", "protocol-failed", "transport-uncertain"],
        idle: ["stopped"],
    },
    sessionOperation: {
        ready: ["bound"],
        loading: ["creating", "loading"],
        faulted: ["uncertain"],
        idle: ["none"],
    },
};

const STATE_KINDS = new Map();
for (const kind of ["ready", "loading", "held", "faulted", "idle"]) {
    for (const source of Object.values(STATE_SOURCES)) {
        for (const state of source[kind] || []) {
            STATE_KINDS.set(state, kind);
        }
    }
}

export function stateKind(rawState) {
    const value = normalizeState(rawState);
    return STATE_KINDS.get(value) || "unknown";
}

function normalizeState(rawState) {
    return String(rawState ?? "")
        .trim()
        .toLowerCase()
        .replace(/[\s_]+/g, "-")
        .replace(/-+/g, "-");
}

// Derives the display kind and text source for a remote-owned employee.
// Availability is the host-computed lifecycle authority: every non-ready
// availability state, including a new/foreign value, wins over lower-level
// connection/session detail. This prevents an authenticated bridge from
// painting a worker green while it is provisioning, held, interrupted, or in a
// lifecycle state this client does not yet understand. Once availability is
// ready (or absent), connection/session state may refine the presentation.
export function remoteStatePresentation(runtime, data) {
    const runtimeData = runtime && typeof runtime === "object" ? runtime : {};
    const employee = data && typeof data === "object" ? data : {};
    const availability = normalizeState(employee.availability);
    const availabilityKind = stateKind(availability);
    if (availability && availabilityKind !== "ready") {
        return { kind: availabilityKind, rawState: employee.availability };
    }
    const rawState = runtimeData.controlStatus || runtimeData.sessionState || employee.availability;
    return { kind: stateKind(rawState), rawState };
}

export function remoteStateKind(runtime, data) {
    return remoteStatePresentation(runtime, data).kind;
}

function displayState(rawState) {
    if (typeof rawState !== "string" || rawState.trim() === "") {
        return "Unknown";
    }
    return rawState.trim().replace(/[_-]+/g, " ");
}

// The runtime catalog is presented as { id, name, provider } but every field
// is treated as optional/foreign so a partial payload cannot break the select.
function providerFromId(id) {
    const text = String(id || "");
    const slash = text.indexOf("/");
    return slash > 0 ? text.slice(0, slash) : "Other";
}

function normalizeCatalog(rawCatalog) {
    if (!Array.isArray(rawCatalog)) {
        return [];
    }
    const seen = new Set();
    const models = [];
    for (const item of rawCatalog) {
        if (!item || typeof item !== "object") {
            continue;
        }
        const id = typeof item.id === "string" ? item.id.trim() : "";
        if (!id || seen.has(id)) {
            continue;
        }
        seen.add(id);
        models.push({
            id,
            name: typeof item.name === "string" && item.name.trim() !== "" ? item.name.trim() : id,
            provider:
                typeof item.provider === "string" && item.provider.trim() !== ""
                    ? item.provider.trim()
                    : providerFromId(id),
        });
    }
    return models;
}

function makeOption(value, label) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    return option;
}

class TerminalPortal {
    constructor(root) {
        this.root = root;
        this.mount = root.querySelector("[data-terminal]");
        this.overlay = root.querySelector('[data-field="terminal-overlay"]');
        this.modelSelect = root.querySelector("[data-model-select]");
        this.modelReceipt = root.querySelector("[data-model-receipt]");

        this.term = null;
        this.fit = null;
        this.socket = null;
        this.outputDecoder = null;
        this.resizeObserver = null;

        this.running = false;
        this.terminalReady = false;
        this.selectedEmployeeId = "";
        this.selectedTerminalUrl = "";
        // Ownership is unknown until employee-detail.js supplies an exact
        // selection. Treating first paint as host-owned can apply host telemetry
        // (or open its terminal) before the requested employee is known.
        this.hostOwned = null;
        this.runtimeState = "";
        this.runtimeSessionId = "";
        this.canControl = false;
        this.runtimeKind = "unknown";
        this.manualDetach = false;
        this.everAttached = false;
        this.cancelInFlight = false;
        this.cancelTimedOut = false;
        this.pollTimer = null;
        this.pollAbort = null;
        this.cancelAbort = null;
        this.notifiedDetachedInput = false;
        this.awaitingObservedIdle = false;
        // employee-detail.js may finish its read before this module starts. Keep
        // the replay inert until start() has created xterm and rendered the base
        // connection state, so an immediately attachable employee cannot open a
        // socket before output has somewhere to go.
        this.pendingInitialEmployee = root.agentControlSelectedEmployee || null;

        // Host model dropdown state. The authoritative value comes from /api/control;
        // the select is only reconciled when the operator is not interacting and
        // no change request is pending.
        this.modelCatalog = [];
        this.authoritativeModel = "";
        // A host session may support model sync; a remote viewer never does.
        // Leaving this false until an authoritative host status says otherwise
        // keeps the selector disabled for a remote employee on first paint.
        this.modelSyncSupported = false;
        this.modelChangeInFlight = false;
        this.modelAbort = null;
        this.modelTimedOut = false;
        this.modelGuard = null;
        this.modelAwaiting = null;
        // A non-2xx (or interrupted) model request is not proof the change did
        // not apply. While set, a status poll may still observe the requested
        // model and turn the uncertainty into a confirmed receipt.
        this.modelUnconfirmed = null;

        // Bounded auto-reattach backoff.
        this.connectFailures = 0;
        this.nextConnectAt = 0;
        this.autoConnectSuppressed = false;
        this.attachedThisAttempt = false;

        this.buttons = {};
        root.querySelectorAll("[data-action]").forEach((button) => {
            this.buttons[button.dataset.action] = button;
        });
        this.bindControls();
        this.bindModelControl();

        // Registered exactly once per page (not per start/stop cycle) so a
        // bfcache pagehide/pageshow pair cannot stack duplicate handlers.
        this.onVisibility = () => this.handleVisibility();
        this.onPageHide = () => this.stop("pagehide");
        this.onPageShow = (event) => {
            if (event.persisted) {
                this.start();
            }
        };
        document.addEventListener("visibilitychange", this.onVisibility, { passive: true });
        window.addEventListener("pagehide", this.onPageHide);
        window.addEventListener("pageshow", this.onPageShow);
        root.addEventListener("agentcontrol:employee-selected", (event) => {
            if (!this.running) {
                this.pendingInitialEmployee = event.detail;
                return;
            }
            this.selectEmployee(event.detail);
        });
    }

    // ---- lifecycle -------------------------------------------------------

    start() {
        if (this.running) {
            return;
        }
        this.running = true;
        this.createTerminal();
        this.applyFit();
        this.renderConnection("idle", true);
        const initialEmployee = this.pendingInitialEmployee;
        this.pendingInitialEmployee = null;
        if (initialEmployee) {
            // selectEmployee performs the single authoritative poll for a
            // first selection. Do not also resume below, or the first
            // selection would poll twice.
            this.selectEmployee(initialEmployee);
        } else if (this.hostOwned !== null && this.selectedEmployeeId) {
            // A bfcache pageshow restores a page whose selection is already
            // established and whose ownership/id/url survived stop(). Resume
            // authority immediately instead of leaving the pre-hide telemetry
            // on screen: pollOnce refreshes status, and reattaches through the
            // normal status path once the runtime is (still) ready.
            this.resumeSelected();
        }
        this.updateButtons();
    }

    // Re-establishes an already-selected terminal after a stop/start cycle
    // (bfcache pageshow). The selection is authoritative and retained, so the
    // existing ready snapshot may reopen the socket at once; the immediate poll
    // then reconciles every displayed field with the server.
    resumeSelected() {
        if (this.canAttach()) {
            this.maybeAutoConnect();
        } else {
            this.renderNotAttachable();
        }
        this.pollOnce();
    }

    // Tears down the live transport and any in-flight work, but deliberately
    // retains selectedEmployeeId / selectedTerminalUrl / hostOwned and the last
    // authoritative snapshot so a bfcache pageshow can resume the exact
    // selection. Only the socket is closed here.
    stop() {
        this.running = false;
        if (this.pollTimer) {
            clearTimeout(this.pollTimer);
            this.pollTimer = null;
        }
        if (this.pollAbort) {
            this.pollAbort.abort();
            this.pollAbort = null;
        }
        if (this.cancelAbort) {
            this.cancelAbort.abort();
            this.cancelAbort = null;
        }
        if (this.modelAbort) {
            this.modelAbort.abort();
            this.modelAbort = null;
        }
        this.closeSocket(1000, "pagehide");
    }

    handleVisibility() {
        if (document.hidden) {
            if (this.pollTimer) {
                clearTimeout(this.pollTimer);
                this.pollTimer = null;
            }
            return;
        }
        if (this.running && !this.pollTimer) {
            this.pollOnce();
        }
    }

    // ---- readiness gates -------------------------------------------------

    // An established session is one the runtime has actually handed us a session
    // id for. Both ready and degraded qualify: a degraded bootstrap is still an
    // attached, recoverable session, and locking the operator out of terminal
    // recovery or cancellation would strand the session. Starting, faulted,
    // stopped and unknown states never qualify.
    //
    // `canControl` is the parent's gate. It is deliberately never trusted on its
    // own: a status payload must also carry a ready/degraded state and a session
    // id, so a fake/foreign status cannot turn the controls on.
    isRuntimeEstablished() {
        return (this.runtimeState === "ready" || this.runtimeState === "degraded")
            && Boolean(this.runtimeSessionId);
    }

    isRuntimeReady() {
        return this.isRuntimeEstablished();
    }

    isRuntimeControllable() {
        return this.isRuntimeEstablished() && this.canControl === true;
    }

    canAttach() {
        const runtimeAvailable = this.hostOwned === true
            ? this.isRuntimeEstablished()
            : this.hostOwned === false && this.runtimeKind === "ready";
        return runtimeAvailable
            && this.terminalReady === true
            && Boolean(this.selectedEmployeeId)
            && Boolean(this.selectedTerminalUrl);
    }

    selectEmployee(detail) {
        const employee = detail && typeof detail === "object" ? detail : {};
        const nextId = typeof employee.id === "string" ? employee.id.trim() : "";
        const runtime = employee.runtime && typeof employee.runtime === "object" ? employee.runtime : {};
        const nextHostOwned = nextId ? runtime.hostOwned === true : null;
        const terminal = employee.terminal && typeof employee.terminal === "object" ? employee.terminal : {};
        const nextUrl = nextId && terminal.available === true && typeof terminal.url === "string"
            ? terminal.url.trim()
            : "";
        const previousHostOwned = this.hostOwned;
        const targetChanged = nextId !== this.selectedEmployeeId
            || nextUrl !== this.selectedTerminalUrl
            || nextHostOwned !== this.hostOwned
            || !nextId
            || !nextUrl;
        if (targetChanged) {
            this.manualDetach = false;
            this.closeSocket(1000, "employee-terminal-target-changed");
            this.connectFailures = 0;
            this.nextConnectAt = 0;
            this.autoConnectSuppressed = false;
        }
        if (this.pollTimer) {
            clearTimeout(this.pollTimer);
            this.pollTimer = null;
        }
        if (this.pollAbort) {
            this.pollAbort.abort();
            this.pollAbort = null;
        }
        this.selectedEmployeeId = nextId;
        this.selectedTerminalUrl = nextUrl;
        this.hostOwned = nextHostOwned;
        if (this.hostOwned === null) {
            delete this.root.dataset.hostOwned;
        } else {
            this.root.dataset.hostOwned = String(this.hostOwned);
        }
        const hostNote = this.root.querySelector('[data-model-host-note]');
        if (hostNote) hostNote.hidden = this.hostOwned !== false;
        const cancelNote = this.root.querySelector('[data-remote-cancel-note]');
        if (cancelNote) cancelNote.hidden = this.hostOwned !== false;
        if (this.hostOwned === false) {
            this.applyRemoteStatus(employee);
        } else if (this.hostOwned === null) {
            this.resetUnresolvedTelemetry();
        } else if (previousHostOwned !== true || targetChanged) {
            // Ownership moved to the host (typically remote -> host) or the
            // host target changed. Blank every value the previous selection
            // owned before the first host poll: a stale remote session/model
            // must never be shown as if it were the newly selected host's.
            this.resetHostTelemetry();
        }
        if (this.canAttach()) this.maybeAutoConnect();
        else this.renderNotAttachable();
        this.updateButtons();
        if (this.hostOwned !== null) {
            this.pollOnce();
        }
    }

    // Neutralize the telemetry surface while no exact employee is selected.
    resetUnresolvedTelemetry() {
        this.runtimeKind = "unknown";
        this.runtimeState = "";
        this.runtimeSessionId = "";
        this.canControl = false;
        this.terminalReady = false;
        this.root.dataset.runtimeState = "unknown";
        this.setField("state-detail", "Select an employee");
        this.setField("sessionId", "\u2014");
        this.setField("syncedAt", "\u2014");
        this.hideError();
        this.renderStatePills("unknown");
    }

    // Clear host-owned telemetry to a neutral Synchronizing state. Used when a
    // selection newly becomes host-owned (or swaps host employees) so no remote
    // or previous-employee snapshot lingers during the first host poll. The
    // next authoritative /api/control response repopulates every field.
    resetHostTelemetry() {
        this.runtimeKind = "unknown";
        this.runtimeState = "";
        this.runtimeSessionId = "";
        this.canControl = false;
        this.terminalReady = false;
        this.modelSyncSupported = false;
        this.authoritativeModel = "";
        this.modelCatalog = [];
        this.modelAwaiting = null;
        this.modelUnconfirmed = null;
        this.root.dataset.runtimeState = "unknown";
        this.setField("state-detail", "Synchronizing");
        this.setField("sessionId", "\u2014");
        this.setField("model", "\u2014");
        this.setField("syncedAt", "\u2014");
        this.setModelPlaceholder("Synchronizing\u2026");
        this.renderStatePills("unknown");
        this.hideError();
    }

    // ---- terminal --------------------------------------------------------

    createTerminal() {
        if (this.term || !this.mount) {
            return;
        }
        const TerminalCtor = window.Terminal;
        const FitCtor = window.FitAddon && window.FitAddon.FitAddon;
        if (!TerminalCtor || !FitCtor) {
            // The UMD builds are deferred ahead of this module, but a slow or
            // reordered load should not strand the page: retry briefly.
            this.assetRetries = (this.assetRetries || 0) + 1;
            if (this.assetRetries <= 40) {
                window.setTimeout(() => this.createTerminal(), 50);
            } else {
                this.setOverlay("Terminal assets unavailable");
            }
            return;
        }

        this.fit = new FitCtor();
        this.term = new TerminalCtor({
            convertEol: true,
            cursorBlink: true,
            cursorStyle: "bar",
            fontFamily: "ui-monospace, SFMono-Regular, 'JetBrains Mono', Menlo, Consolas, monospace",
            fontSize: 13,
            lineHeight: 1.15,
            letterSpacing: 0,
            scrollback: 5000,
            screenReaderMode: true,
            allowTransparency: true,
            macOptionIsMeta: true,
            theme: {
                background: "#05080e",
                foreground: "#d7e3f2",
                cursor: "#3fd0d9",
                cursorAccent: "#05080e",
                selectionBackground: "rgba(63, 208, 217, 0.28)",
                black: "#0b111c",
                red: "#f2758e",
                green: "#57d98f",
                yellow: "#e8b45a",
                blue: "#6ea8fe",
                magenta: "#c58cff",
                cyan: "#3fd0d9",
                white: "#d7e3f2",
                brightBlack: "#6f829b",
                brightRed: "#ff9aac",
                brightGreen: "#7cebaa",
                brightYellow: "#f4cd7d",
                brightBlue: "#9cc4ff",
                brightMagenta: "#d9b0ff",
                brightCyan: "#6fe3ea",
                brightWhite: "#f3f7fc",
            },
        });
        this.term.loadAddon(this.fit);
        this.term.open(this.mount);

        this.term.onData((data) => {
            if (!this.isAttached()) {
                if (!this.notifiedDetachedInput) {
                    this.notifiedDetachedInput = true;
                    this.writeNotice("Not attached. Reconnect the terminal to send input.");
                }
                return;
            }
            this.send({ type: "input", data });
        });

        this.term.onResize(({ cols, rows }) => {
            const safeCols = clamp(cols, COLS_MIN, COLS_MAX);
            const safeRows = clamp(rows, ROWS_MIN, ROWS_MAX);
            if (safeCols !== null && safeRows !== null) {
                this.send({ type: "resize", cols: safeCols, rows: safeRows });
            }
        });

        this.boundFit = () => this.fitSoon();
        window.addEventListener("resize", this.boundFit, { passive: true });

        if ("ResizeObserver" in window) {
            this.resizeObserver = new ResizeObserver(() => this.fitSoon());
            this.resizeObserver.observe(this.mount);
        }

        this.setOverlay("Waiting for runtime");
        this.updateButtons();

        // If xterm arrived after the first status poll, attach now.
        if (this.canAttach() && !this.socket && !this.manualDetach) {
            this.maybeAutoConnect();
        }
    }

    fitSoon() {
        if (this.fitQueued) {
            return;
        }
        this.fitQueued = true;
        requestAnimationFrame(() => {
            this.fitQueued = false;
            this.applyFit();
        });
    }

    applyFit() {
        if (!this.term || !this.fit || !this.mount || this.mount.offsetParent === null) {
            return;
        }
        let dimensions = null;
        try {
            dimensions = this.fit.proposeDimensions();
        } catch {
            dimensions = null;
        }
        const cols = dimensions ? clamp(dimensions.cols, COLS_MIN, COLS_MAX) : null;
        const rows = dimensions ? clamp(dimensions.rows, ROWS_MIN, ROWS_MAX) : null;
        if (cols === null || rows === null) {
            return;
        }
        if (cols !== this.term.cols || rows !== this.term.rows) {
            this.term.resize(cols, rows);
        }
    }

    clearView() {
        if (!this.term) {
            return;
        }
        this.term.clear();
        this.writeNotice("View cleared locally. Native session output is unaffected.");
    }

    writeNotice(message) {
        if (!this.term) {
            return;
        }
        const text = String(message || "").replace(/[\u0000-\u001f]/g, " ");
        this.term.write(`\r\n\x1b[38;5;245m[terminal] ${text}\x1b[0m\r\n`);
    }

    // ---- attachment ------------------------------------------------------

    isAttached() {
        return Boolean(this.socket) && this.socket.readyState === WebSocket.OPEN;
    }

    isAttaching() {
        return Boolean(this.socket) && this.socket.readyState === WebSocket.CONNECTING;
    }

    maybeAutoConnect() {
        if (!this.running || !this.term || !this.canAttach() || this.manualDetach || this.socket) {
            return;
        }
        if (this.autoConnectSuppressed) {
            return;
        }
        if (Date.now() < this.nextConnectAt) {
            return;
        }
        this.connect();
    }

    connect() {
        if (this.socket && (this.isAttached() || this.isAttaching())) {
            return;
        }
        if (!this.canAttach()) {
            this.renderNotAttachable();
            return;
        }

        this.manualDetach = false;
        this.notifiedDetachedInput = false;
        this.attachedThisAttempt = false;
        this.outputDecoder = null;
        this.renderConnection("connecting", true);
        this.setOverlay("Attaching to session\u2026");

        const scheme = window.location.protocol === "https:" ? "wss" : "ws";
        let socket;
        try {
            socket = new WebSocket(`${scheme}://${window.location.host}${this.selectedTerminalUrl}`);
        } catch {
            this.renderConnection("faulted", true);
            this.setOverlay("Could not open the terminal transport");
            this.scheduleReconnect();
            return;
        }
        this.socket = socket;

        socket.onopen = () => {
            if (socket !== this.socket) {
                return;
            }
            this.attachedThisAttempt = true;
            this.everAttached = true;
            this.connectFailures = 0;
            this.nextConnectAt = 0;
            this.autoConnectSuppressed = false;
            this.renderConnection("attached", true);
            this.setOverlay("");
            this.applyFit();
            if (this.term) {
                this.send({
                    type: "resize",
                    cols: clamp(this.term.cols, COLS_MIN, COLS_MAX),
                    rows: clamp(this.term.rows, ROWS_MIN, ROWS_MAX),
                });
                this.term.focus();
            }
            this.updateButtons();
        };

        socket.onmessage = (event) => this.handleMessage(event.data);

        socket.onerror = () => {
            // The close event carries the follow-up decision.
        };

        socket.onclose = () => {
            if (socket !== this.socket) {
                return;
            }
            this.socket = null;
            const decoder = this.outputDecoder;
            this.outputDecoder = null;
            if (decoder && this.term) this.term.write(decoder.decode());
            this.handleAttachmentClosed();
        };

        this.updateButtons();
    }

    handleAttachmentClosed() {
        if (this.manualDetach) {
            this.setOverlay("");
            this.renderConnection("detached", true);
        } else if (!this.canAttach()) {
            this.renderNotAttachable();
        } else {
            this.setOverlay("");
            this.renderConnection("disconnected", true);
            this.scheduleReconnect();
        }
        this.updateButtons();
    }

    scheduleReconnect() {
        if (this.manualDetach || !this.canAttach()) {
            return;
        }
        this.connectFailures += 1;
        if (this.connectFailures >= MAX_AUTO_ATTEMPTS) {
            this.autoConnectSuppressed = true;
            this.setOverlay("Reconnect required");
            return;
        }
        const wasAttached = this.attachedThisAttempt;
        const base = wasAttached ? RECONNECT_BASE_MS : RECONNECT_HANDSHAKE_BASE_MS;
        const ceiling = wasAttached ? RECONNECT_MAX_MS : RECONNECT_HANDSHAKE_MAX_MS;
        const delay = Math.min(base * Math.pow(2, this.connectFailures - 1), ceiling);
        this.nextConnectAt = Date.now() + delay;
    }

    renderNotAttachable() {
        if (this.runtimeKind === "faulted") {
            this.renderConnection("faulted", true);
            this.setOverlay("Runtime faulted");
        } else if (this.runtimeKind === "loading") {
            this.renderConnection("unavailable", true);
            this.setOverlay("Runtime loading");
        } else if (this.runtimeKind === "idle") {
            this.renderConnection("unavailable", true);
            this.setOverlay("Runtime stopped");
        } else {
            this.renderConnection("unavailable", true);
            this.setOverlay("Terminal not ready");
        }
    }

    reconnect() {
        this.manualDetach = false;
        this.connectFailures = 0;
        this.nextConnectAt = 0;
        this.autoConnectSuppressed = false;
        this.closeSocket(1000, "reconnect");
        this.renderConnection("idle", true);
        this.connect();
    }

    detach() {
        this.manualDetach = true;
        this.closeSocket(1000, "detach");
        this.renderConnection("detached", true);
        this.writeNotice("Detached from the browser view. The native session keeps running.");
        this.updateButtons();
    }

    closeSocket(code, reason) {
        const socket = this.socket;
        this.socket = null;
        this.outputDecoder = null;
        if (socket) {
            try {
                socket.close(code, reason);
            } catch {
                // Socket was already closing.
            }
        }
    }

    send(payload) {
        if (!this.isAttached()) {
            return;
        }
        try {
            this.socket.send(JSON.stringify(payload));
        } catch {
            this.renderConnection("faulted", true);
        }
    }

    handleMessage(raw) {
        let message;
        try {
            message = JSON.parse(typeof raw === "string" ? raw : new TextDecoder().decode(raw));
        } catch {
            return;
        }
        if (!message || typeof message !== "object") {
            return;
        }
        if (message.type === "output" && typeof message.data === "string") {
            if (!this.term) return;
            if (message.encoding === "text") {
                this.term.write(message.data);
                return;
            }
            if (message.encoding === "base64") {
                try {
                    const binary = atob(message.data);
                    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
                    this.outputDecoder ??= new TextDecoder("utf-8", { fatal: false });
                    this.term.write(this.outputDecoder.decode(bytes, { stream: true }));
                } catch {
                    this.writeNotice("transport error: invalid terminal output encoding");
                }
                return;
            }
            this.writeNotice("transport error: unsupported terminal output encoding");
            return;
        }
        if (message.type === "error") {
            // Error frames describe a rejected command or bridge condition; the
            // socket may still be usable, so surface the text without tearing
            // down the attachment state.
            this.writeNotice(`transport error: ${message.message || "unspecified"}`);
        }
    }

    // ---- status polling --------------------------------------------------

    async pollOnce() {
        if (!this.running || this.hostOwned === null) {
            return;
        }
        if (this.hostOwned === false && !this.selectedEmployeeId) {
            this.resetUnresolvedTelemetry();
            this.closeSocket(1000, "employee-selection-unresolved");
            this.renderNotAttachable();
            this.updateButtons();
            return;
        }
        const controller = new AbortController();
        this.pollAbort = controller;
        const requestedHostOwned = this.hostOwned;
        const requestedEmployeeId = this.selectedEmployeeId;
        try {
            const endpoint = requestedHostOwned === true
                ? STATUS_ENDPOINT
                : `/api/employees/${encodeURIComponent(requestedEmployeeId)}`;
            const response = await fetch(endpoint, {
                method: "GET",
                headers: { Accept: "application/json" },
                cache: "no-store",
                credentials: "same-origin",
                signal: controller.signal,
            });
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }
            const status = await response.json();
            const selectionChanged = requestedHostOwned !== this.hostOwned
                || (requestedHostOwned === false && requestedEmployeeId !== this.selectedEmployeeId);
            if (this.running && !selectionChanged) {
                if (requestedHostOwned === true) this.applyStatus(status);
                else this.applyRemoteStatus(status);
            }
        } catch (error) {
            if (!error || error.name !== "AbortError") {
                this.setField("syncedAt", "status unavailable");
            }
        } finally {
            const ownsPoll = this.pollAbort === controller;
            if (ownsPoll) {
                this.pollAbort = null;
            }
            if (ownsPoll && this.running && !document.hidden && this.hostOwned !== null
                && (this.hostOwned === true || Boolean(this.selectedEmployeeId))) {
                this.pollTimer = window.setTimeout(() => this.pollOnce(), POLL_INTERVAL_MS);
            }
        }
    }

    // Run one immediate poll so a confirmed model change is reflected without
    // waiting up to POLL_INTERVAL_MS. Coalesces with any in-flight poll.
    async refreshStatus() {
        if (!this.running || this.pollAbort) {
            return;
        }
        if (this.pollTimer) {
            clearTimeout(this.pollTimer);
            this.pollTimer = null;
        }
        await this.pollOnce();
    }

    applyRemoteStatus(employee) {
        if (this.hostOwned !== false) {
            return;
        }
        const data = employee && typeof employee === "object" ? employee : {};
        const runtime = data.runtime && typeof data.runtime === "object" ? data.runtime : {};
        const terminal = data.terminal && typeof data.terminal === "object" ? data.terminal : {};
        const { kind, rawState } = remoteStatePresentation(runtime, data);
        const previousKind = this.root.dataset.runtimeState;
        const nextUrl = terminal.available === true && typeof terminal.url === "string" ? terminal.url : "";
        if (nextUrl !== this.selectedTerminalUrl) {
            this.closeSocket(1000, "remote-terminal-target-changed");
            this.selectedTerminalUrl = nextUrl;
            this.connectFailures = 0;
            this.nextConnectAt = 0;
            this.autoConnectSuppressed = false;
        }

        this.runtimeKind = kind;
        this.runtimeState = typeof rawState === "string" ? rawState.trim().toLowerCase() : "";
        this.runtimeSessionId = typeof runtime.nativeSessionId === "string" ? runtime.nativeSessionId.trim() : "";
        this.canControl = false;
        this.terminalReady = terminal.available === true;
        this.root.dataset.runtimeState = kind;
        this.setField("state-detail", displayState(rawState));
        this.setField("sessionId", this.runtimeSessionId || "\u2014");
        this.authoritativeModel = typeof runtime.controlModel === "string" ? runtime.controlModel : "";
        this.setField("model", this.authoritativeModel || "\u2014");
        this.setModelPlaceholder(this.authoritativeModel || "Unavailable");
        this.selectModelValue(this.authoritativeModel);
        this.setField("syncedAt", new Date().toLocaleTimeString());
        this.renderStatePills(kind);
        if (runtime.sanitizedError) {
            this.showError(runtime.sanitizedError);
        } else {
            this.hideError();
        }

        if (kind !== previousKind) {
            this.connectFailures = 0;
            this.nextConnectAt = 0;
            this.autoConnectSuppressed = false;
        }
        if (this.canAttach()) {
            this.setOverlay(this.autoConnectSuppressed ? "Reconnect required" : "");
            this.maybeAutoConnect();
        } else {
            this.closeSocket(1000, "remote-runtime-not-ready");
            this.renderNotAttachable();
        }
        this.updateButtons();
    }

    applyStatus(status) {
        if (this.hostOwned !== true) {
            return;
        }
        const data = status && typeof status === "object" ? status : {};
        const kind = stateKind(data.state);
        const previousKind = this.root.dataset.runtimeState;
        this.runtimeKind = kind;
        this.runtimeState = typeof data.state === "string" ? data.state.trim().toLowerCase() : "";
        this.runtimeSessionId = typeof data.sessionId === "string" ? data.sessionId.trim() : "";
        this.canControl = data.canControl === true;
        this.root.dataset.runtimeState = kind;

        this.setField("organizationName", data.organizationName || DEFAULT_ORGANIZATION);
        this.setField("state", displayState(data.state));
        this.setField("state-detail", displayState(data.state));
        this.setField("sessionId", data.sessionId || "\u2014");
        this.updateModelControl(data);
        this.setField("transport", data.transport || "ACP");
        this.setField("syncedAt", new Date().toLocaleTimeString());

        this.renderStatePills(kind);

        if (data.error) {
            this.showError(data.error);
        } else {
            this.hideError();
        }

        this.terminalReady = data.terminalReady === true;

        // A state transition invalidates the previous backoff decision so a
        // recovered runtime can reattach promptly.
        if (kind !== previousKind) {
            this.connectFailures = 0;
            this.nextConnectAt = 0;
            this.autoConnectSuppressed = false;
        }

        // "loading / faulted clear": on a fresh transition into a loading or
        // faulted runtime, drop stale viewport content so the operator cannot
        // mistake old output for the current state. The bounded xterm
        // scrollback is the only buffer involved.
        if (kind !== previousKind && (kind === "loading" || kind === "faulted")) {
            if (this.term) {
                this.term.clear();
                this.writeNotice(
                    kind === "faulted"
                        ? "Runtime faulted. View cleared; see the status detail above."
                        : "Runtime loading. View cleared while the runtime starts.",
                );
            }
        }

        this.trackObservedIdle(data);

        // Attach only when the control host reports a ready runtime AND the
        // terminal gate is open. Never reconnect while faulted, stopped or
        // still starting.
        if (this.canAttach()) {
            this.setOverlay(this.autoConnectSuppressed ? "Reconnect required" : "");
            this.maybeAutoConnect();
        } else {
            this.closeSocket(1000, "runtime-not-ready");
            this.renderNotAttachable();
        }

        this.updateButtons();
    }

    trackObservedIdle(data) {
        if (!this.awaitingObservedIdle) {
            return;
        }
        const sessionState = typeof data.sessionState === "string" ? data.sessionState.toLowerCase() : "";
        if (sessionState === "idle") {
            this.awaitingObservedIdle = false;
            this.setReceipt(
                "Observed session idle after the interrupt request. This is a status observation; " +
                "side effects are not rolled back.",
                "ok",
            );
        }
    }

    showError(message) {
        const banner = this.root.querySelector('[data-field="error-banner"]');
        const text = this.root.querySelector('[data-field="error"]');
        const wasHidden = !banner || banner.hidden;
        // Expose the live region before changing its text so role=alert
        // announces a newly visible error. Repeated polls update in place and do
        // not keep pulling an operator back after they scroll elsewhere.
        if (banner) {
            banner.hidden = false;
        }
        if (text) {
            text.textContent = String(message);
        }
        if (banner && wasHidden) {
            banner.scrollIntoView({ block: "nearest", inline: "nearest" });
        }
    }

    hideError() {
        const banner = this.root.querySelector('[data-field="error-banner"]');
        if (banner) {
            banner.hidden = true;
        }
    }

    // ---- cancel (interrupt) ----------------------------------------------

    async interrupt() {
        // Ownership is unknown until an exact employee is selected. That is a
        // selection problem, not a remote-capability problem, and must not be
        // reported as if a remote employee had been chosen.
        if (this.hostOwned === null) {
            this.setReceipt("Interrupt was not sent: select an employee first.", "error");
            return;
        }
        if (this.hostOwned === false && !this.selectedEmployeeId) {
            this.setReceipt(
                "Interrupt was not sent: the selected remote employee id is empty; select an employee first.",
                "error",
            );
            return;
        }
        if (this.hostOwned === false) {
            this.setReceipt("Interrupt was not sent: remote turn cancellation is unavailable from this console.", "error");
            return;
        }
        // Gated on a controllable session (ready or degraded); the button is
        // disabled otherwise.
        if (this.cancelInFlight || !this.isRuntimeControllable()) {
            return;
        }
        this.cancelInFlight = true;
        this.updateButtons();
        this.setReceipt("Interrupt request sent\u2026 waiting for a receipt.", "pending");

        const controller = new AbortController();
        this.cancelAbort = controller;
        this.cancelTimedOut = false;
        const timeout = window.setTimeout(() => {
            this.cancelTimedOut = true;
            controller.abort();
        }, INTERRUPT_TIMEOUT_MS);

        try {
            const response = await fetch(CANCEL_ENDPOINT, {
                method: "POST",
                headers: { Accept: "application/json", "Content-Type": "application/json" },
                credentials: "same-origin",
                body: "{}",
                signal: controller.signal,
            });
            let body = null;
            try {
                body = await response.json();
            } catch {
                body = null;
            }
            const reference = body && (body.receiptId || body.requestId || body.id);
            const suffix = reference ? ` \u00b7 ref ${reference}` : "";

            if (response.status === 202) {
                // 202 Accepted is a request receipt, not a completion.
                this.awaitingObservedIdle = true;
                this.setReceipt(
                    `Interrupt request sent (HTTP 202)${suffix}. Waiting for an observed idle session state; ` +
                    "this is not confirmation the turn completed and does not roll back effects.",
                    "pending",
                );
            } else if (response.ok) {
                this.awaitingObservedIdle = true;
                this.setReceipt(
                    `Interrupt request accepted (HTTP ${response.status})${suffix}. Waiting for an observed idle ` +
                    "session state; this is not confirmation the turn completed.",
                    "pending",
                );
            } else {
                this.setReceipt(
                    `Interrupt request not accepted (HTTP ${response.status})${suffix}. No completion is implied.`,
                    "error",
                );
            }
        } catch (error) {
            if (this.cancelTimedOut) {
                if (this.running) {
                    this.setReceipt(
                        "Interrupt request timed out after 10s without a receipt. The turn state is unknown.",
                        "error",
                    );
                }
            } else if (error && error.name === "AbortError") {
                // Aborted by pagehide; the page is going away, so stay quiet.
            } else if (this.running) {
                const detail = error && error.message ? error.message : "network error";
                this.setReceipt(`Interrupt request failed: ${detail}. No receipt was received.`, "error");
            }
        } finally {
            clearTimeout(timeout);
            this.cancelTimedOut = false;
            if (this.cancelAbort === controller) {
                this.cancelAbort = null;
            }
            this.cancelInFlight = false;
            this.updateButtons();
        }
    }

    // ---- model selection -------------------------------------------------

    isModelSelectFocused() {
        return Boolean(this.modelSelect) && document.activeElement === this.modelSelect;
    }

    isModelSelectBusy() {
        return this.modelChangeInFlight || this.isModelSelectFocused();
    }

    updateModelControl(data) {
        this.modelSyncSupported = data.modelSyncSupported !== false;
        const note = this.root.querySelector('[data-model-sync-note]');
        if (note) note.hidden = this.modelSyncSupported;
        const incoming =
            typeof data.model === "string" && data.model.trim() !== "" ? data.model.trim() : "";

        // A just-confirmed change takes precedence over a poll response that was
        // already in flight when the POST resolved. The guard expires so a real
        // runtime override still wins and an explicit null is still surfaced.
        let actual = incoming;
        if (this.modelGuard) {
            if (Date.now() >= this.modelGuard.expiresAt || this.modelGuard.value === incoming) {
                this.modelGuard = null;
            } else {
                actual = this.modelGuard.value;
            }
        }

        this.authoritativeModel = actual;
        this.setField("model", actual || "\u2014");

        // A missing models field keeps the last known catalog. An explicit empty
        // array means the runtime has no catalog: keep the displayed option but
        // disable the control.
        if (Array.isArray(data.models)) {
            this.modelCatalog = normalizeCatalog(data.models);
        }

        if (!this.isModelSelectBusy()) {
            this.renderModelOptions(actual);
        }
        this.updateModelDisabled();
        this.resolveModelAwaiting(actual);
        this.resolveUnconfirmedModel(actual);
    }

    renderModelOptions(actual) {
        const select = this.modelSelect;
        if (!select) {
            return;
        }
        const catalog = Array.isArray(this.modelCatalog) ? this.modelCatalog : [];
        if (catalog.length === 0) {
            // No catalog: never wipe the current option. On first load this is
            // the server-rendered placeholder; it stays visible but disabled.
            this.selectModelValue(actual);
            return;
        }

        const known = new Set(catalog.map((model) => model.id));
        const fragment = document.createDocumentFragment();

        if (actual && !known.has(actual)) {
            const current = makeOption(actual, `${actual} (not in catalog)`);
            current.disabled = true;
            fragment.appendChild(current);
        } else if (!actual) {
            const unknown = makeOption("", "Unknown model");
            unknown.disabled = true;
            fragment.appendChild(unknown);
        }

        const groups = new Map();
        for (const model of catalog) {
            if (!groups.has(model.provider)) {
                groups.set(model.provider, []);
            }
            groups.get(model.provider).push(model);
        }
        for (const [provider, models] of groups) {
            const group = document.createElement("optgroup");
            group.label = provider;
            for (const model of models) {
                group.appendChild(makeOption(model.id, model.name));
            }
            fragment.appendChild(group);
        }

        select.replaceChildren(fragment);
        this.selectModelValue(actual || "");
    }

    selectModelValue(id) {
        const select = this.modelSelect;
        if (!select) {
            return;
        }
        const target = typeof id === "string" ? id : "";
        const hasTarget = Array.from(select.options).some((option) => option.value === target);
        if (hasTarget && select.value !== target) {
            select.value = target;
        }
    }

    // A remote employee has no host model catalog or host model control.
    // Replace the host-only placeholder (or a stale host catalog) with a single
    // explicit option so the disabled control never implies a pending host sync
    // that cannot occur.
    setModelPlaceholder(text) {
        const select = this.modelSelect;
        if (!select) {
            return;
        }
        const options = Array.from(select.options);
        if (options.length === 1 && options[0].textContent === text && options[0].value === "") {
            return;
        }
        const option = document.createElement("option");
        option.value = "";
        option.textContent = text;
        option.disabled = true;
        option.selected = true;
        select.replaceChildren(option);
    }

    updateModelDisabled() {
        if (!this.modelSelect) {
            return;
        }
        const hasCatalog = Array.isArray(this.modelCatalog) && this.modelCatalog.length > 0;
        this.modelSelect.disabled = !this.hostOwned || !this.modelSyncSupported || !this.isRuntimeReady() || this.modelChangeInFlight || !hasCatalog;
        this.modelSelect.setAttribute("aria-busy", this.modelChangeInFlight ? "true" : "false");
    }

    resolveModelAwaiting(actual) {
        if (!this.modelAwaiting || this.isModelSelectBusy()) {
            return;
        }
        if (actual && actual === this.modelAwaiting.target) {
            this.modelAwaiting = null;
            this.setModelReceipt(`Runtime confirmed the active model: ${actual}.`, "ok");
            return;
        }
        if (Date.now() - this.modelAwaiting.at > MODEL_CONFIRM_GUARD_MS) {
            this.modelAwaiting = null;
            this.setModelReceipt(
                `Runtime did not confirm the requested model; the active model is ${actual || "unknown"}.`,
                "error",
            );
        }
    }

    // A non-2xx (502, 409, ...) or timed-out change request cannot prove the
    // change was not applied. Until a status poll actually observes the
    // requested model we keep the uncertainty receipt and never claim the old
    // model is still in effect or retry on the operator's behalf.
    resolveUnconfirmedModel(actual) {
        if (!this.modelUnconfirmed || this.modelChangeInFlight) {
            return;
        }
        if (actual && actual === this.modelUnconfirmed.target) {
            this.modelUnconfirmed = null;
            this.setModelReceipt(`Runtime later confirmed the active model: ${actual}.`, "ok");
            return;
        }
        if (Date.now() - this.modelUnconfirmed.at > MODEL_CONFIRM_GUARD_MS) {
            // Stop watching after the bounded window. The receipt already tells
            // the operator to re-check, and we deliberately never convert this
            // into an "unchanged" claim.
            this.modelUnconfirmed = null;
        }
    }

    handleModelChange() {
        const select = this.modelSelect;
        if (!this.hostOwned) {
            this.setModelReceipt("Model change was not sent: model selection is unavailable for a remote employee.", "error");
            this.selectModelValue(this.authoritativeModel);
            return;
        }
        if (!select || this.modelChangeInFlight) {
            return;
        }
        const requested = select.value;
        if (!requested) {
            // The placeholder / unknown-model option is not a submission.
            this.selectModelValue(this.authoritativeModel);
            return;
        }
        if (requested === this.authoritativeModel) {
            this.setModelReceipt("", "");
            return;
        }
        const known =
            Array.isArray(this.modelCatalog) && this.modelCatalog.some((model) => model.id === requested);
        if (!known) {
            this.setModelReceipt(
                `Selection ${requested} is not part of the runtime catalog; nothing was submitted.`,
                "error",
            );
            this.selectModelValue(this.authoritativeModel);
            return;
        }
        this.requestModelChange(requested);
    }

    handleModelBlur() {
        if (this.modelChangeInFlight) {
            return;
        }
        // The operator left the control without submitting; snap back to the
        // authoritative model rather than letting the draft linger.
        this.renderModelOptions(this.authoritativeModel);
        this.selectModelValue(this.authoritativeModel);
    }

    async requestModelChange(id) {
        this.modelChangeInFlight = true;
        this.modelAwaiting = null;
        this.modelUnconfirmed = null;
        this.updateModelDisabled();
        this.setModelReceipt(
            `Model change requested: ${id}. Awaiting runtime confirmation\u2026`,
            "pending",
        );

        const controller = new AbortController();
        this.modelAbort = controller;
        this.modelTimedOut = false;
        const timeout = window.setTimeout(() => {
            this.modelTimedOut = true;
            controller.abort();
        }, MODEL_TIMEOUT_MS);

        try {
            const response = await fetch(MODEL_ENDPOINT, {
                method: "POST",
                headers: { Accept: "application/json", "Content-Type": "application/json" },
                credentials: "same-origin",
                body: JSON.stringify({ model: id }),
                signal: controller.signal,
            });

            let body = null;
            try {
                body = await response.json();
            } catch {
                body = null;
            }

            if (!response.ok) {
                // A non-2xx does not prove the change was not applied. A 502
                // (SetModelAsync returned false) or a rejected/aborted request
                // can still have reached the session, and even the API detail
                // says to check before retrying. Record the uncertainty and let
                // a status poll decide; never retry automatically.
                this.modelUnconfirmed = { target: id, status: response.status, at: Date.now() };
                this.setModelReceipt(
                    `Model change could not be confirmed (HTTP ${response.status}). ` +
                    "It may have applied; re-check the active model before retrying. " +
                    "No retry was sent automatically.",
                    "error",
                );
                this.refreshStatus();
                return;
            }

            const confirmed = this.extractConfirmedModel(body);
            if (confirmed) {
                this.modelGuard = { value: confirmed, expiresAt: Date.now() + MODEL_CONFIRM_GUARD_MS };
                this.authoritativeModel = confirmed;
                this.setField("model", confirmed);
                if (!this.isModelSelectFocused()) {
                    this.selectModelValue(confirmed);
                }
                this.setModelReceipt(`Runtime confirmed the active model: ${confirmed}.`, "ok");
            } else {
                // HTTP 200 is an accepted request receipt, not proof the model
                // changed. Wait for a fresh snapshot before reporting success.
                this.modelAwaiting = { target: id, at: Date.now() };
                this.setModelReceipt(
                    "Model change accepted; waiting for the runtime status to confirm the active model\u2026",
                    "pending",
                );
            }

            if (body && typeof body === "object" && ("state" in body || "models" in body)) {
                this.applyStatus(body);
            } else {
                await this.refreshStatus();
            }
        } catch (error) {
            if (this.modelTimedOut) {
                this.modelUnconfirmed = { target: id, status: "timeout", at: Date.now() };
                this.setModelReceipt(
                    "Model change timed out after 10s without confirmation. It may have applied; " +
                    "re-check the active model before retrying. No retry was sent automatically.",
                    "error",
                );
            } else if (error && error.name === "AbortError") {
                // Aborted by pagehide; the page is going away, so stay quiet.
            } else {
                const detail = error && error.message ? error.message : "network error";
                this.modelUnconfirmed = { target: id, status: "network", at: Date.now() };
                this.setModelReceipt(
                    `Model change failed: ${detail}. The request may or may not have reached the runtime; ` +
                    "re-check the active model before retrying. No retry was sent automatically.",
                    "error",
                );
            }
        } finally {
            clearTimeout(timeout);
            this.modelTimedOut = false;
            if (this.modelAbort === controller) {
                this.modelAbort = null;
            }
            this.modelChangeInFlight = false;
            this.updateModelDisabled();
            this.resolveUnconfirmedModel(this.authoritativeModel);
            if (!this.isModelSelectFocused()) {
                this.selectModelValue(this.authoritativeModel);
            }
        }
    }

    extractConfirmedModel(body) {
        if (body && typeof body === "object") {
            if (typeof body.model === "string" && body.model.trim() !== "") {
                return body.model.trim();
            }
            if (body.data && typeof body.data === "object" && typeof body.data.model === "string"
                && body.data.model.trim() !== "") {
                return body.data.model.trim();
            }
        }
        return "";
    }

    setModelReceipt(message, status) {
        const receipt = this.modelReceipt;
        if (!receipt) {
            return;
        }
        if (!message) {
            receipt.textContent = "";
            receipt.dataset.status = "";
            receipt.hidden = true;
            return;
        }
        receipt.textContent = message;
        receipt.dataset.status = status || "";
        receipt.hidden = false;
    }

    bindModelControl() {
        if (!this.modelSelect) {
            return;
        }
        this.modelSelect.addEventListener("change", () => this.handleModelChange());
        this.modelSelect.addEventListener("blur", () => this.handleModelBlur());
    }

    // ---- rendering helpers ----------------------------------------------

    setField(name, value) {
        this.root.querySelectorAll(`[data-field="${name}"]`).forEach((element) => {
            element.textContent = value;
        });
    }

    // State pills (the header state, and the runtime-state detail) expose their
    // normalized kind as data-state so CSS can color the pill without coupling
    // to text. The detail span is addressed by data-field like any other field.
    renderStatePills(kind) {
        this.root.querySelectorAll('[data-field="state"], [data-field="state-detail"]').forEach((pill) => {
            pill.dataset.state = kind;
        });
    }

    renderConnection(kind, force) {
        if (this.connectionKind === kind && !force) {
            return;
        }
        this.connectionKind = kind;
        const label = CONNECTION_LABELS[kind] || kind;
        const led = this.root.querySelector("[data-connection-led]");
        if (led) {
            led.dataset.connection = kind;
        }
        this.setField("connection", label);
        const surface = this.root.querySelector(".terminal-surface");
        if (surface) {
            surface.dataset.connection = kind;
        }
    }

    setOverlay(message) {
        if (!this.overlay) {
            return;
        }
        const text = this.root.querySelector('[data-field="overlay-text"]');
        if (text) {
            text.textContent = message;
        }
        this.overlay.hidden = !message;
    }

    setReceipt(message, status) {
        const receipt = this.root.querySelector('[data-field="receipt"]');
        if (!receipt) {
            return;
        }
        receipt.textContent = message;
        receipt.dataset.status = status || "";
    }

    updateButtons() {
        const attached = this.isAttached();
        const connecting = this.isAttaching();
        if (this.buttons.reconnect) {
            this.buttons.reconnect.disabled = !this.canAttach();
        }
        if (this.buttons.detach) {
            this.buttons.detach.disabled = !(attached || connecting);
        }
        if (this.buttons.clear) {
            this.buttons.clear.disabled = !this.term;
        }
        if (this.buttons.interrupt) {
            this.buttons.interrupt.disabled = !this.hostOwned || !this.isRuntimeControllable() || this.cancelInFlight;
        }
        this.updateModelDisabled();
    }

    // ---- event bindings --------------------------------------------------

    bindControls() {
        const action = (name, handler) => {
            if (this.buttons[name]) {
                this.buttons[name].addEventListener("click", handler);
            }
        };
        action("reconnect", () => this.reconnect());
        action("detach", () => this.detach());
        action("clear", () => this.clearView());
        action("interrupt", () => this.interrupt());
    }
}

function boot() {
    const root = document.querySelector("[data-portal]");
    if (!root || root.dataset.portalActive === "true") {
        return;
    }
    root.dataset.portalActive = "true";
    const portal = new TerminalPortal(root);
    portal.start();
}

if (typeof document !== "undefined") {
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", boot, { once: true });
    } else {
        boot();
    }
}

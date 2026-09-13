// AgentControl V2 terminal portal runtime.
//
// Loaded as an external ES module after the locally bundled @xterm/xterm and
// @xterm/addon-fit UMD builds. There is intentionally no SignalR / Blazor
// circuit here: the static server render is enhanced by
//   - polling GET /api/control (bounded to one request every 2 seconds), and
//   - attaching xterm.js to the same-origin WebSocket at /terminal.
//
// Wire protocol (JSON text frames):
//   client -> server  { "type": "input",  "data": "..." }
//                     { "type": "resize", "cols": <n>, "rows": <n> }
//   server -> client  { "type": "output", "data": "..." }
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
const TERMINAL_ENDPOINT = "/terminal";

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

function stateKind(rawState) {
    const value = String(rawState || "").toLowerCase();
    if (!value) {
        return "unknown";
    }
    if (/(fault|error|fail|crash|unhealthy)/.test(value)) {
        return "faulted";
    }
    if (/(load|start|init|sync|pending|provision|wait|connect)/.test(value)) {
        return "loading";
    }
    if (/(ready|run|active|healthy|ok|attached)/.test(value)) {
        return "ready";
    }
    if (/(stop|off|idle|detach|disconnect|disable|suspend|exit)/.test(value)) {
        return "idle";
    }
    return "unknown";
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
        this.resizeObserver = null;

        this.running = false;
        this.terminalReady = false;
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

        // Model dropdown state. The authoritative value comes from /api/control;
        // the select is only reconciled when the operator is not interacting and
        // no change request is pending.
        this.modelCatalog = [];
        this.authoritativeModel = "";
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
        this.pollOnce();
        this.updateButtons();
    }

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
        return this.isRuntimeEstablished() && this.terminalReady === true;
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
        if (!this.canAttach() || this.manualDetach || this.socket) {
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
        this.renderConnection("connecting", true);
        this.setOverlay("Attaching to session\u2026");

        const scheme = window.location.protocol === "https:" ? "wss" : "ws";
        let socket;
        try {
            socket = new WebSocket(`${scheme}://${window.location.host}${TERMINAL_ENDPOINT}`);
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
            if (this.term) {
                this.term.write(message.data);
            }
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
        if (!this.running) {
            return;
        }
        const controller = new AbortController();
        this.pollAbort = controller;
        try {
            const response = await fetch(STATUS_ENDPOINT, {
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
            if (this.running) {
                this.applyStatus(status);
            }
        } catch (error) {
            if (!error || error.name !== "AbortError") {
                this.setField("syncedAt", "status unavailable");
            }
        } finally {
            if (this.pollAbort === controller) {
                this.pollAbort = null;
            }
            if (this.running && !document.hidden) {
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

    applyStatus(status) {
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

        this.root.querySelectorAll('[data-field="state"]').forEach((pill) => {
            pill.dataset.state = kind;
        });

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
        if (text) {
            text.textContent = String(message);
        }
        if (banner) {
            banner.hidden = false;
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

    updateModelDisabled() {
        if (!this.modelSelect) {
            return;
        }
        const hasCatalog = Array.isArray(this.modelCatalog) && this.modelCatalog.length > 0;
        this.modelSelect.disabled = !this.modelSyncSupported || !this.isRuntimeReady() || this.modelChangeInFlight || !hasCatalog;
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
            this.buttons.interrupt.disabled = !this.isRuntimeControllable() || this.cancelInFlight;
        }
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

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot, { once: true });
} else {
    boot();
}

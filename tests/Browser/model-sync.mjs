// AgentControl V2 model-dropdown UI test against a LOCAL STUB ONLY.
//
// This suite never talks to the live container or the ACP runtime: a tiny
// loopback HTTP server serves a copy of the portal markup, the real
// wwwroot/js/terminal.js and the real wwwroot/css/portal.css, and stubs
//   GET  /api/control
//   POST /api/control/model
// The live end-to-end contract (a real parent applying the model) is covered by
// the separate live portal smoke suite; this file exercises only browser UI
// behaviour:
//   1. optgroups render per provider, option values are model ids, the
//      authoritative model is selected in the header and the dropdown
//   2. a poll cannot overwrite the dropdown while it is focused; blur catches
//      up to the authoritative model
//   3. a successful POST is reported as a runtime confirmation, not optimistic
//   4. a rejected POST surfaces a visible failure and reverts on blur
//   5. a missing catalog keeps the last known options (enabled); an explicit
//      empty catalog keeps the current option but disables the control
//   6. HTTP 200 without a returned model is not reported as success
//   7. pagehide aborts an in-flight request quietly
//   8. a request that never settles fails loudly after the 10s bound
//   9. a 502 (SetModelAsync returned false) is reported as unconfirmed, never
//      as "the active model is unchanged", and never selects the request
//      optimistically or retries automatically
//  10. a degraded established session stays controllable (terminal + cancel)
//      and keeps its error banner, while faulted/disabled do not; a fake
//      canControl:true on a faulted status must not enable the controls
//  11. modelSyncSupported:false keeps the model selector disabled
//  12. selection/ownership gates: an unresolved or empty-id selection never
//      polls or attaches, a remote selection only attaches when its
//      availability/runtime is ready, and an interrupt on unresolved ownership
//      asks for an employee instead of blaming remote cancellation
//  13. delayed host/remote poll responses cannot overwrite a newer selection
//  14. a remote -> host selection clears stale remote telemetry to
//      Synchronizing / em-dash before the first host poll
//  15. a bfcache pagehide/pageshow pair resumes polling, refreshes telemetry
//      and reopens the ready socket without a stale pre-hide snapshot
//
// Usage: node tests/Browser/model-sync.mjs
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const TERMINAL_JS = fileURLToPath(
  new URL('../../src/HVO.AgentControl/wwwroot/js/terminal.js', import.meta.url),
);
const PORTAL_CSS = fileURLToPath(
  new URL('../../src/HVO.AgentControl/wwwroot/css/portal.css', import.meta.url),
);
// Optional explicit browser; when unset, Chromium bundled by the local
// `playwright` dependency is used (portable across machines and CI).
const CHROME = process.env.CHROME_PATH || undefined;

const CATALOG = [
  { id: 'openai/gpt-5', name: 'GPT-5', provider: 'OpenAI' },
  { id: 'openai/gpt-5-mini', name: 'GPT-5 mini', provider: 'OpenAI' },
  { id: 'anthropic/claude', name: 'Claude', provider: 'Anthropic' },
];

const state = {
  control: {
    state: 'ready',
    organizationName: 'Stub harness',
    sessionId: 'stub-session',
    model: 'openai/gpt-5',
    transport: 'ACP',
    terminalReady: false,
    sessionState: 'idle',
    models: CATALOG,
  },
  modelPlan: { status: 200, mode: 'echo', delayMs: 0, body: null },
  modelRequests: [],
  controlDelayMs: 0,
  employees: {},
  controlRequests: 0,
  employeeRequests: [],
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const readBody = (req) =>
  new Promise((resolve) => {
    let text = '';
    req.on('data', (chunk) => {
      text += chunk;
    });
    req.on('end', () => resolve(text));
  });

const HARNESS = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>model-sync stub</title>
<link rel="stylesheet" href="/css/portal.css">
</head>
<body>
<div class="portal" data-portal data-runtime-state="unknown">
  <header class="app-bar">
    <span class="state-pill" data-field="state" data-state="unknown">Synchronizing</span>
    <code class="mono" data-field="model">&mdash;</code>
  </header>
  <p class="error-banner" data-field="error-banner" role="alert" hidden>
    <span class="error-text" data-field="error"></span>
  </p>
  <main class="control-deck">
    <section class="telemetry">
      <dl class="telemetry-grid">
        <div class="telemetry-row"><dt>Runtime state</dt><dd data-field="state-detail">Synchronizing</dd></div>
        <div class="telemetry-row"><dt>Session</dt><dd><code data-field="sessionId">&mdash;</code></dd></div>
        <div class="telemetry-row"><dt>Transport</dt><dd><code data-field="transport">ACP</code></dd></div>
        <div class="telemetry-row telemetry-row--model">
          <dt><label for="model-select">Model</label></dt>
          <dd class="model-control">
            <select id="model-select" class="model-select" data-model-select disabled>
              <option value="" disabled selected>Synchronizing&hellip;</option>
            </select>
            <p class="model-receipt" data-model-receipt role="status" aria-live="polite" hidden></p>
          </dd>
        </div>
        <div class="telemetry-row"><dt>Last sync</dt><dd><time data-field="syncedAt">&mdash;</time></dd></div>
      </dl>
      <div class="controls">
        <button type="button" data-action="reconnect" disabled>Reconnect</button>
        <button type="button" data-action="detach" disabled>Detach</button>
        <button type="button" data-action="clear">Clear</button>
        <button type="button" data-action="interrupt" disabled>Interrupt</button>
      </div>
      <p class="receipt" data-field="receipt" role="status"></p>
    </section>
    <section class="terminal-surface">
      <span class="terminal-lamp" data-connection-led></span>
      <span data-field="connection">Idle</span>
      <div class="terminal-stage">
        <div id="terminal" class="terminal-mount" data-terminal></div>
        <div class="terminal-overlay" data-field="terminal-overlay" hidden>
          <span class="overlay-text" data-field="overlay-text"></span>
        </div>
      </div>
    </section>
  </main>
</div>
<script>
  window.__terminalEvents = [];
  window.__errorScrolls = 0;
  Element.prototype.scrollIntoView = function () { window.__errorScrolls += 1; };
  window.Terminal = class {
    constructor() { this.cols = 80; this.rows = 24; window.__terminalEvents.push('terminal-created'); }
    loadAddon() {} open() { window.__terminalEvents.push('terminal-opened'); } onData() {} onResize() {} write() {} clear() {} focus() {}
    resize(cols, rows) { this.cols = cols; this.rows = rows; }
  };
  window.FitAddon = { FitAddon: class { proposeDimensions() { return { cols: 80, rows: 24 }; } } };
  window.WebSocket = class {
    static OPEN = 1; static CONNECTING = 0;
    constructor(url) { this.url = url; this.readyState = 0; window.__terminalEvents.push('socket-created:' + url); setTimeout(() => { this.readyState = 1; window.__terminalEvents.push('socket-opened:' + url); this.onopen?.(); }, 0); }
    close(code, reason) { this.readyState = 3; window.__terminalEvents.push('socket-closed:' + code + ':' + reason); }
    send() {}
  };
  const parameters = new URLSearchParams(location.search);
  if (parameters.has('initial-host')) {
    document.querySelector('[data-portal]').agentControlSelectedEmployee = {
      id: 'host-stub', availability: 'ready',
      runtime: { hostOwned: true },
      terminal: { available: true, url: '/terminal?employeeId=host-stub' },
    };
  } else if (parameters.has('initial-employee')) {
    document.querySelector('[data-portal]').agentControlSelectedEmployee = {
      id: 'emp-fast', availability: 'ready',
      runtime: { hostOwned: false, controlStatus: 'ready', nativeSessionId: 'fast-session', controlModel: 'fast/model' },
      terminal: { available: true, url: '/terminal?employeeId=emp-fast' },
    };
  }
</script>
<script type="module" src="/js/terminal.js"></script>
</body>
</html>`;

function applyStub(patch) {
  if (patch.control) Object.assign(state.control, patch.control);
  if (patch.modelPlan) Object.assign(state.modelPlan, patch.modelPlan);
  if ('controlDelayMs' in patch) state.controlDelayMs = Number(patch.controlDelayMs) || 0;
  if (patch.employees) state.employees = { ...state.employees, ...patch.employees };
  if (patch.resetRequests) state.modelRequests.length = 0;
  if (patch.resetPollRequests) {
    state.controlRequests = 0;
    state.employeeRequests.length = 0;
  }
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const send = (status, body, type) => {
    res.statusCode = status;
    res.setHeader('Content-Type', type || 'application/json');
    res.end(type ? body : JSON.stringify(body));
  };

  if (url.pathname === '/') return send(200, HARNESS, 'text/html; charset=utf-8');
  if (url.pathname === '/js/terminal.js') return send(200, readFileSync(TERMINAL_JS, 'utf8'), 'text/javascript; charset=utf-8');
  if (url.pathname === '/css/portal.css') return send(200, readFileSync(PORTAL_CSS, 'utf8'), 'text/css; charset=utf-8');
  if (url.pathname === '/api/control' && req.method === 'GET') {
    state.controlRequests += 1;
    const snapshot = structuredClone(state.control);
    const delayMs = state.controlDelayMs;
    if (delayMs > 0) await sleep(delayMs);
    return send(200, snapshot);
  }
  if (url.pathname.startsWith('/api/employees/') && req.method === 'GET') {
    const id = decodeURIComponent(url.pathname.slice('/api/employees/'.length));
    state.employeeRequests.push(id);
    const employee = state.employees[id];
    if (!employee) return send(404, { error: 'employee not found' });
    const snapshot = structuredClone(employee);
    const delayMs = Number(snapshot.__delayMs || 0);
    delete snapshot.__delayMs;
    if (delayMs > 0) await sleep(delayMs);
    return send(200, snapshot);
  }

  if (url.pathname === '/__stub' && req.method === 'POST') {
    applyStub(JSON.parse((await readBody(req)) || '{}'));
    return send(200, { ok: true });
  }

  if (url.pathname === '/api/control/model' && req.method === 'POST') {
    const payload = JSON.parse((await readBody(req)) || '{}');
    state.modelRequests.push(payload);
    const plan = state.modelPlan;
    if (plan.delayMs > 0) await sleep(plan.delayMs);
    if (plan.status >= 400) return send(plan.status, { error: 'rejected' });
    const requested = String(payload.model || '');
    if (plan.mode === 'empty') return send(200, {});
    if (plan.mode === 'fixed') {
      const actual = plan.body?.model || requested;
      state.control.model = actual;
      return send(200, { model: actual });
    }
    state.control.model = requested;
    return send(200, { model: requested });
  }

  return send(404, { error: 'not found' });
});

await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;

const results = [];
const record = (name, passed, detail = {}) => {
  results.push({ name, passed: !!passed, detail });
  console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${Object.keys(detail).length ? '  :: ' + JSON.stringify(detail) : ''}`);
};

const browser = await chromium.launch({ ...(CHROME ? { executablePath: CHROME } : {}), headless: true, args: ['--no-sandbox'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();
const pageErrors = [];
page.on('pageerror', (error) => pageErrors.push(String(error && error.message ? error.message : error)));

const valueOf = (selector) => page.$eval(selector, (el) => el.value);
const headerModel = () => page.$eval('[data-field="model"]', (el) => el.textContent.trim());
const disabled = () => page.$eval('[data-model-select]', (el) => el.disabled);
const receipt = () =>
  page.$eval('[data-model-receipt]', (el) => ({
    text: el.textContent.trim(),
    status: el.dataset.status || '',
    hidden: el.hidden,
  }));
const buttonDisabled = (action) => page.$eval(`[data-action="${action}"]`, (el) => el.disabled);
const errorBanner = () =>
  page.$eval('[data-field="error-banner"]', (el) => ({
    hidden: el.hidden,
    text: (el.querySelector('[data-field="error"]') || {}).textContent?.trim() || '',
  }));

async function waitFor(predicate, label, timeoutMs = 6000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (await predicate()) return true;
    if (Date.now() >= deadline) throw new Error(`timeout waiting for ${label}`);
    await sleep(100);
  }
}

try {
  // ---- selection/attachment safety gates --------------------------------
  await fetch(`${base}/__stub`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ controlDelayMs: 1500, resetPollRequests: true }),
  });
  const unselectedPage = await context.newPage();
  await unselectedPage.goto(base, { waitUntil: 'domcontentloaded' });
  await sleep(250);
  const unselected = await unselectedPage.evaluate(() => ({
    hostOwned: document.querySelector('[data-portal]').dataset.hostOwned || '',
    state: document.querySelector('[data-field="state-detail"]')?.textContent.trim(),
    session: document.querySelector('[data-field="sessionId"]')?.textContent.trim(),
    errorHidden: document.querySelector('[data-field="error-banner"]')?.hidden,
    events: window.__terminalEvents,
  }));
  record('initial ownership stays unresolved and does not poll or apply host telemetry before selection',
    state.controlRequests === 0 && state.employeeRequests.length === 0
      && unselected.hostOwned === '' && unselected.state === 'Synchronizing'
      && unselected.session === '—' && unselected.errorHidden
      && !unselected.events.some((event) => event.startsWith('socket-created:')),
    { controlRequests: state.controlRequests, employeeRequests: state.employeeRequests, unselected });

  // Programmatic interrupt on a disabled control (dispatchEvent bypasses the
  // disabled UI gate) must report the selection problem, never imply that a
  // remote employee was selected and therefore lacked cancellation.
  const readInterruptReceipt = (target) => target.evaluate(() => {
    document.querySelector('[data-action="interrupt"]').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    return document.querySelector('[data-field="receipt"]').textContent.trim();
  });
  const unresolvedReceipt = await readInterruptReceipt(unselectedPage);
  record('interrupt with unresolved ownership asks for an employee, not a remote excuse',
    unresolvedReceipt.includes('select an employee') && !unresolvedReceipt.includes('remote turn cancellation'),
    { unresolvedReceipt });

  await unselectedPage.evaluate(() => document.querySelector('[data-portal]').dispatchEvent(new CustomEvent(
    'agentcontrol:employee-selected',
    { detail: { id: '   ', runtime: { hostOwned: false }, terminal: { available: true, url: '/terminal?employeeId=' } } },
  )));
  await sleep(100);
  const unresolved = await unselectedPage.evaluate(() => ({
    state: document.querySelector('[data-field="state-detail"]')?.textContent.trim(),
    connection: document.querySelector('[data-field="connection"]')?.textContent.trim(),
  }));
  record('an empty remote employee id stays unresolved and performs no employee fetch',
    state.employeeRequests.length === 0 && unresolved.state === 'Select an employee'
      && unresolved.connection === 'Unavailable',
    { employeeRequests: state.employeeRequests, unresolved });
  const emptyRemoteReceipt = await readInterruptReceipt(unselectedPage);
  record('an explicitly empty remote employee id keeps the interrupt receipt explicit about selection',
    emptyRemoteReceipt.includes('select an employee') && !emptyRemoteReceipt.includes('remote turn cancellation'),
    { emptyRemoteReceipt });

  const remoteCases = [
    { name: 'provisioning availability', availability: 'provisioning', controlStatus: 'authenticated', expectedSockets: 0 },
    { name: 'unknown availability', availability: 'future-state', controlStatus: 'authenticated', expectedSockets: 0 },
    { name: 'non-ready runtime status', availability: 'ready', controlStatus: 'connecting', expectedSockets: 0 },
    { name: 'ready authenticated runtime', availability: 'ready', controlStatus: 'authenticated', expectedSockets: 1 },
  ];
  for (const [index, item] of remoteCases.entries()) {
    await unselectedPage.evaluate(({ index: caseIndex, item: current }) => {
      window.__terminalEvents.length = 0;
      document.querySelector('[data-portal]').dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: {
        id: `emp-gate-${caseIndex}`,
        availability: current.availability,
        runtime: { hostOwned: false, controlStatus: current.controlStatus, nativeSessionId: `session-${caseIndex}` },
        terminal: { available: true, url: `/terminal?employeeId=emp-gate-${caseIndex}` },
      } }));
    }, { index, item });
    await sleep(100);
    const events = await unselectedPage.evaluate(() => window.__terminalEvents);
    const sockets = events.filter((event) => event.startsWith('socket-created:')).length;
    record(`remote ${item.name} with terminal.available=true opens ${item.expectedSockets} sockets`,
      sockets === item.expectedSockets, { events });
  }
  await unselectedPage.evaluate(() => {
    window.__terminalEvents.length = 0;
    document.querySelector('[data-portal]').dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: {
      id: '', runtime: { hostOwned: false }, terminal: { available: false, url: null },
    } }));
  });
  await sleep(50);
  const invalidSelection = await unselectedPage.evaluate(() => ({
    events: window.__terminalEvents,
    connection: document.querySelector('[data-field="connection"]')?.textContent.trim(),
  }));
  record('invalid selection closes an attached remote terminal and renders unavailable',
    invalidSelection.events.some((event) => event.includes('socket-closed:1000:employee-terminal-target-changed'))
      && invalidSelection.connection === 'Unavailable', invalidSelection);
  await unselectedPage.close();

  await fetch(`${base}/__stub`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ controlDelayMs: 0, resetPollRequests: true }),
  });
  await page.goto(`${base}/?initial-host=1`, { waitUntil: 'domcontentloaded' });

  // ---- 1. catalog render ------------------------------------------------
  await waitFor(async () => (await valueOf('#model-select')) === 'openai/gpt-5', 'initial catalog selection');
  const groups = await page.$$eval('[data-model-select] optgroup', (nodes) =>
    nodes.map((group) => ({ label: group.label, values: Array.from(group.querySelectorAll('option')).map((o) => o.value) })));
  const values = await page.$$eval('[data-model-select] option', (nodes) => nodes.map((o) => o.value));
  record('optgroups render per provider with model ids as option values',
    JSON.stringify(groups) === JSON.stringify([
      { label: 'OpenAI', values: ['openai/gpt-5', 'openai/gpt-5-mini'] },
      { label: 'Anthropic', values: ['anthropic/claude'] },
    ]) && JSON.stringify(values) === JSON.stringify(CATALOG.map((m) => m.id)),
    { groups, values });
  record('header and dropdown show the authoritative model',
    (await headerModel()) === 'openai/gpt-5' && (await valueOf('#model-select')) === 'openai/gpt-5',
    { header: await headerModel(), select: await valueOf('#model-select') });
  record('control is enabled when the runtime is ready and a catalog exists', (await disabled()) === false);

  // ---- 2. poll must not overwrite a focused control ---------------------
  await page.focus('[data-model-select]');
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ control: { model: 'anthropic/claude' } }) });
  await waitFor(async () => (await headerModel()) === 'anthropic/claude', 'poll updates the header');
  record('poll updates the header but leaves a focused dropdown untouched',
    (await valueOf('#model-select')) === 'openai/gpt-5' && (await headerModel()) === 'anthropic/claude',
    { header: await headerModel(), select: await valueOf('#model-select') });
  await page.$eval('[data-model-select]', (el) => el.blur());
  await waitFor(async () => (await valueOf('#model-select')) === 'anthropic/claude', 'blur catches up');
  record('blur catches the dropdown up to the authoritative model',
    (await valueOf('#model-select')) === 'anthropic/claude', { select: await valueOf('#model-select') });

  // ---- 3. confirmed change ---------------------------------------------
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelPlan: { status: 200, mode: 'echo', delayMs: 500, body: null }, resetRequests: true }) });
  await page.selectOption('#model-select', 'openai/gpt-5');
  await waitFor(async () => (await receipt()).status === 'pending', 'pending receipt');
  record('a change is reported as pending (not optimistic success) while in flight',
    (await receipt()).status === 'pending' && (await disabled()) === true,
    { receipt: await receipt(), disabled: await disabled() });
  await waitFor(async () => (await receipt()).status === 'ok', 'confirmed receipt');
  record('a 200 confirmation updates the header and dropdown and says confirmed',
    (await receipt()).text.includes('confirmed') && (await headerModel()) === 'openai/gpt-5' && (await valueOf('#model-select')) === 'openai/gpt-5',
    { receipt: await receipt(), header: await headerModel() });
  const firstRequest = state.modelRequests[0];
  record('POST /api/control/model sends { model: id }',
    firstRequest?.model === 'openai/gpt-5' && Object.keys(firstRequest || {}).length === 1,
    { request: firstRequest });

  // ---- 4. rejected change ----------------------------------------------
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelPlan: { status: 500, mode: 'echo', delayMs: 0, body: null }, resetRequests: true }) });
  await page.selectOption('#model-select', 'anthropic/claude');
  await waitFor(async () => (await receipt()).status === 'error', 'failed receipt');
  record('a rejected change surfaces a visible failure and does not claim success or "unchanged"',
    (await receipt()).status === 'error'
      && (await receipt()).text.includes('could not be confirmed')
      && (await receipt()).text.includes('may have applied')
      && !(await receipt()).text.includes('unchanged')
      && (await headerModel()) === 'openai/gpt-5',
    { receipt: await receipt(), header: await headerModel() });
  await page.$eval('[data-model-select]', (el) => el.blur());
  await waitFor(async () => (await valueOf('#model-select')) === 'openai/gpt-5', 'revert after failure');
  record('blur after a rejected change reverts the dropdown to the authoritative model',
    (await valueOf('#model-select')) === 'openai/gpt-5' && (await headerModel()) === 'openai/gpt-5',
    { select: await valueOf('#model-select'), header: await headerModel() });

  // ---- 5. missing catalog keeps last known options ----------------------
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ control: { models: null } }) });
  await sleep(2600);
  record('a missing catalog keeps the last known options enabled',
    (await disabled()) === false && (await valueOf('#model-select')) === 'openai/gpt-5',
    { disabled: await disabled(), select: await valueOf('#model-select') });

  // ---- 6. explicit empty catalog keeps option but disables --------------
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ control: { models: [] } }) });
  await waitFor(async () => (await disabled()) === true, 'empty catalog disables');
  record('an empty catalog keeps the current option but disables the control',
    (await disabled()) === true && (await valueOf('#model-select')) === 'openai/gpt-5',
    { disabled: await disabled(), select: await valueOf('#model-select') });

  // ---- 7. HTTP 200 without a model is not success -----------------------
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ control: { models: CATALOG, model: 'openai/gpt-5' }, modelPlan: { status: 200, mode: 'empty', delayMs: 0, body: null }, resetRequests: true }) });
  await waitFor(async () => (await disabled()) === false, 'catalog restored');
  await page.selectOption('#model-select', 'anthropic/claude');
  await waitFor(async () => (await receipt()).status === 'pending', 'unconfirmed receipt');
  await sleep(400);
  record('an unconfirmed 200 stays pending and never reports success',
    (await receipt()).status === 'pending' && (await headerModel()) === 'openai/gpt-5',
    { receipt: await receipt(), header: await headerModel() });

  // ---- 8. pagehide aborts in flight quietly -----------------------------
  await page.reload({ waitUntil: 'domcontentloaded' });
  await waitFor(async () => (await valueOf('#model-select')) === 'openai/gpt-5', 'reload catalog');
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelPlan: { status: 200, mode: 'echo', delayMs: 4000, body: null }, resetRequests: true }) });
  await page.selectOption('#model-select', 'anthropic/claude');
  await waitFor(async () => (await receipt()).status === 'pending', 'pending before pagehide');
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide')));
  await sleep(300);
  record('pagehide aborts the in-flight request without a failure receipt',
    (await receipt()).status !== 'error',
    { receipt: await receipt() });

  // ---- 9. timeout bound -------------------------------------------------
  // The stub cannot observe the aborted socket, so let the orphaned handler
  // from step 8 finish and reset the authoritative model before reloading.
  await sleep(4200);
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ control: { model: 'openai/gpt-5' } }) });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await waitFor(async () => (await valueOf('#model-select')) === 'openai/gpt-5', 'reload catalog before timeout');
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelPlan: { status: 200, mode: 'echo', delayMs: 12000, body: null }, resetRequests: true }) });
  await page.selectOption('#model-select', 'anthropic/claude');
  await waitFor(async () => (await receipt()).status === 'error', 'timeout receipt', 12000);
  record('a request that never settles fails loudly after the ~10s bound',
    (await receipt()).text.includes('timed out') && (await headerModel()) === 'openai/gpt-5',
    { receipt: await receipt(), header: await headerModel() });

  // ---- 10. 502 is unconfirmed, never "unchanged" ------------------------
  // Step 9 left an orphaned 12s stub response in flight; let it settle, then
  // reset the authoritative model and reload so this section starts clean.
  await sleep(2600);
  await fetch(`${base}/__stub`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      control: {
        model: 'openai/gpt-5',
        state: 'ready',
        sessionId: 'stub-session',
        terminalReady: false,
        error: null,
        models: CATALOG,
      },
      modelPlan: { status: 200, mode: 'echo', delayMs: 0, body: null },
      resetRequests: true,
    }),
  });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await waitFor(async () => (await valueOf('#model-select')) === 'openai/gpt-5', 'reload before 502');

  await fetch(`${base}/__stub`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ modelPlan: { status: 502, mode: 'echo', delayMs: 0, body: null }, resetRequests: true }),
  });
  await page.selectOption('#model-select', 'anthropic/claude');
  await waitFor(async () => (await receipt()).status === 'error', '502 uncertainty receipt');
  const r502 = await receipt();
  record('a 502 reports uncertainty and never claims the active model is unchanged',
    r502.status === 'error'
      && r502.text.includes('could not be confirmed')
      && r502.text.includes('may have applied')
      && r502.text.includes('re-check')
      && r502.text.includes('No retry was sent automatically')
      && !r502.text.includes('unchanged'),
    { receipt: r502 });
  await page.$eval('[data-model-select]', (el) => el.blur());
  await waitFor(async () => (await valueOf('#model-select')) === 'openai/gpt-5', '502 reverts selection');
  record('a 502 does not optimistically select the requested model or auto-retry',
    (await headerModel()) === 'openai/gpt-5'
      && (await valueOf('#model-select')) === 'openai/gpt-5'
      && state.modelRequests.length === 1,
    { header: await headerModel(), select: await valueOf('#model-select'), requests: state.modelRequests.length });

  // ---- 11. degraded is controllable; faulted/disabled are not -----------
  // Faulted: a (fake) canControl:true must not enable control because the
  // runtime state is not ready/degraded.
  await fetch(`${base}/__stub`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      control: {
        state: 'faulted',
        sessionId: 'stub-session',
        terminalReady: true,
        canControl: true,
        error: 'synthetic fault',
      },
    }),
  });
  await waitFor(async () => (await buttonDisabled('interrupt')) === true, 'faulted locks interrupt');
  record('a faulted runtime cannot control even when the wire claims canControl',
    (await buttonDisabled('interrupt')) === true && (await buttonDisabled('reconnect')) === true,
    { interrupt: await buttonDisabled('interrupt'), reconnect: await buttonDisabled('reconnect') });

  // Degraded: an established session must stay recoverable.
  await fetch(`${base}/__stub`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      control: {
        state: 'degraded',
        sessionId: 'stub-session',
        terminalReady: true,
        canControl: true,
        error: 'synthetic degraded bootstrap',
      },
    }),
  });
  await waitFor(async () => (await buttonDisabled('interrupt')) === false, 'degraded allows interrupt');
  await page.evaluate(() => document.querySelector('[data-portal]').dispatchEvent(new CustomEvent(
    'agentcontrol:employee-selected',
    { detail: { id: 'emp-degraded', runtime: { hostOwned: true }, terminal: { available: true, url: '/terminal?employeeId=emp-degraded' } } },
  )));
  record('a degraded established session can control the terminal and cancel',
    (await buttonDisabled('interrupt')) === false && (await buttonDisabled('reconnect')) === false,
    { interrupt: await buttonDisabled('interrupt'), reconnect: await buttonDisabled('reconnect') });
  record('a degraded status keeps its error banner visible',
    (await errorBanner()).hidden === false && (await errorBanner()).text.includes('synthetic degraded'),
    { banner: await errorBanner() });

  // Disabled: no session control at all.
  await fetch(`${base}/__stub`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      control: {
        state: 'disabled',
        sessionId: 'stub-session',
        terminalReady: true,
        canControl: false,
        error: null,
      },
    }),
  });
  await waitFor(async () => (await buttonDisabled('interrupt')) === true, 'disabled locks interrupt');
  record('a disabled runtime cannot control',
    (await buttonDisabled('interrupt')) === true && (await buttonDisabled('reconnect')) === true,
    { interrupt: await buttonDisabled('interrupt'), reconnect: await buttonDisabled('reconnect') });

  // ---- 12. modelSyncSupported still disables the selector ----------------
  await fetch(`${base}/__stub`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      control: {
        state: 'ready',
        sessionId: 'stub-session',
        terminalReady: false,
        canControl: true,
        modelSyncSupported: false,
        models: CATALOG,
        error: null,
      },
    }),
  });
  await waitFor(async () => (await disabled()) === true, 'modelSyncSupported false disables');
  record('modelSyncSupported false keeps the model selector disabled',
    (await disabled()) === true, { disabled: await disabled() });

  // ---- 13. remote employee keeps viewer controls but not host controls ----
  await fetch(`${base}/__stub`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ control: { state: 'ready', sessionId: 'stub-session', terminalReady: true, canControl: true, modelSyncSupported: true, models: CATALOG } }),
  });
  await sleep(2200);
  await page.evaluate(() => document.querySelector('[data-portal]').dispatchEvent(new CustomEvent(
    'agentcontrol:employee-selected',
    { detail: { id: 'emp-remote', runtime: { hostOwned: false, controlStatus: 'ready', nativeSessionId: 'remote-session', controlModel: 'remote/model' }, terminal: { available: true, url: '/terminal?employeeId=emp-remote' } } },
  )));
  record('remote employee disables host model and interrupt while retaining terminal reconnect',
    (await disabled()) === true && (await buttonDisabled('interrupt')) === true && (await buttonDisabled('reconnect')) === false,
    { model: await disabled(), interrupt: await buttonDisabled('interrupt'), reconnect: await buttonDisabled('reconnect') });
  record('remote employee telemetry is not overwritten by host status polling',
    (await headerModel()) === 'remote/model' && (await page.locator('[data-field="sessionId"]').innerText()) === 'remote-session',
    { model: await headerModel(), session: await page.locator('[data-field="sessionId"]').innerText() });

  // ---- 14. employee terminal target changes tear down immediately --------
  await fetch(`${base}/__stub`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ control: { state: 'ready', sessionId: 'stub-session', terminalReady: true, canControl: true }, controlDelayMs: 0 }),
  });
  await sleep(2200);
  await page.evaluate(() => {
    const events = [];
    window.__socketEvents = events;
    class Socket {
      static OPEN = 1; static CONNECTING = 0;
      constructor(url) { this.url = url; this.readyState = 1; events.push(`open:${url}`); setTimeout(() => this.onopen?.(), 0); }
      close(code, reason) { this.readyState = 3; events.push(`close:${code}:${reason}`); }
      send() {}
    }
    window.WebSocket = Socket;
    const root = document.querySelector('[data-portal]');
    root.dispatchEvent(new CustomEvent('agentcontrol:employee-selected', {
      detail: { id: 'emp-one', runtime: { hostOwned: true }, terminal: { available: true, url: '/terminal?employeeId=emp-one' } },
    }));
  });
  // Let the host poll establish readiness for the new target before attaching.
  await waitFor(() => page.evaluate(() => document.querySelector('[data-portal]').dataset.runtimeState === 'ready'), 'emp-one host ready');
  await page.evaluate(() => document.querySelector('[data-action="reconnect"]').click());
  await sleep(50);
  // Same employee, new terminal URL: the old socket must close immediately and
  // must not reattach until a poll confirms readiness again.
  await page.evaluate(() => document.querySelector('[data-portal]').dispatchEvent(new CustomEvent(
    'agentcontrol:employee-selected',
    { detail: { id: 'emp-one', runtime: { hostOwned: true }, terminal: { available: true, url: '/terminal?employeeId=emp-one&revision=2' } } },
  )));
  const afterUrlChange = await page.evaluate(() => window.__socketEvents.slice());
  await waitFor(() => page.evaluate(() => document.querySelector('[data-portal]').dataset.runtimeState === 'ready'), 'emp-one revision 2 ready');
  // Terminal revocation closes the (poll-driven) reattached socket immediately.
  await page.evaluate(() => document.querySelector('[data-portal]').dispatchEvent(new CustomEvent(
    'agentcontrol:employee-selected',
    { detail: { id: 'emp-one', runtime: { hostOwned: true }, terminal: { available: false, url: null } } },
  )));
  await sleep(50);
  const terminalEvents = await page.evaluate(() => window.__socketEvents.slice());
  record('same employee URL change or terminal revocation closes the old socket immediately',
    afterUrlChange.filter((event) => event === 'close:1000:employee-terminal-target-changed').length === 1
      && terminalEvents.filter((event) => event === 'close:1000:employee-terminal-target-changed').length === 2,
    { afterUrlChange, terminalEvents });

  // ---- 15. retained fast selection waits for terminal construction --------
  const orderingPage = await context.newPage();
  await orderingPage.goto(`${base}/?initial-employee=1`, { waitUntil: 'domcontentloaded' });
  await waitFor(async () => (await orderingPage.evaluate(() => window.__terminalEvents.includes('socket-opened:ws://127.0.0.1:' + location.port + '/terminal?employeeId=emp-fast'))), 'fast retained employee socket');
  const ordering = await orderingPage.evaluate(() => ({
    events: window.__terminalEvents,
    connection: document.querySelector('[data-field="connection"]')?.textContent,
  }));
  const terminalCreatedAt = ordering.events.indexOf('terminal-created');
  const terminalOpenedAt = ordering.events.indexOf('terminal-opened');
  const socketCreatedAt = ordering.events.findIndex((event) => event.startsWith('socket-created:'));
  record('retained fast employee selection creates and opens xterm before opening its socket',
    terminalCreatedAt >= 0 && terminalOpenedAt > terminalCreatedAt && socketCreatedAt > terminalOpenedAt
      && ordering.connection === 'Attached', ordering);
  await orderingPage.close();

  // ---- 16. poll responses are bound to their request target ----------------
  let releaseHostPoll;
  let hostPollStartedResolve;
  const hostPollStarted = new Promise((resolve) => { hostPollStartedResolve = resolve; });
  await page.route('**/api/control', async (route) => {
    const response = await route.fetch();
    hostPollStartedResolve();
    await new Promise((resolve) => { releaseHostPoll = resolve; });
    await route.fulfill({ response, json: {
      ...state.control,
      state: 'faulted', sessionId: 'stale-host-session', model: 'stale/host-model',
      terminalReady: false, canControl: false, error: 'stale host error',
    } });
  });
  const hostRaceNavigation = page.goto(`${base}/?initial-host=1`, { waitUntil: 'domcontentloaded' });
  await hostPollStarted;
  const remoteRace = {
    id: 'emp-race-remote', availability: 'ready',
    runtime: { hostOwned: false, controlStatus: 'ready', nativeSessionId: 'remote-race-session', controlModel: 'remote/race-model', sanitizedError: 'safe remote warning' },
    terminal: { available: true, url: '/terminal?employeeId=emp-race-remote' },
  };
  await page.evaluate((employee) => {
    const root = document.querySelector('[data-portal]');
    root.dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: employee }));
    window.__terminalEvents.length = 0;
  }, remoteRace);
  await waitFor(async () => (await page.locator('[data-field="sessionId"]').innerText()) === 'remote-race-session', 'remote selection before stale host release');
  releaseHostPoll();
  await hostRaceNavigation;
  await sleep(250);
  const hostRace = await page.evaluate(() => ({
    session: document.querySelector('[data-field="sessionId"]')?.textContent,
    model: document.querySelector('[data-field="model"]')?.textContent,
    state: document.querySelector('[data-field="state-detail"]')?.textContent,
    error: document.querySelector('[data-field="error"]')?.textContent,
    events: window.__terminalEvents,
  }));
  record('a delayed host poll cannot overwrite a newly selected remote employee or close its socket',
    hostRace.session === 'remote-race-session' && hostRace.model === 'remote/race-model'
      && hostRace.state === 'ready' && hostRace.error === 'safe remote warning'
      && !hostRace.events.some((event) => event.startsWith('socket-closed:')), hostRace);
  await page.unroute('**/api/control');

  const delayedRemote = {
    id: 'emp-race-delayed', availability: 'quarantined', __delayMs: 0,
    runtime: { hostOwned: false, controlStatus: 'authenticated', nativeSessionId: 'stale-remote-session', controlModel: 'stale/remote-model', sanitizedError: 'stale remote error' },
    terminal: { available: false, url: null },
  };
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ employees: { 'emp-race-delayed': delayedRemote }, controlDelayMs: 0, control: {
    state: 'ready', sessionId: 'fresh-host-session', model: 'fresh/host-model', terminalReady: true,
    canControl: true, error: null, modelSyncSupported: true, models: CATALOG,
  } }) });
  let releaseRemotePoll;
  let remotePollStartedResolve;
  const remotePollStarted = new Promise((resolve) => { remotePollStartedResolve = resolve; });
  await page.route('**/api/employees/emp-race-delayed', async (route) => {
    const response = await route.fetch();
    remotePollStartedResolve();
    await new Promise((resolve) => { releaseRemotePoll = resolve; });
    await route.fulfill({ response });
  });
  await page.evaluate((employee) => {
    const root = document.querySelector('[data-portal]');
    root.dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: employee }));
  }, {
    ...delayedRemote,
    availability: 'ready',
    runtime: { ...delayedRemote.runtime, sanitizedError: null },
    terminal: { available: true, url: '/terminal?employeeId=emp-race-delayed' },
  });
  await remotePollStarted;
  await page.evaluate(() => {
    document.querySelector('[data-portal]').dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: {
      id: 'host-race', runtime: { hostOwned: true }, terminal: { available: true, url: '/terminal?employeeId=emp-race-delayed' },
    } }));
    // Keep the existing attached transport as the sentinel. If the stale remote
    // response is applied, its unavailable terminal will close this socket.
    window.__terminalEvents.length = 0;
  });
  releaseRemotePoll();
  await sleep(250);
  const inverseBeforeHostPoll = await page.evaluate(() => ({
    hostOwned: document.querySelector('[data-portal]').dataset.hostOwned,
    events: window.__terminalEvents,
    error: document.querySelector('[data-field="error"]')?.textContent,
  }));
  await waitFor(async () => (await page.locator('[data-field="sessionId"]').innerText()) === 'fresh-host-session', 'host poll after stale remote discard', 5000);
  const inverseAfterHostPoll = {
    session: await page.locator('[data-field="sessionId"]').innerText(),
    model: await headerModel(),
    error: await errorBanner(),
  };
  record('a delayed remote poll cannot overwrite a newly selected host or close its socket',
    inverseBeforeHostPoll.hostOwned === 'true'
      && !inverseBeforeHostPoll.events.some((event) => event.startsWith('socket-closed:'))
      && inverseBeforeHostPoll.error !== 'stale remote error'
      && inverseAfterHostPoll.session === 'fresh-host-session'
      && inverseAfterHostPoll.model === 'fresh/host-model'
      && inverseAfterHostPoll.error.hidden, { inverseBeforeHostPoll, inverseAfterHostPoll });
  await page.unroute('**/api/employees/emp-race-delayed');

  // ---- 18. remote -> host clears stale telemetry before the host poll ------
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    employees: {
      'emp-clear': {
        id: 'emp-clear', availability: 'ready',
        runtime: { hostOwned: false, controlStatus: 'ready', nativeSessionId: 'remote-clear-session', controlModel: 'remote/clear-model' },
        terminal: { available: false, url: null },
      },
    },
    control: {
      state: 'ready', organizationName: 'Stub harness', sessionId: 'host-after-clear', model: 'fresh/host-model',
      transport: 'ACP', terminalReady: true, sessionState: 'idle', canControl: true, error: null,
      modelSyncSupported: true, models: CATALOG,
    },
    controlDelayMs: 0,
  }) });
  await page.evaluate(() => document.querySelector('[data-portal]').dispatchEvent(new CustomEvent(
    'agentcontrol:employee-selected',
    { detail: {
      id: 'emp-clear', availability: 'ready',
      runtime: { hostOwned: false, controlStatus: 'ready', nativeSessionId: 'remote-clear-session', controlModel: 'remote/clear-model' },
      terminal: { available: false, url: null },
    } },
  )));
  await waitFor(async () => (await page.locator('[data-field="sessionId"]').innerText()) === 'remote-clear-session', 'remote telemetry before host swap');

  let releaseHostClear;
  let hostClearStartedResolve;
  const hostClearStarted = new Promise((resolve) => { hostClearStartedResolve = resolve; });
  await page.route('**/api/control', async (route) => {
    const response = await route.fetch();
    hostClearStartedResolve();
    await new Promise((resolve) => { releaseHostClear = resolve; });
    await route.fulfill({ response });
  });
  await page.evaluate(() => document.querySelector('[data-portal]').dispatchEvent(new CustomEvent(
    'agentcontrol:employee-selected',
    { detail: { id: 'host-clear', runtime: { hostOwned: true }, terminal: { available: true, url: '/terminal?employeeId=host-clear' } } },
  )));
  await hostClearStarted;
  const duringHostGap = await page.evaluate(() => ({
    state: document.querySelector('[data-field="state-detail"]')?.textContent.trim(),
    session: document.querySelector('[data-field="sessionId"]')?.textContent.trim(),
    model: document.querySelector('[data-field="model"]')?.textContent.trim(),
    syncedAt: document.querySelector('[data-field="syncedAt"]')?.textContent.trim(),
  }));
  releaseHostClear();
  await waitFor(async () => (await page.locator('[data-field="sessionId"]').innerText()) === 'host-after-clear', 'host telemetry after delayed poll', 5000);
  const afterHostClear = {
    session: await page.locator('[data-field="sessionId"]').innerText(),
    model: await headerModel(),
  };
  record('a remote -> host selection clears stale remote telemetry to Synchronizing / em-dash before the host poll',
    duringHostGap.state === 'Synchronizing' && duringHostGap.session === '\u2014'
      && duringHostGap.model === '\u2014' && duringHostGap.syncedAt === '\u2014'
      && afterHostClear.session === 'host-after-clear' && afterHostClear.model === 'fresh/host-model',
    { duringHostGap, afterHostClear });
  await page.unroute('**/api/control');

  // ---- 17. visible errors do not repeatedly move the viewport -------------
  await page.evaluate(() => { window.__errorScrolls = 0; });
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ control: { error: 'persistent poll error' } }) });
  await waitFor(async () => (await errorBanner()).text === 'persistent poll error', 'persistent error appears');
  await sleep(2300);
  const errorScrolls = await page.evaluate(() => window.__errorScrolls);
  record('repeated polls of the same visible error scroll only on hidden-to-visible transition',
    errorScrolls === 1, { errorScrolls, banner: await errorBanner() });

  // ---- 19. bfcache pagehide/pageshow resumes polling and reattaches -------
  // Close the busy main page first so the shared /api/control request counter
  // measures only this page's activity.
  await page.close();
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    control: {
      state: 'ready', organizationName: 'Stub harness', sessionId: 'resume-session-1', model: 'openai/gpt-5',
      transport: 'ACP', terminalReady: true, sessionState: 'idle', canControl: true, error: null,
      modelSyncSupported: true, models: CATALOG,
    },
    controlDelayMs: 0, resetPollRequests: true,
  }) });
  const resumePage = await context.newPage();
  await resumePage.goto(`${base}/?initial-host=1`, { waitUntil: 'domcontentloaded' });
  await waitFor(async () => (await resumePage.locator('[data-field="sessionId"]').innerText()) === 'resume-session-1', 'initial resume telemetry');
  await waitFor(async () => resumePage.evaluate(() => window.__terminalEvents.some((event) => event.startsWith('socket-opened:'))), 'initial resume socket');
  const pollsBeforeHide = state.controlRequests;
  await resumePage.evaluate(() => {
    window.__terminalEvents.length = 0;
    window.dispatchEvent(new PageTransitionEvent('pagehide'));
  });
  await sleep(100);
  const hidden = await resumePage.evaluate(() => ({
    events: window.__terminalEvents,
  }));
  record('pagehide closes the transport while retaining the selection',
    hidden.events.some((event) => event.startsWith('socket-closed:1000:pagehide')), hidden);

  // Change authority while the page is hidden; the bfcache restore must not
  // trust the pre-hide snapshot.
  await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    control: { sessionId: 'resume-session-2', model: 'anthropic/claude' },
  }) });
  await resumePage.evaluate(() => {
    window.__terminalEvents.length = 0;
    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
  });
  await waitFor(async () => (await resumePage.locator('[data-field="sessionId"]').innerText()) === 'resume-session-2', 'pageshow refreshes telemetry');
  await waitFor(async () => resumePage.evaluate(() => window.__terminalEvents.some((event) => event.startsWith('socket-opened:'))), 'pageshow reopens socket');
  const resumed = await resumePage.evaluate(() => ({
    session: document.querySelector('[data-field="sessionId"]')?.textContent.trim(),
    model: document.querySelector('[data-field="model"]')?.textContent.trim(),
    state: document.querySelector('[data-field="state-detail"]')?.textContent.trim(),
    events: window.__terminalEvents,
  }));
  record('a persisted pageshow polls again, refreshes telemetry, and reopens the ready socket',
    state.controlRequests > pollsBeforeHide && resumed.session === 'resume-session-2'
      && resumed.model === 'anthropic/claude' && resumed.state === 'ready'
      && resumed.events.some((event) => event.startsWith('socket-opened:')),
    { pollsBeforeHide, pollsAfterShow: state.controlRequests, resumed });
  await resumePage.close();

  record('no uncaught page errors during the model sync suite', pageErrors.length === 0, { pageErrors });
} catch (error) {
  record('model sync suite completed without fatal error', false, {
    fatal: String(error && error.stack ? error.stack : error),
  });
} finally {
  await browser.close();
  server.close();
}

const failed = results.filter((result) => result.passed === false).length;
console.log('---');
console.log(`RESULT: ${results.length - failed}/${results.length} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);

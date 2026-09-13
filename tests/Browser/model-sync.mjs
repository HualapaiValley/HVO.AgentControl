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
  window.Terminal = class {
    constructor() { this.cols = 80; this.rows = 24; }
    loadAddon() {} open() {} onData() {} onResize() {} write() {} clear() {} focus() {}
    resize(cols, rows) { this.cols = cols; this.rows = rows; }
  };
  window.FitAddon = { FitAddon: class { proposeDimensions() { return { cols: 80, rows: 24 }; } } };
</script>
<script type="module" src="/js/terminal.js"></script>
</body>
</html>`;

function applyStub(patch) {
  if (patch.control) Object.assign(state.control, patch.control);
  if (patch.modelPlan) Object.assign(state.modelPlan, patch.modelPlan);
  if (patch.resetRequests) state.modelRequests.length = 0;
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
  if (url.pathname === '/api/control' && req.method === 'GET') return send(200, state.control);

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

async function waitFor(predicate, label, timeoutMs = 6000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (await predicate()) return true;
    if (Date.now() >= deadline) throw new Error(`timeout waiting for ${label}`);
    await sleep(100);
  }
}

try {
  await page.goto(base, { waitUntil: 'domcontentloaded' });

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
  record('a rejected change surfaces a visible failure and does not claim success',
    (await receipt()).status === 'error' && (await receipt()).text.includes('not accepted') && (await headerModel()) === 'openai/gpt-5',
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

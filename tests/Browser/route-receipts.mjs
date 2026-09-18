// Deterministic route-module receipt/status test against a LOCAL STUB ONLY.
//
// Serves the real wwwroot/js/employee-detail.js, hiring.js and page-common.js
// behind minimal harnesses that mirror EmployeeDetail.razor and Hiring.razor,
// and stubs the APIs so no .NET process, provider, or container is involved. It
// pins the cross-module status contract the review called out:
//   1. a successful employee-detail load clears any stale page-status error
//   2. a failed employee-detail load reports a visible page-status error
//   3. an in-flight orientation mutation reports a pending receipt
//   4. a failed orientation mutation reports an error receipt
//   5. a successful mutation after a failure clears the receipt to ok
//   6. a successful hiring load clears any stale page-status error
//   7. an in-flight hire create reports a pending receipt
//   8. a failed hire create reports an error receipt, and a later success ok
//   9. a failed hiring load reports a visible page-status error
//
// Usage: node tests/Browser/route-receipts.mjs
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const MODULE_DIR = new URL('../../src/HVO.AgentControl/wwwroot/js/', import.meta.url);
const read = (name) => readFileSync(fileURLToPath(new URL(name, MODULE_DIR)), 'utf8');
const PORTAL_CSS = fileURLToPath(new URL('../../src/HVO.AgentControl/wwwroot/css/portal.css', import.meta.url));
const CHROME = process.env.CHROME_PATH || undefined;

const EMPLOYEE = {
  id: 'emp-1', slug: 'emp-one', displayName: 'Employee One', organizationId: 'org-1',
  departmentId: 'dept-ops', departmentSlug: 'operations', departmentDisplayName: 'Operations',
  roleId: 'role-ops', roleSlug: 'ops', roleDisplayName: 'Operations / IT',
  purpose: 'Keep operations healthy.', instructions: 'Do the work.', rules: 'Be careful.', restrictions: 'No deletes.',
  availability: 'ready',
  runtime: { bindingId: 'binding-1', placement: 'InternalSharedContainer', hostOwned: true, remoteOwned: false, remoteHostId: null, nativeSessionId: 'session-1', sessionTitle: 'Ops session', controlModel: 'stub/model', controlStatus: 'authenticated', sessionState: 'running', terminalAvailable: true, sanitizedError: null },
  orientation: { assignmentId: 'assign-1', orientationVersion: 1, state: 'delivered', revision: 2, restartRequired: false, dispatchHeld: false, holdReasons: [], evidenceSource: 'owner', lastError: null },
  terminal: { supported: true, available: true, reason: '', url: '/terminal/emp-1' },
  recentLogs: { supported: false, reason: 'Recent logs are not supported.' },
};

const DEPARTMENT = { id: 'dept-ops', slug: 'operations', displayName: 'Operations' };
const ROLE = { id: 'role-ops', departmentId: 'dept-ops', slug: 'ops', displayName: 'Operations / IT' };
const OVERVIEW = { id: 'org-1', slug: 'stub', displayName: 'Stub organization', departments: [DEPARTMENT], roles: [ROLE] };

const EMPLOYEE_HARNESS = `<!doctype html><html lang="en"><head><meta charset="utf-8"><link rel="stylesheet" href="/css/portal.css"></head><body>
<section class="page employee-detail" data-page="employee-detail" data-employee-detail data-employee-id="emp-1">
  <p class="page-status" data-page-status role="status">Loading exact employee…</p>
  <div data-employee-content hidden>
    <h1 data-employee-name>Employee</h1><span data-employee-availability data-availability="unknown">Loading</span>
    <dl data-employee-identity></dl><dl data-employee-runtime></dl><dl data-employee-orientation></dl><dl data-employee-diagnostics></dl>
    <button type="button" data-orientation-deliver disabled>Recompose &amp; deliver</button>
    <button type="button" data-orientation-comprehension disabled>Run comprehension</button>
    <button type="button" data-orientation-hold disabled>Set manual hold</button>
    <p class="receipt" data-orientation-receipt role="status" aria-live="polite"></p>
    <section class="control-deck" data-portal></section>
    <span data-selected-employee-name>—</span>
  </div>
</section>
<script type="module" src="/js/employee-detail.js"></script></body></html>`;

const HIRING_HARNESS = `<!doctype html><html lang="en"><head><meta charset="utf-8"><link rel="stylesheet" href="/css/portal.css"></head><body>
<section class="page" data-page="hiring" data-hiring-page>
  <form class="panel-form" data-hire-form>
    <input id="hire-name" name="requestedDisplayName" required />
    <textarea id="hire-purpose" name="purpose" required></textarea>
    <select id="hire-department" name="departmentId" required></select>
    <select id="hire-role" name="roleId" required></select>
    <p data-no-hire-roles hidden></p>
    <select id="hire-placement" name="placement"><option value="InternalSharedContainer">Internal shared container</option></select>
    <input id="hire-cpu" name="cpuLimit" type="number" value="2" /><input id="hire-memory" name="memoryLimitMiB" type="number" value="2048" /><input id="hire-pids" name="pidsLimit" type="number" value="256" />
    <button type="submit" class="btn btn-primary" data-hire-submit disabled>Create hire request</button>
  </form>
  <p class="receipt" data-hire-receipt role="status"></p>
  <p class="page-status" data-page-status role="status">Loading requests…</p>
  <div class="request-list" data-hire-requests hidden></div>
</section>
<script type="module" src="/js/hiring.js"></script></body></html>`;

const state = {
  employeePlans: [],
  mutationPlans: [],
  hirePlans: [],
  createPlans: [],
  rejectPlans: [],
  mutationDelayMs: 0,
  createDelayMs: 0,
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const readBody = (req) => new Promise((resolve) => { let body = ''; req.on('data', (chunk) => { body += chunk; }); req.on('end', () => resolve(body)); });
const send = (res, status, body, type = 'application/json') => { if (res.destroyed) return; res.statusCode = status; res.setHeader('Content-Type', type); res.end(type.includes('json') ? JSON.stringify(body) : body); };
const consume = (plans, fallback) => plans.length ? plans.shift() : fallback;

const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname === '/employee') return send(res, 200, EMPLOYEE_HARNESS, 'text/html; charset=utf-8');
  if (url.pathname === '/hiring') return send(res, 200, HIRING_HARNESS, 'text/html; charset=utf-8');
  if (url.pathname === '/css/portal.css') return send(res, 200, readFileSync(PORTAL_CSS, 'utf8'), 'text/css; charset=utf-8');
  if (url.pathname.startsWith('/js/')) {
    try { return send(res, 200, read(url.pathname.slice('/js/'.length)), 'text/javascript; charset=utf-8'); }
    catch { return send(res, 404, {}); }
  }
  if (url.pathname === '/__stub' && req.method === 'POST') {
    const patch = JSON.parse((await readBody(req)) || '{}');
    const keys = ['employeePlans', 'mutationPlans', 'hirePlans', 'createPlans', 'rejectPlans'];
    if (patch.resetPlans) for (const key of keys) state[key].length = 0;
    for (const key of keys) if (patch[key]) state[key].push(...patch[key]);
    if (typeof patch.mutationDelayMs === 'number') state.mutationDelayMs = patch.mutationDelayMs;
    if (typeof patch.createDelayMs === 'number') state.createDelayMs = patch.createDelayMs;
    return send(res, 200, { ok: true });
  }
  if (url.pathname === '/api/employees/emp-1' && req.method === 'GET') {
    const plan = consume(state.employeePlans, null);
    if (plan) return plan.status >= 400 ? send(res, plan.status, plan.body || { title: 'unavailable' }) : send(res, 200, plan.employee || EMPLOYEE);
    return send(res, 200, EMPLOYEE);
  }
  if (url.pathname === '/api/organization/portal' && req.method === 'GET') return send(res, 200, OVERVIEW);
  if (url.pathname === '/api/hire-requests' && req.method === 'GET') {
    const plan = consume(state.hirePlans, null);
    return plan ? send(res, plan.status || 200, plan.body || []) : send(res, 200, []);
  }
  if (url.pathname === '/api/hire-requests' && req.method === 'POST') {
    if (state.createDelayMs) await sleep(state.createDelayMs);
    const plan = consume(state.createPlans, null);
    if (plan) return send(res, plan.status || 200, plan.body || {});
    return send(res, 200, { id: 'hire-created', state: 'Requested', requestedDisplayName: 'New Hire' });
  }
  if (url.pathname.startsWith('/api/hire-requests/') && url.pathname.endsWith('/reject')) {
    const plan = consume(state.rejectPlans, null);
    return plan ? send(res, plan.status || 200, plan.body || {}) : send(res, 200, { state: 'Rejected' });
  }
  if (url.pathname.startsWith('/api/orientation') && req.method !== 'GET') {
    if (state.mutationDelayMs) await sleep(state.mutationDelayMs);
    const plan = consume(state.mutationPlans, null);
    return plan ? send(res, plan.status || 200, plan.body || { state: 'delivered' }) : send(res, 200, { state: 'delivered' });
  }
  return send(res, 404, {});
});

await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ ...(CHROME ? { executablePath: CHROME } : {}), headless: true, args: ['--no-sandbox'] });
const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
const page = await context.newPage();
const pageErrors = [];
page.on('pageerror', (error) => pageErrors.push(String(error && error.message ? error.message : error)));

const results = [];
const record = (name, passed, detail = {}) => { results.push({ name, passed: !!passed, detail }); console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${Object.keys(detail).length ? ' :: ' + JSON.stringify(detail) : ''}`); };
const stub = (patch) => fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) });
const statusAttr = (selector) => page.getAttribute(selector, 'data-status');
const pageStatusText = () => page.locator('[data-page-status]').innerText();
const receiptText = () => page.locator('[data-orientation-receipt]').innerText();

try {
  // ---- employee detail ------------------------------------------------
  await page.goto(`${base}/employee`, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-employee-content]:not([hidden])');
  record('a successful employee-detail load leaves the page status neutral',
    (await statusAttr('[data-page-status]')) === null && (await pageStatusText()).includes('Exact employee emp-1'),
    { status: await statusAttr('[data-page-status]'), text: await pageStatusText() });

  await stub({ resetPlans: true, employeePlans: [{ status: 503, body: { title: 'employee store down' } }] });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('Employee unavailable'));
  record('a failed employee-detail load reports a visible error page status',
    (await statusAttr('[data-page-status]')) === 'error' && await page.locator('[data-employee-content]').isHidden(),
    { status: await statusAttr('[data-page-status]'), text: await pageStatusText() });

  await stub({ resetPlans: true });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-employee-content]:not([hidden])');
  record('a successful employee-detail reload clears a prior error page status',
    (await statusAttr('[data-page-status]')) === null, { status: await statusAttr('[data-page-status]') });

  // Pending: delay the mutation response and observe the receipt before it lands.
  await stub({ resetPlans: true, mutationDelayMs: 400 });
  await page.click('[data-orientation-hold]');
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').dataset.status === 'pending');
  const pendingVisible = (await statusAttr('[data-orientation-receipt]')) === 'pending' && (await receiptText()).includes('Saving');
  record('an in-flight orientation mutation reports a pending receipt', pendingVisible, { receipt: await receiptText() });
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').dataset.status === 'ok');

  await stub({ resetPlans: true, mutationDelayMs: 0, mutationPlans: [{ status: 503, body: { title: 'orientation store down' } }] });
  await page.click('[data-orientation-deliver]');
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').textContent.includes('Action failed'));
  record('a failed orientation mutation reports an error receipt',
    (await statusAttr('[data-orientation-receipt]')) === 'error' && (await receiptText()).includes('orientation store down'),
    { status: await statusAttr('[data-orientation-receipt]'), receipt: await receiptText() });

  await stub({ resetPlans: true, mutationPlans: [{ status: 200, body: { state: 'delivered' } }] });
  await page.click('[data-orientation-deliver]');
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').textContent.includes('Saved. State'));
  record('a successful mutation after a failure clears the receipt to ok',
    (await statusAttr('[data-orientation-receipt]')) === 'ok' && (await receiptText()).includes('Saved. State: delivered'),
    { status: await statusAttr('[data-orientation-receipt]'), receipt: await receiptText() });

  // ---- hiring ---------------------------------------------------------
  await page.goto(`${base}/hiring`, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-hire-requests]:not([hidden])');
  record('a successful hiring load leaves the page status neutral',
    (await statusAttr('[data-page-status]')) === null, { status: await statusAttr('[data-page-status]') });

  await page.fill('#hire-name', 'New Hire');
  await page.fill('#hire-purpose', 'Deterministic receipt test.');
  await stub({ resetPlans: true, createDelayMs: 400 });
  await page.click('[data-hire-submit]');
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').dataset.status === 'pending');
  record('an in-flight hire create reports a pending receipt',
    (await statusAttr('[data-hire-receipt]')) === 'pending' && (await page.locator('[data-hire-receipt]').innerText()).includes('Creating durable request'),
    { receipt: await page.locator('[data-hire-receipt]').innerText() });
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').dataset.status === 'ok');

  await page.fill('#hire-name', 'New Hire');
  await page.fill('#hire-purpose', 'Deterministic receipt test.');
  await stub({ resetPlans: true, createDelayMs: 0, createPlans: [{ status: 503, body: { title: 'hire store down' } }] });
  await page.click('[data-hire-submit]');
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').textContent.includes('Request failed'));
  record('a failed hire create reports an error receipt',
    (await statusAttr('[data-hire-receipt]')) === 'error' && (await page.locator('[data-hire-receipt]').innerText()).includes('hire store down'),
    { status: await statusAttr('[data-hire-receipt]'), receipt: await page.locator('[data-hire-receipt]').innerText() });

  await page.fill('#hire-name', 'New Hire');
  await page.fill('#hire-purpose', 'Deterministic receipt test.');
  await stub({ resetPlans: true, createPlans: [{ status: 200, body: { id: 'hire-created', state: 'Requested' } }] });
  await page.click('[data-hire-submit]');
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').textContent.includes('Created request'));
  record('a successful hire create after a failure clears the receipt to ok',
    (await statusAttr('[data-hire-receipt]')) === 'ok' && (await page.locator('[data-hire-receipt]').innerText()).includes('No employee has been created.'),
    { status: await statusAttr('[data-hire-receipt]'), receipt: await page.locator('[data-hire-receipt]').innerText() });

  await stub({ resetPlans: true, hirePlans: [{ status: 503, body: { title: 'hire list down' } }] });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('Hire requests unavailable'));
  record('a failed hiring list load reports a visible error page status',
    (await statusAttr('[data-page-status]')) === 'error', { status: await statusAttr('[data-page-status]'), text: await pageStatusText() });

  await stub({ resetPlans: true });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-hire-requests]:not([hidden])');
  record('a successful hiring reload clears a prior error page status',
    (await statusAttr('[data-page-status]')) === null, { status: await statusAttr('[data-page-status]') });

  record('no uncaught page errors during the route receipts suite', pageErrors.length === 0, { pageErrors });
} catch (error) {
  record('route receipts suite completed without fatal error', false, { fatal: String(error && error.stack ? error.stack : error) });
} finally {
  await browser.close();
  server.close();
}

const failed = results.filter((result) => result.passed === false).length;
console.log('---');
console.log(`RESULT: ${results.length - failed}/${results.length} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);

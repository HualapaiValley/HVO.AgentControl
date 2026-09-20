// Deterministic route-module receipt/status test against a LOCAL STUB ONLY.
//
// Serves the real wwwroot/js/employee-detail.js, hiring.js, profiles.js and page-common.js
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
//  10. a successful profiles load clears any stale page-status error
//  11. an in-flight profile create reports a pending receipt
//  12. a failed profile create reports an error receipt, and a later success ok
//  13. a failed profiles load reports a visible page-status error
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
  revision: 3,
  terminal: { supported: true, available: true, reason: '', url: '/terminal/emp-1' },
  recentLogs: { supported: false, reason: 'Recent logs are not supported.' },
  profileStatus: { currentProfileId: 'prof-1', currentProfileDisplayName: 'Developer', currentProfileRevisionId: 'prev-current', currentRevisionNumber: 1, currentImageDigest: `sha256:${'1'.repeat(64)}`, currentPlatform: 'linux/amd64', workerId: 'wrk-1', hostId: 'local-docker', newerRevisionAvailable: true, newerRevisionId: 'prev-newer', newerRevisionNumber: 2, activeRebuildState: null, activeRebuildId: null },
};

const DEPARTMENT = { id: 'dept-ops', slug: 'operations', displayName: 'Operations' };
const ROLE = { id: 'role-ops', departmentId: 'dept-ops', slug: 'ops', displayName: 'Operations / IT' };
const OVERVIEW = { id: 'org-1', slug: 'stub', displayName: 'Stub organization', departments: [DEPARTMENT], roles: [ROLE] };

const EMPLOYEE_HARNESS = `<!doctype html><html lang="en"><head><meta charset="utf-8"><link rel="stylesheet" href="/css/portal.css"></head><body>
<section class="page employee-detail" data-page="employee-detail" data-employee-detail data-employee-id="emp-1">
  <p class="page-status" data-page-status role="status">Loading exact employee…</p>
  <div data-employee-content hidden>
    <h1 data-employee-name>Employee</h1><span data-employee-availability data-availability="unknown">Loading</span>
    <dl data-employee-identity></dl><dl data-employee-runtime></dl><dl data-employee-orientation></dl><dl data-employee-diagnostics></dl><dl data-employee-profile></dl><p data-profile-update-note hidden></p>
    <section data-employee-rebuild hidden><form data-rebuild-form><select name="targetProfileRevisionId" data-rebuild-target></select><input type="checkbox" name="resetWorkspace" data-reset-workspace><input type="checkbox" name="resetHome" data-reset-home><label data-reset-confirmation-wrap hidden><code data-reset-confirmation-phrase></code><input name="resetConfirmation" data-reset-confirmation></label><button type="submit" data-rebuild-submit disabled>Rebuild</button></form><p class="receipt" data-rebuild-receipt></p><div data-rebuild-history></div></section>
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

const PROFILES_HARNESS = `<!doctype html><html lang="en"><head><meta charset="utf-8"><link rel="stylesheet" href="/css/portal.css"></head><body>
<section class="page" data-page="profiles" data-profiles-page>
  <form class="panel-form" data-profile-form>
    <input id="profile-slug" name="slug" required /><input id="profile-name" name="displayName" required />
    <textarea id="profile-description" name="description"></textarea>
    <textarea id="profile-definition" name="definition" required>{"image":"agentcontrol-worker-base"}</textarea>
    <textarea id="profile-fragment" name="dockerfileFragment"></textarea>
    <button class="btn btn-primary" type="submit" data-profile-submit>Create profile</button>
    <p class="receipt" data-profile-receipt role="status"></p>
  </form>
  <p class="page-status" data-page-status role="status">Loading profiles…</p>
  <div class="request-list" data-profile-list hidden></div>
</section>
<script type="module" src="/js/profiles.js"></script></body></html>`;

const state = {
  employeePlans: [],
  mutationPlans: [],
  hirePlans: [],
  createPlans: [],
  rejectPlans: [],
  approvePlans: [],
  profilePlans: [],
  profileCreatePlans: [],
  buildPlans: [],
  rebuildPlans: [],
  rebuildHistory: [{ id: 'reb-applied', state: 'Applied', toProfileRevisionId: 'prev-newer', resetWorkspace: false, resetHome: false, failureSummary: null, updatedAt: '2026-09-19T00:00:00Z' }],
  lastRebuildBody: null,
  mutationDelayMs: 0,
  createDelayMs: 0,
  profileCreateDelayMs: 0,
  approveDelayMs: 0,
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const readBody = (req) => new Promise((resolve) => { let body = ''; req.on('data', (chunk) => { body += chunk; }); req.on('end', () => resolve(body)); });
const send = (res, status, body, type = 'application/json') => { if (res.destroyed) return; res.statusCode = status; res.setHeader('Content-Type', type); res.end(type.includes('json') ? JSON.stringify(body) : body); };
const consume = (plans, fallback) => plans.length ? plans.shift() : fallback;

const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname === '/employee') return send(res, 200, EMPLOYEE_HARNESS, 'text/html; charset=utf-8');
  if (url.pathname === '/hiring') return send(res, 200, HIRING_HARNESS, 'text/html; charset=utf-8');
  if (url.pathname === '/profiles') return send(res, 200, PROFILES_HARNESS, 'text/html; charset=utf-8');
  if (url.pathname === '/css/portal.css') return send(res, 200, readFileSync(PORTAL_CSS, 'utf8'), 'text/css; charset=utf-8');
  if (url.pathname.startsWith('/js/')) {
    try { return send(res, 200, read(url.pathname.slice('/js/'.length)), 'text/javascript; charset=utf-8'); }
    catch { return send(res, 404, {}); }
  }
  if (url.pathname === '/__stub' && req.method === 'POST') {
    const patch = JSON.parse((await readBody(req)) || '{}');
    const keys = ['employeePlans', 'mutationPlans', 'hirePlans', 'createPlans', 'rejectPlans', 'approvePlans', 'profilePlans', 'profileCreatePlans', 'buildPlans', 'rebuildPlans'];
    if (patch.resetPlans) for (const key of keys) state[key].length = 0;
    for (const key of keys) if (patch[key]) state[key].push(...patch[key]);
    if (typeof patch.mutationDelayMs === 'number') state.mutationDelayMs = patch.mutationDelayMs;
    if (typeof patch.createDelayMs === 'number') state.createDelayMs = patch.createDelayMs;
    if (typeof patch.profileCreateDelayMs === 'number') state.profileCreateDelayMs = patch.profileCreateDelayMs;
    if (typeof patch.approveDelayMs === 'number') state.approveDelayMs = patch.approveDelayMs;
    if (patch.rebuildHistory) state.rebuildHistory = patch.rebuildHistory;
    return send(res, 200, { ok: true, lastRebuildBody: state.lastRebuildBody });
  }
  if (url.pathname === '/api/employees/emp-1/rebuilds' && req.method === 'GET') return send(res, 200, state.rebuildHistory);
  if (url.pathname === '/api/employees/emp-1/rebuild' && req.method === 'POST') {
    state.lastRebuildBody = JSON.parse((await readBody(req)) || '{}');
    const plan = consume(state.rebuildPlans, null);
    return plan ? send(res, plan.status || 200, plan.body || {}) : send(res, 200, { rebuild: { id: 'reb-new', state: 'Applied' }, profileStatus: EMPLOYEE.profileStatus });
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
  if (url.pathname.startsWith('/api/hire-requests/') && url.pathname.endsWith('/approve')) {
    if (state.approveDelayMs) await sleep(state.approveDelayMs);
    const plan = consume(state.approvePlans, null);
    return plan ? send(res, plan.status || 200, plan.body || {}) : send(res, 200, { state: 'Approved' });
  }
  if (url.pathname.startsWith('/api/profiles/') && url.pathname.endsWith('/builds') && req.method === 'GET') {
    const plan = consume(state.buildPlans, null);
    return plan ? send(res, plan.status || 200, plan.body || []) : send(res, 200, []);
  }
  if (url.pathname === '/api/profiles' && req.method === 'GET') {
    const plan = consume(state.profilePlans, null);
    return plan ? send(res, plan.status || 200, plan.body || []) : send(res, 200, []);
  }
  if (url.pathname === '/api/profiles' && req.method === 'POST') {
    if (state.profileCreateDelayMs) await sleep(state.profileCreateDelayMs);
    const plan = consume(state.profileCreatePlans, null);
    if (plan) return send(res, plan.status || 200, plan.body || {});
    return send(res, 200, { id: 'prof-created', slug: 'stub', displayName: 'Stub', status: 'active', currentRevisionNumber: 1, currentBuildStatus: 'unbuilt', currentContentHash: 'sha256:0' });
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
  record('newer profile revision note renders without acting',
    (await page.locator('[data-profile-update-note]').innerText()).includes('will not be adopted automatically') && state.lastRebuildBody === null);
  record('applied rebuild history renders', (await page.locator('[data-rebuild-history]').innerText()).includes('Applied'));
  await page.check('[data-reset-workspace]');
  record('reset rebuild requires the exact typed confirmation', await page.locator('[data-rebuild-submit]').isDisabled() && (await page.locator('[data-reset-confirmation-phrase]').innerText()) === 'reset-workspace');
  await page.fill('[data-reset-confirmation]', 'reset-workspace');
  record('reset rebuild enables only after exact confirmation', !(await page.locator('[data-rebuild-submit]').isDisabled()));
  await page.click('[data-rebuild-submit]');
  await page.waitForFunction(() => document.querySelector('[data-rebuild-receipt]').dataset.status === 'ok');
  const rebuildStub = await (await fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json();
  record('rebuild POST includes employee revision and target profile revision', rebuildStub.lastRebuildBody?.expectedRevision === 3 && rebuildStub.lastRebuildBody?.targetProfileRevisionId === 'prev-newer', rebuildStub.lastRebuildBody || {});
  await stub({ rebuildHistory: [
    { id: 'reb-uncertain', state: 'Uncertain', toProfileRevisionId: 'prev-newer', resetWorkspace: false, resetHome: false, failureSummary: 'remote-effect-uncertain', updatedAt: '2026-09-19T00:00:00Z' },
    { id: 'reb-failed', state: 'Failed', toProfileRevisionId: 'prev-newer', resetWorkspace: false, resetHome: false, failureSummary: 'deterministic-rebuild-validation-failed', updatedAt: '2026-09-18T00:00:00Z' },
  ] });
  await page.reload({ waitUntil: 'domcontentloaded' }); await page.waitForSelector('[data-employee-content]:not([hidden])');
  const historyText = await page.locator('[data-rebuild-history]').innerText();
  record('uncertain and failed rebuild history render sanitized summaries', historyText.includes('Uncertain') && historyText.includes('Failed') && historyText.includes('remote-effect-uncertain') && historyText.includes('deterministic-rebuild-validation-failed'));

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

  // ---- approval -------------------------------------------------------
  // A Requested DeveloperContainer request is selectable only when an active
  // current profile revision has a verified built row on a ready host. The
  // harness serves one of each so the exact selection reaches the approve body.
  const approvable = [{
    id: 'hire-approve', state: 'Requested', revision: 4, createdAt: '2026-01-01T00:00:00.0000000+00:00', requestedDisplayName: 'Approve Me',
    departmentDisplayName: 'Operations', roleDisplayName: 'Operations / IT', placement: 'DeveloperContainer',
    cpuLimit: 2, memoryLimitMiB: 2048, pidsLimit: 256, purpose: 'Approve this developer container.',
  }];
  const activeProfiles = [{ id: 'prof-1', slug: 'generic-employee', displayName: 'Generic Employee', status: 'active', currentRevisionId: 'prev-1', currentRevisionNumber: 1 }];
  const verifiedBuilds = [{ id: 'build-1', profileRevisionId: 'prev-1', hostId: 'local-docker', state: 'built', verified: true, imageDigest: 'sha256:' + '9'.repeat(64) }];
  const approvedSummary = [{ ...approvable[0], state: 'Approved', revision: 5, containerProfileRevisionId: 'prev-1', profileBuildId: 'build-1', approvedImageDigest: 'sha256:' + '9'.repeat(64), approvedHostId: 'local-docker', employeeId: 'emp-managed', runtimeBindingId: 'rtb-managed', workerId: null, statusDetail: null }];

  await stub({ resetPlans: true, hirePlans: [{ status: 200, body: approvable }], profilePlans: [{ status: 200, body: activeProfiles }], buildPlans: [{ status: 200, body: verifiedBuilds }] });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.request-card[data-request-id="hire-approve"] [data-approve-profile]:not([disabled])');
  record('a verified build on the controller-local Docker target enables the approval selection',
    await page.locator('[data-approve-host="hire-approve"]').count() === 0 && await page.locator('[data-approve-profile="hire-approve"] option').count() === 1,
    { profileOptions: await page.locator('[data-approve-profile="hire-approve"] option').allTextContents() });

  await stub({ resetPlans: true, approveDelayMs: 400 });
  const approvalRequest = page.waitForRequest((request) => request.url().endsWith('/api/hire-requests/hire-approve/approve'));
  await page.click('.request-card[data-request-id="hire-approve"] button:text("Approve")');
  const approveBody = (await approvalRequest).postDataJSON();
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').dataset.status === 'pending');
  record('an in-flight approval reports a pending receipt and sends the exact selection',
    (await statusAttr('[data-hire-receipt]')) === 'pending' && (await page.locator('[data-hire-receipt]').innerText()).includes('Approving hire-approve')
      && approveBody.expectedRevision === 4 && approveBody.profileRevisionId === 'prev-1' && approveBody.hostId === undefined,
    { receipt: await page.locator('[data-hire-receipt]').innerText(), approveBody });

  await stub({ resetPlans: true, approveDelayMs: 0, approvePlans: [{ status: 409, body: { title: 'Hire request approval conflicted.', detail: 'The hire request changed; reload and retry.' } }] });
  await page.click('.request-card[data-request-id="hire-approve"] button:text("Approve")');
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').dataset.status === 'error');
  record('a failed approval reports an error receipt with the server detail',
    (await statusAttr('[data-hire-receipt]')) === 'error' && (await page.locator('[data-hire-receipt]').innerText()).includes('The hire request changed'),
    { status: await statusAttr('[data-hire-receipt]'), receipt: await page.locator('[data-hire-receipt]').innerText() });

  await stub({ resetPlans: true, approveDelayMs: 0, approvePlans: [{ status: 200, body: approvedSummary[0] }], hirePlans: [{ status: 200, body: approvable }, { status: 200, body: approvedSummary }], profilePlans: [{ status: 200, body: activeProfiles }], buildPlans: [{ status: 200, body: verifiedBuilds }] });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.request-card[data-request-id="hire-approve"] [data-approve-profile]:not([disabled])');
  await page.click('.request-card[data-request-id="hire-approve"] button:text("Approve")');
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').textContent.includes('Approved hire-approve'));
  record('a successful approval clears the receipt and names the created identity without provisioning',
    (await statusAttr('[data-hire-receipt]')) === 'ok' && (await page.locator('[data-hire-receipt]').innerText()).includes('provisioning is queued; it runs in the background'),
    { status: await statusAttr('[data-hire-receipt]'), receipt: await page.locator('[data-hire-receipt]').innerText() });
  const frozenText = await page.locator('.request-card[data-request-id="hire-approve"] [data-frozen-approval]').innerText();
  record('the approved card renders the frozen build, digest, target, employee and binding',
    frozenText.includes('prev-1') && frozenText.includes('build-1') && frozenText.includes('emp-managed') && frozenText.includes('rtb-managed') && frozenText.includes('Controller-local Docker'),
    { frozenText });
  record('the approved card states provisioning is durably queued and never exposes the owner identity',
    (await page.locator('.request-card[data-request-id="hire-approve"] [data-provisioning-note]').innerText()).includes('durably queued provisioning and orientation')
      && !(await page.locator('[data-hiring-page]').innerText()).includes('owner-basic-auth'),
    { provisioningNote: await page.locator('.request-card[data-request-id="hire-approve"] [data-provisioning-note]').innerText() });

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

  // ---- profiles -------------------------------------------------------
  await page.goto(`${base}/profiles`, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-profile-list]:not([hidden])');
  record('a successful profiles load leaves the page status neutral',
    (await statusAttr('[data-page-status]')) === null, { status: await statusAttr('[data-page-status]') });

  await page.fill('#profile-slug', 'stub'); await page.fill('#profile-name', 'Stub');
  await stub({ resetPlans: true, profileCreateDelayMs: 400 });
  await page.click('[data-profile-submit]');
  await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').dataset.status === 'pending');
  record('an in-flight profile create reports a pending receipt',
    (await statusAttr('[data-profile-receipt]')) === 'pending' && (await page.locator('[data-profile-receipt]').innerText()).includes('Creating profile'),
    { receipt: await page.locator('[data-profile-receipt]').innerText() });
  await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').dataset.status === 'ok');

  await page.fill('#profile-slug', 'stub'); await page.fill('#profile-name', 'Stub'); await page.fill('#profile-definition', '{"image":"agentcontrol-worker-base"}');
  await stub({ resetPlans: true, profileCreateDelayMs: 0, profileCreatePlans: [{ status: 422, body: { title: 'Invalid container profile.', detail: "Key 'privileged' is not allowed" } }] });
  await page.click('[data-profile-submit]');
  await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').textContent.includes('Create failed'));
  record('a failed profile create reports an error receipt with the server detail',
    (await statusAttr('[data-profile-receipt]')) === 'error' && (await page.locator('[data-profile-receipt]').innerText()).includes("'privileged' is not allowed"),
    { status: await statusAttr('[data-profile-receipt]'), receipt: await page.locator('[data-profile-receipt]').innerText() });

  await page.fill('#profile-slug', 'stub'); await page.fill('#profile-name', 'Stub'); await page.fill('#profile-definition', '{"image":"agentcontrol-worker-base"}');
  await stub({ resetPlans: true });
  await page.click('[data-profile-submit]');
  await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').textContent.includes('Created profile'));
  record('a successful profile create after a failure clears the receipt to ok',
    (await statusAttr('[data-profile-receipt]')) === 'ok' && (await page.locator('[data-profile-receipt]').innerText()).includes('No image was built and no employee was created.'),
    { status: await statusAttr('[data-profile-receipt]') });

  await stub({ resetPlans: true, profilePlans: [{ status: 503, body: { title: 'profile store down' } }] });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('Container profiles unavailable'));
  record('a failed profiles list load reports a visible error page status',
    (await statusAttr('[data-page-status]')) === 'error', { status: await statusAttr('[data-page-status]'), text: await pageStatusText() });

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

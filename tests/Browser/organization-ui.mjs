// Organization portal UI races and dynamic rendering against a loopback stub.
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ORGANIZATION_JS = fileURLToPath(new URL('../../src/HVO.AgentControl/wwwroot/js/organization.js', import.meta.url));
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const employee = (id, name, departmentSlug, departmentName, terminal = {}) => ({
  id, slug: id, displayName: name, organizationId: 'org-1', departmentId: `dept-${departmentSlug}`,
  departmentSlug, departmentDisplayName: departmentName, roleId: `role-${departmentSlug}`,
  roleSlug: `${departmentSlug}-role`, roleDisplayName: `${departmentName} role`, purpose: 'Purpose',
  instructions: 'Instructions', rules: 'Rules', restrictions: 'Restrictions', availability: 'ready',
  runtime: { bindingId: `binding-${id}`, placement: 'internal-shared-container', hostOwned: true,
    nativeSessionId: 'session-1', sessionTitle: 'Session', controlStatus: 'ready', sessionState: 'idle',
    controlModel: 'provider/model', sanitizedError: null },
  orientation: { assignmentId: 'assignment-1', orientationVersion: 'v1', state: 'Comprehended', revision: 1,
    restartRequired: false, dispatchHeld: false, holdReasons: [], evidenceSource: 'live-model', lastError: null },
  terminal: { supported: true, available: true, reason: 'available', url: `/terminal?employeeId=${id}`, ...terminal },
  recentLogs: { supported: false, reason: 'not implemented' },
});
const operations = employee('emp-operations', 'Operations employee', 'operations', 'Operations');
const development = employee('emp-development', 'Development employee', 'development', 'Development');
const finance = employee('emp-finance', 'Finance employee', 'finance', 'Finance');
const overview = (revision, displayName = 'Stub organization') => ({
  id: 'org-1', slug: 'stub', displayName, description: 'Authoritative stub', basicInstructions: `instructions-${revision}`,
  revision, departments: [
    { id: 'dept-development', slug: 'development', displayName: 'Development', employeeCount: 1, availability: [] },
    { id: 'dept-operations', slug: 'operations', displayName: 'Operations', employeeCount: 1, availability: [] },
    { id: 'dept-qa', slug: 'qa', displayName: 'QA', employeeCount: 0, availability: [] },
    { id: 'dept-finance', slug: 'finance', displayName: 'Finance', employeeCount: 1, availability: [] },
  ],
  roles: [
    { id: 'role-operations', slug: 'operations-role', displayName: 'Operations role', standingInstructions: 'ops standing', revision },
    { id: 'role-development', slug: 'development-role', displayName: 'Development role', standingInstructions: 'dev standing', revision },
  ],
  employees: [operations, development, finance],
  availability: [
    { category: 'ready', count: 3 }, { category: 'held', count: 0 }, { category: 'reload-required', count: 0 },
    { category: 'orientation-failed', count: 0 }, { category: 'orientation-stale', count: 0 },
    { category: 'runtime-unavailable', count: 0 },
  ],
  pendingApprovals: { supported: false, count: 0, items: [], reason: 'not implemented' },
  failuresNeedingAttention: [{ employeeId: development.id, employeeDisplayName: development.displayName,
    category: 'orientation-stale', summary: 'Needs attention', url: `/#employee/${development.id}` }],
});

const state = {
  organizationPlans: [], employeePlans: new Map(), organization: overview(1), employeeRequests: [], mutations: [],
};
const HARNESS = `<!doctype html><html><body>
<div data-portal data-runtime-state="ready">
<a class="skip-link" href="#portal-content">Skip</a>
<nav><button data-nav="overview">Overview</button><button data-nav="operations">Operations</button><button data-nav="development">Development</button><button data-nav="qa">QA</button><button data-nav="system">System configuration</button></nav>
<main id="portal-content" tabindex="-1"><section data-org-overview><p data-org-state></p>
<section data-view="overview"><p data-org-description></p><p data-org-basic-instructions></p><dl data-org-departments></dl><dl data-org-availability></dl><p data-pending-approvals></p><ul data-org-failures></ul></section>
<div data-department-views></div>
<section data-view="employee" hidden><button data-back-to-department>Back</button><h2 data-employee-name></h2><span data-employee-availability></span><dl data-employee-identity></dl><dl data-employee-runtime></dl><dl data-employee-orientation></dl><dl data-employee-diagnostics></dl><button data-orientation-deliver></button><button data-orientation-comprehension></button><button data-orientation-hold></button><p data-orientation-receipt></p></section>
<section data-view="system" hidden><form data-org-name-form><input data-org-name-input data-organization-control><button data-organization-control type="submit">save</button></form><form data-org-instructions-form><textarea data-org-instructions data-organization-control></textarea><button data-organization-control type="submit">save</button></form><form data-role-instructions-form><select data-role-select data-organization-control></select><textarea data-role-instructions data-organization-control></textarea><button data-organization-control type="submit">save</button></form><p data-config-receipt></p></section>
</section></main><span data-selected-employee-name></span></div><script type="module" src="/js/organization.js"></script></body></html>`;
const readBody = (req) => new Promise((resolve) => { let body = ''; req.on('data', (chunk) => body += chunk); req.on('end', () => resolve(body)); });
const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const send = (status, body, type = 'application/json') => { res.statusCode = status; res.setHeader('Content-Type', type); res.end(type.includes('json') ? JSON.stringify(body) : body); };
  if (url.pathname === '/') return send(200, HARNESS, 'text/html');
  if (url.pathname === '/js/organization.js') return send(200, readFileSync(ORGANIZATION_JS, 'utf8'), 'text/javascript');
  if (url.pathname === '/api/organization/portal') {
    const plan = state.organizationPlans.shift() || { delay: 0, body: state.organization };
    if (plan.delay) await sleep(plan.delay);
    return send(200, plan.body);
  }
  if (url.pathname.startsWith('/api/employees/')) {
    const id = decodeURIComponent(url.pathname.split('/').pop()); state.employeeRequests.push(id);
    const plans = state.employeePlans.get(id) || []; const plan = plans.shift() || { delay: 0, body: [operations, development, finance].find((item) => item.id === id) };
    if (plan.delay) await sleep(plan.delay);
    return send(plan.status || 200, plan.body || {});
  }
  if (url.pathname === '/__stub' && req.method === 'POST') {
    const patch = JSON.parse(await readBody(req) || '{}');
    if (patch.organizationPlans) state.organizationPlans.push(...patch.organizationPlans);
    if (patch.employeePlans) for (const [id, plans] of Object.entries(patch.employeePlans)) state.employeePlans.set(id, plans);
    if (patch.organization) state.organization = patch.organization;
    if (patch.resetEmployeeRequests) state.employeeRequests.length = 0;
    return send(200, { ok: true });
  }
  if (['PATCH', 'PUT', 'POST'].includes(req.method)) {
    const body = JSON.parse(await readBody(req) || '{}'); state.mutations.push({ path: url.pathname, body });
    if (body.displayName === '') return send(400, { title: 'invalid' });
    if (body.displayName === 'conflict') return send(409, { title: 'conflict' });
    if (url.pathname.includes('manual-hold')) return send(200, { state: 'Comprehended', holdReasons: body.held ? ['manual'] : [] });
    if (url.pathname.includes('deliver')) return send(200, { state: 'Delivered', restartRequired: true });
    if (url.pathname.includes('comprehension')) return send(200, { state: 'Failed' });
    state.organization = overview(state.organization.revision + 1, body.displayName || state.organization.displayName);
    return send(200, state.organization);
  }
  return send(404, {});
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
const page = await browser.newPage();
const results = []; const record = (name, passed, detail = {}) => { results.push({ name, passed }); console.log(`${passed ? 'PASS' : 'FAIL'}  ${name} :: ${JSON.stringify(detail)}`); };
const stub = (patch) => fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) });
try {
  await page.goto(base); await page.waitForSelector('[data-organization-loaded="1"]');
  const nav = await page.$$eval('[data-nav]:not([hidden])', (nodes) => nodes.map((n) => n.dataset.nav));
  record('authoritative future department is added before System', JSON.stringify(nav) === JSON.stringify(['overview','operations','development','qa','finance','system']), { nav });
  await page.click('[data-nav="development"]');
  record('non-Operations employee renders from authoritative data', (await page.textContent('[data-department-employees="development"]')).includes('Development employee'));
  await page.click('[data-nav="qa"]'); record('authoritative empty department renders an actual empty state', (await page.textContent('[data-department-employees="qa"]')).includes('No employees'));
  await page.click('[data-nav="system"]'); const roles = await page.$$eval('[data-role-select] option', (nodes) => nodes.map((n) => n.textContent));
  record('all authoritative roles are selectable', JSON.stringify(roles) === JSON.stringify(['Operations role','Development role']), { roles });

  await page.fill('[data-org-instructions]', 'typed draft survives');
  await stub({ organizationPlans: [{ delay: 300, body: overview(2) }, { delay: 20, body: overview(3) }] });
  await page.evaluate(() => { const p = document.querySelector('[data-portal]'); p.dataset.runtimeState = 'degraded'; setTimeout(() => p.dataset.runtimeState = 'ready', 20); });
  await page.waitForSelector('[data-organization-loaded="3"]'); await sleep(350);
  record('typing survives background refresh and stale older organization response', (await page.inputValue('[data-org-instructions]')) === 'typed draft survives' && (await page.getAttribute('[data-portal]', 'data-organization-loaded')) === '3');

  await stub({ employeePlans: { 'emp-operations': [{ delay: 300, body: operations }] }, resetEmployeeRequests: true });
  await page.click('[data-nav="operations"]'); await page.click('[data-department-employees="operations"] .employee-card'); await page.click('[data-nav="system"]'); await sleep(350);
  record('delayed employee selection cannot navigate away from System', await page.isVisible('[data-view="system"]') && await page.evaluate(() => location.hash === '#system'));

  await page.fill('[data-org-name-input]', 'conflict'); await page.click('[data-org-name-form] button'); await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('409 remains visible and preserves the conflicting draft', (await page.inputValue('[data-org-name-input]')) === 'conflict' && (await page.textContent('[data-config-receipt]')).includes('stale'));
  await page.fill('[data-org-name-input]', ''); await page.click('[data-org-name-form] button'); await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 400'));
  record('validation failure preserves the invalid draft', (await page.inputValue('[data-org-name-input]')) === '');

  await page.evaluate(() => location.hash = '#bogus'); await page.waitForFunction(() => location.hash === '#overview'); record('unknown hash normalizes to overview', await page.isVisible('[data-view="overview"]'));
  await page.click('[data-nav="system"]'); await page.focus('.skip-link'); await page.keyboard.press('Enter'); await page.waitForFunction(() => document.activeElement?.id === 'portal-content');
  record('keyboard skip focuses content without changing the visible route', await page.isVisible('[data-view="system"]') && await page.evaluate(() => location.hash === '#system'));

  await page.click('[data-nav="development"]'); await page.click('[data-department-employees="development"] .employee-card'); await page.waitForSelector('[data-view="employee"]:not([hidden])');
  await page.click('[data-orientation-hold]'); await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').textContent === 'Manual hold set. State: Comprehended.');
  record('manual hold receipt is operation-specific', true);
  await page.click('[data-orientation-deliver]'); await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').textContent === 'Delivered. Runtime restart required.'); record('deliver receipt reports restart required', true);
  await page.click('[data-orientation-comprehension]'); await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').textContent === 'Comprehension failed: Failed.');
  record('comprehension receipt reports actual failed state', (await page.getAttribute('[data-orientation-receipt]', 'data-status')) === 'error');
} catch (error) { record('suite completed', false, { error: String(error.stack || error) }); }
await browser.close(); server.close();
const failed = results.filter((r) => !r.passed).length; console.log(`RESULT: ${results.length - failed}/${results.length} passed`); process.exit(failed ? 1 : 0);

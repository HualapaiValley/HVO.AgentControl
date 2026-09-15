// Organization portal UI races and dynamic rendering against a loopback stub.
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ORGANIZATION_JS = fileURLToPath(new URL('../../src/HVO.AgentControl/wwwroot/js/organization.js', import.meta.url));
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
const overview = (revision, displayName = 'Stub organization', basicInstructions = `instructions-${revision}`) => ({
  id: 'org-1', slug: 'stub', displayName, description: 'Authoritative stub', basicInstructions,
  revision, departments: [
    { id: 'dept-development', slug: 'development', displayName: 'Development', employeeCount: 1, availability: [] },
    { id: 'dept-operations', slug: 'operations', displayName: 'Operations', employeeCount: 1, availability: [] },
    { id: 'dept-qa', slug: 'qa', displayName: 'QA', employeeCount: 0, availability: [] },
    { id: 'dept-finance', slug: 'finance', displayName: 'Finance', employeeCount: 1, availability: [] },
  ],
  roles: [
    { id: 'role-operations', slug: 'operations-role', displayName: 'Operations role', standingInstructions: `ops standing ${revision}`, revision },
    { id: 'role-development', slug: 'development-role', displayName: 'Development role', standingInstructions: `dev standing ${revision}`, revision },
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
  organizationPlans: [], employeePlans: new Map(), mutationPlans: [], organization: overview(1),
  employeeRequests: [], employeeResponses: [], mutations: [], gates: new Map(),
};
const waitForGate = (name) => {
  let gate = state.gates.get(name);
  if (!gate) {
    let release;
    const promise = new Promise((resolve) => { release = resolve; });
    gate = { promise, release, released: false };
    state.gates.set(name, gate);
  }
  return gate.promise;
};
const releaseGate = (name) => {
  let gate = state.gates.get(name);
  if (!gate) {
    gate = { promise: Promise.resolve(), release: () => {}, released: true };
    state.gates.set(name, gate);
  }
  if (!gate.released) { gate.released = true; gate.release(); }
};
const applyPlan = async (plan) => { if (plan?.gate) await waitForGate(plan.gate); };
const HARNESS = `<!doctype html><html><body>
<div data-portal data-runtime-state="ready">
<a class="skip-link" href="#portal-content">Skip</a>
<nav><button data-nav="overview">Overview</button><button data-nav="operations">Operations</button><button data-nav="development">Development</button><button data-nav="qa">QA</button><button data-nav="system">System configuration</button></nav>
<main id="portal-content" tabindex="-1"><section data-org-overview><p data-org-state></p>
<section data-view="overview"><p data-org-description></p><p data-org-basic-instructions></p><dl data-org-departments></dl><dl data-org-availability></dl><p data-pending-approvals></p><ul data-org-failures></ul></section>
<div data-department-views></div>
<section data-view="employee" hidden><button data-back-to-department>Back</button><h2 data-employee-name></h2><span data-employee-availability></span><dl data-employee-identity></dl><dl data-employee-runtime></dl><dl data-employee-orientation></dl><dl data-employee-diagnostics></dl><button data-orientation-deliver></button><button data-orientation-comprehension></button><button data-orientation-hold></button><p data-orientation-receipt></p></section>
<section data-view="system" hidden><form data-org-name-form><input data-org-name-input data-organization-control><button data-organization-control type="submit">save</button><button type="button" data-reset-org-name hidden></button></form><form data-org-instructions-form><textarea data-org-instructions data-organization-control></textarea><button data-organization-control type="submit">save</button><button type="button" data-reset-org-instructions hidden></button></form><form data-role-instructions-form><select data-role-select data-organization-control></select><textarea data-role-instructions data-organization-control></textarea><button data-organization-control type="submit">save</button><button type="button" data-reset-role-instructions hidden></button></form><p data-config-receipt></p></section>
</section></main><span data-selected-employee-name></span></div><script>document.querySelector('[data-portal]').addEventListener('agentcontrol:employee-selected',event=>document.querySelector('[data-portal]').dataset.terminalEmployeeId=event.detail.id)</script><script type="module" src="/js/organization.js"></script></body></html>`;
const readBody = (req) => new Promise((resolve) => { let body = ''; req.on('data', (chunk) => body += chunk); req.on('end', () => resolve(body)); });
const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const send = (status, body, type = 'application/json') => { if (res.destroyed) return; res.statusCode = status; res.setHeader('Content-Type', type); res.end(type.includes('json') ? JSON.stringify(body) : body); };
  if (url.pathname === '/') return send(200, HARNESS, 'text/html');
  if (url.pathname === '/js/organization.js') return send(200, readFileSync(ORGANIZATION_JS, 'utf8'), 'text/javascript');
  if (url.pathname === '/api/organization/portal') {
    const plan = state.organizationPlans.shift() || { body: state.organization };
    await applyPlan(plan);
    return send(plan.status || 200, plan.body || state.organization);
  }
  if (url.pathname.startsWith('/api/employees/')) {
    const id = decodeURIComponent(url.pathname.split('/').pop()); state.employeeRequests.push(id);
    const plans = state.employeePlans.get(id) || [];
    const plan = plans.shift() || { body: [operations, development, finance].find((item) => item.id === id) };
    await applyPlan(plan);
    state.employeeResponses.push(id);
    return send(plan.status || 200, plan.body || {});
  }
  if (url.pathname === '/__stub' && req.method === 'POST') {
    const patch = JSON.parse(await readBody(req) || '{}');
    if (patch.organizationPlans) state.organizationPlans.push(...patch.organizationPlans);
    if (patch.employeePlans) for (const [id, plans] of Object.entries(patch.employeePlans)) state.employeePlans.set(id, plans);
    if (patch.mutationPlans) state.mutationPlans.push(...patch.mutationPlans);
    if (patch.organization) state.organization = patch.organization;
    if (patch.resetEmployeeRequests) { state.employeeRequests.length = 0; state.employeeResponses.length = 0; }
    if (patch.resetMutations) state.mutations.length = 0;
    if (patch.releaseGates) patch.releaseGates.forEach(releaseGate);
    return send(200, { ok: true });
  }
  if (['PATCH', 'PUT', 'POST'].includes(req.method)) {
    const body = JSON.parse(await readBody(req) || '{}'); state.mutations.push({ path: url.pathname, body });
    const plan = state.mutationPlans.shift();
    await applyPlan(plan);
    if (plan) {
      if (plan.organization) state.organization = plan.organization;
      return send(plan.status || 200, plan.body || plan.organization || {});
    }
    if (body.displayName === '') return send(400, { title: 'invalid' });
    if (url.pathname.includes('manual-hold')) return send(200, { state: 'Comprehended', holdReasons: body.held ? ['manual'] : [] });
    if (url.pathname.includes('deliver')) return send(200, { state: 'Delivered', restartRequired: true });
    if (url.pathname.includes('comprehension')) return send(200, { state: 'Failed' });
    state.organization = overview(state.organization.revision + 1, body.displayName || state.organization.displayName,
      body.basicInstructions || state.organization.basicInstructions);
    return send(200, state.organization);
  }
  return send(404, {});
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
const page = await browser.newPage();
const results = [];
const record = (name, passed, detail = {}) => { results.push({ name, passed }); console.log(`${passed ? 'PASS' : 'FAIL'}  ${name} :: ${JSON.stringify(detail)}`); };
const stub = (patch) => fetch(`${base}/__stub`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) });
const waitFor = async (predicate, description, timeout = 3000) => {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error(`Timed out waiting for ${description}`);
};
const refreshOrganization = async (organization) => {
  await stub({ organization, organizationPlans: [{ body: organization }] });
  await page.evaluate(() => {
    const portal = document.querySelector('[data-portal]');
    portal.dataset.runtimeState = portal.dataset.runtimeState === 'ready' ? 'degraded' : 'ready';
  });
  await page.waitForFunction((revision) => document.querySelector('[data-portal]').dataset.organizationLoaded === String(revision), organization.revision);
};
try {
  await page.goto(base); await page.waitForSelector('[data-organization-loaded="1"]');
  const nav = await page.$$eval('[data-nav]:not([hidden])', (nodes) => nodes.map((n) => n.dataset.nav));
  record('authoritative future department is added before System', JSON.stringify(nav) === JSON.stringify(['overview','operations','development','qa','finance','system']), { nav });
  await page.click('[data-nav="development"]');
  record('non-Operations employee renders from authoritative data', (await page.textContent('[data-department-employees="development"]')).includes('Development employee'));
  await page.click('[data-nav="qa"]'); record('authoritative empty department renders an actual empty state', (await page.textContent('[data-department-employees="qa"]')).includes('No employees'));
  await page.click('[data-nav="system"]');
  const roles = await page.$$eval('[data-role-select] option', (nodes) => nodes.map((n) => n.textContent));
  record('all authoritative roles are selectable', JSON.stringify(roles) === JSON.stringify(['Operations role','Development role']), { roles });

  await page.fill('[data-org-instructions]', 'instructions draft at revision 1');
  await refreshOrganization(overview(2));
  record('organization instructions preserve draft and visibly mark authoritative change',
    await page.inputValue('[data-org-instructions]') === 'instructions draft at revision 1'
      && await page.getAttribute('[data-org-instructions]', 'data-draft-state') === 'conflict'
      && await page.isVisible('[data-reset-org-instructions]'));
  await stub({ resetMutations: true, mutationPlans: [{ status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-org-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('organization instructions submit frozen base revision and preserve draft after 409',
    state.mutations[0]?.body.revision === 1
      && state.mutations[0]?.body.basicInstructions === 'instructions draft at revision 1'
      && await page.inputValue('[data-org-instructions]') === 'instructions draft at revision 1', { mutation: state.mutations[0] });
  await page.click('[data-reset-org-instructions]');
  record('organization instructions reset adopts authoritative value', await page.inputValue('[data-org-instructions]') === 'instructions-2');

  await page.fill('[data-org-name-input]', 'name draft at revision 2');
  await refreshOrganization(overview(3, 'Authoritative name 3'));
  await stub({ resetMutations: true, mutationPlans: [{ status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('organization name submit uses original revision after authoritative refresh',
    state.mutations[0]?.body.revision === 2
      && state.mutations[0]?.body.displayName === 'name draft at revision 2'
      && await page.getAttribute('[data-org-name-input]', 'data-draft-state') === 'conflict', { mutation: state.mutations[0] });
  await page.click('[data-reset-org-name]');
  await page.fill('[data-org-name-input]', '');
  await page.click('[data-org-name-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 400'));
  record('validation failure preserves the invalid draft', await page.inputValue('[data-org-name-input]') === '');
  await page.click('[data-reset-org-name]');

  await page.fill('[data-role-instructions]', 'role draft at revision 3');
  await refreshOrganization(overview(4, 'Authoritative name 4'));
  await stub({ resetMutations: true, mutationPlans: [{ status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('role draft submit uses role revision captured when editing began',
    state.mutations[0]?.path === '/api/roles/role-operations/instructions'
      && state.mutations[0]?.body.revision === 3
      && state.mutations[0]?.body.standingInstructions === 'role draft at revision 3'
      && await page.inputValue('[data-role-instructions]') === 'role draft at revision 3', { mutation: state.mutations[0] });
  await page.click('[data-reset-role-instructions]');

  await page.fill('[data-org-name-input]', 'save A');
  await stub({ resetMutations: true, mutationPlans: [{ gate: 'save-a', organization: overview(5, 'save A') }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await waitFor(() => state.mutations.length === 1, 'delayed save A request');
  await page.fill('[data-org-name-input]', 'newer draft B');
  await stub({ releaseGates: ['save-a'] });
  await page.waitForSelector('[data-organization-loaded="5"]');
  record('successful save response and authoritative reload preserve newer draft B',
    await page.inputValue('[data-org-name-input]') === 'newer draft B'
      && await page.getAttribute('[data-org-name-input]', 'data-draft-state') === 'conflict');
  await stub({ resetMutations: true, mutationPlans: [{ status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('newer draft B next submit retains safe pre-save base revision',
    state.mutations[0]?.body.revision === 4 && state.mutations[0]?.body.displayName === 'newer draft B', { mutation: state.mutations[0] });
  await page.click('[data-reset-org-name]');

  await page.fill('[data-org-name-input]', 'pending conflict A');
  await stub({ resetMutations: true, mutationPlans: [{ gate: 'pending-conflict-a', status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await waitFor(() => state.mutations.length === 1, 'pending conflict A request');
  await page.fill('[data-org-name-input]', 'pending conflict newer B');
  await stub({ releaseGates: ['pending-conflict-a'] });
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('pending 409 marks a newer edit in the submitted lineage as conflict without replacing it',
    state.mutations[0]?.body.revision === 5
      && state.mutations[0]?.body.displayName === 'pending conflict A'
      && await page.inputValue('[data-org-name-input]') === 'pending conflict newer B'
      && await page.getAttribute('[data-org-name-input]', 'data-draft-state') === 'conflict'
      && await page.isVisible('[data-reset-org-name]'), { mutation: state.mutations[0] });
  await stub({ resetMutations: true, mutationPlans: [{ status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('newer edit after pending 409 retains the lineage original base revision on retry',
    state.mutations[0]?.body.revision === 5
      && state.mutations[0]?.body.displayName === 'pending conflict newer B', { mutation: state.mutations[0] });
  await page.click('[data-reset-org-name]');

  await page.fill('[data-org-name-input]', 'identical conflict A');
  await stub({ resetMutations: true, mutationPlans: [{ gate: 'old-conflict-lineage', status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await waitFor(() => state.mutations.length === 1, 'old conflict lineage request');
  await page.click('[data-reset-org-name]');
  await page.fill('[data-org-name-input]', 'identical conflict A');
  await stub({ releaseGates: ['old-conflict-lineage'] });
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('old 409 cannot mark an identical reset and recreated draft lineage as conflict',
    await page.inputValue('[data-org-name-input]') === 'identical conflict A'
      && await page.getAttribute('[data-org-name-input]', 'data-draft-state') === 'dirty'
      && await page.isVisible('[data-reset-org-name]'));
  await page.click('[data-reset-org-name]');

  await page.fill('[data-org-name-input]', 'identical recreated A');
  await stub({ resetMutations: true, mutationPlans: [{ gate: 'old-success-identical', organization: overview(6, 'identical recreated A') }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await waitFor(() => state.mutations.length === 1, 'old success identical request');
  await page.click('[data-reset-org-name]');
  await page.fill('[data-org-name-input]', 'identical recreated A');
  await stub({ releaseGates: ['old-success-identical'] });
  await page.waitForSelector('[data-organization-loaded="6"]');
  record('old success cannot clear an identical draft recreated in a new lineage',
    await page.inputValue('[data-org-name-input]') === 'identical recreated A'
      && await page.getAttribute('[data-org-name-input]', 'data-draft-state') === 'conflict'
      && await page.isVisible('[data-reset-org-name]'));
  await stub({ resetMutations: true, mutationPlans: [{ status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-org-name-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('HTTP 409'));
  record('identical recreated draft retains its own frozen revision after old success reload',
    state.mutations[0]?.body.revision === 5
      && state.mutations[0]?.body.displayName === 'identical recreated A', { mutation: state.mutations[0] });
  await page.click('[data-reset-org-name]');

  await page.fill('[data-org-instructions]', 'successful clean save');
  await stub({ resetMutations: true, mutationPlans: [{ organization: overview(7, 'save A', 'successful clean save') }] });
  await page.click('[data-org-instructions-form] button[type="submit"]');
  await page.waitForSelector('[data-organization-loaded="7"]');
  record('successful save without newer edit clears draft and adopts new authoritative revision',
    await page.getAttribute('[data-org-instructions]', 'data-draft-state') === 'clean'
      && await page.inputValue('[data-org-instructions]') === 'successful clean save'
      && !(await page.isVisible('[data-reset-org-instructions]')));

  await stub({ resetEmployeeRequests: true, employeePlans: { 'emp-operations': [{ gate: 'abandoned-explicit-a', body: operations }] } });
  await page.click('[data-nav="operations"]');
  await page.click('[data-department-employees="operations"] .employee-card');
  await waitFor(() => state.employeeRequests.includes('emp-operations'), 'explicit A request before leaving');
  await page.click('[data-nav="system"]');
  await stub({ releaseGates: ['abandoned-explicit-a'] });
  await waitFor(() => state.employeeResponses.includes('emp-operations'), 'abandoned explicit A server response');
  record('delayed employee selection cannot navigate away from System', await page.isVisible('[data-view="system"]') && await page.evaluate(() => location.hash === '#system'));

  await page.click('[data-nav="operations"]');
  await page.click('[data-department-employees="operations"] .employee-card');
  await page.waitForSelector('[data-view="employee"]:not([hidden])');
  await stub({ resetEmployeeRequests: true, employeePlans: { 'emp-development': [{ gate: 'explicit-b', body: development }] }, organizationPlans: [{ body: overview(8) }], organization: overview(8) });
  await page.evaluate(() => location.hash = '#employee/emp-development');
  await waitFor(() => state.employeeRequests.includes('emp-development'), 'explicit employee B request');
  await page.evaluate(() => {
    const portal = document.querySelector('[data-portal]');
    portal.dataset.runtimeState = portal.dataset.runtimeState === 'ready' ? 'degraded' : 'ready';
  });
  await page.waitForSelector('[data-organization-loaded="8"]');
  record('organization refresh does not cancel or replace pending explicit B selection',
    JSON.stringify(state.employeeRequests) === JSON.stringify(['emp-development']), { requests: state.employeeRequests });
  await stub({ releaseGates: ['explicit-b'] });
  await page.waitForFunction(() => document.querySelector('[data-portal]').dataset.selectedEmployeeId === 'emp-development');
  record('explicit B commits details, hash, portal id, and terminal selection event',
    (await page.textContent('[data-employee-name]')) === development.displayName
      && await page.evaluate(() => location.hash === '#employee/emp-development')
      && await page.getAttribute('[data-portal]', 'data-selected-employee-id') === development.id
      && await page.getAttribute('[data-portal]', 'data-terminal-employee-id') === development.id);

  await page.click('[data-nav="operations"]');
  await page.click('[data-department-employees="operations"] .employee-card');
  await page.waitForFunction(() => document.querySelector('[data-portal]').dataset.selectedEmployeeId === 'emp-operations');
  await stub({ resetEmployeeRequests: true, employeePlans: { 'emp-operations': [{ gate: 'passive-a', body: operations }] }, organizationPlans: [{ body: overview(9) }], organization: overview(9) });
  await page.evaluate(() => {
    const portal = document.querySelector('[data-portal]');
    portal.dataset.runtimeState = portal.dataset.runtimeState === 'ready' ? 'degraded' : 'ready';
  });
  await waitFor(() => state.employeeRequests.includes('emp-operations'), 'passive A refresh request');
  await page.evaluate(() => location.hash = '#employee/emp-development');
  await page.waitForFunction(() => document.querySelector('[data-portal]').dataset.selectedEmployeeId === 'emp-development');
  await stub({ releaseGates: ['passive-a'] });
  await waitFor(() => state.employeeResponses.includes('emp-operations'), 'passive A server response after explicit B');
  record('delayed passive A response cannot overwrite explicit B',
    await page.getAttribute('[data-portal]', 'data-selected-employee-id') === development.id
      && (await page.textContent('[data-employee-name]')) === development.displayName
      && await page.evaluate(() => location.hash === '#employee/emp-development'));

  await page.evaluate(() => location.hash = '#bogus');
  await page.waitForFunction(() => location.hash === '#overview');
  record('unknown hash normalizes to overview', await page.isVisible('[data-view="overview"]'));
  await page.click('[data-nav="system"]'); await page.focus('.skip-link'); await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.activeElement?.id === 'portal-content');
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

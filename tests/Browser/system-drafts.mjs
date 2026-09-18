// System role selection and draft-lineage UI test against a LOCAL STUB ONLY.
//
// Serves the real wwwroot/js/system.js and page-common.js behind a minimal
// harness that mirrors System.razor, and stubs the configuration APIs so no
// .NET process, provider, or container is involved. It proves the role-select
// retention and draft semantics that the deleted organization-ui.mjs covered
// for the old single-file organization.js:
//   1. selected role is tracked independently of the <select> options, so an
//      unrelated organization reload keeps the selected role and its draft cue
//   2. switching roles renders the newly selected role's own draft
//   3. an authoritative change to a role marks exactly that role's draft as a
//      conflict (aria-invalid + visible reset) without replacing the draft
//   4. reset adopts the new authoritative value and clears the cue
//   5. a successful role save returns to that role's authority, clean
//   6. a selected role that disappears falls back to the first authoritative role
//   7. a failed organization load reports an error status
//
// Usage: node tests/Browser/system-drafts.mjs
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const SYSTEM_JS = fileURLToPath(new URL('../../src/HVO.AgentControl/wwwroot/js/system.js', import.meta.url));
const COMMON_JS = fileURLToPath(new URL('../../src/HVO.AgentControl/wwwroot/js/page-common.js', import.meta.url));
const PORTAL_CSS = fileURLToPath(new URL('../../src/HVO.AgentControl/wwwroot/css/portal.css', import.meta.url));
const CHROME = process.env.CHROME_PATH || undefined;

const role = (id, displayName, standingInstructions, revision = 1) => ({
  id, departmentId: 'dept-ops', slug: `${id}-slug`, displayName,
  instructionProfile: `role/${id}@1`, permissionProfile: `policy/${id}@1`,
  standingInstructions, revision,
});

const overview = (revision, displayName = 'Stub organization', basicInstructions = `org instructions ${revision}`, roles = null) => ({
  id: 'org-1', slug: 'stub', displayName, description: 'Authoritative stub', basicInstructions, revision,
  roles: roles || [role('role-a', 'Role A', `A standing ${revision}`, revision), role('role-b', 'Role B', `B standing ${revision}`, revision)],
});

const state = {
  organization: overview(1),
  organizationPlans: [],
  mutationPlans: [],
  mutations: [],
};

const readBody = (req) => new Promise((resolve) => { let body = ''; req.on('data', (chunk) => { body += chunk; }); req.on('end', () => resolve(body)); });
const send = (res, status, body, type = 'application/json') => { if (res.destroyed) return; res.statusCode = status; res.setHeader('Content-Type', type); res.end(type.includes('json') ? JSON.stringify(body) : body); };

const HARNESS = `<!doctype html><html lang="en"><head><meta charset="utf-8">
<link rel="stylesheet" href="/css/portal.css"></head><body>
<section class="page" data-page="system" data-system-page>
  <p class="page-status" data-page-status role="status">Loading configuration…</p>
  <div class="system-forms" data-system-forms hidden>
    <form class="panel-form" data-org-name-form><label for="org-name">Company display name<input id="org-name" data-org-name-input data-system-control aria-describedby="org-name-draft-state" required maxlength="128" disabled /></label><p id="org-name-draft-state" class="draft-state-cue" data-org-name-draft-state hidden></p><div class="draft-actions"><button class="btn" type="submit" data-system-control disabled>Save display name</button><button class="text-button" type="button" data-reset-org-name hidden>Discard draft</button></div></form>
    <form class="panel-form" data-org-instructions-form><label for="org-instructions">Organization basic instructions<textarea id="org-instructions" data-org-instructions data-system-control aria-describedby="org-instructions-draft-state" rows="7" required disabled></textarea></label><p id="org-instructions-draft-state" class="draft-state-cue" data-org-instructions-draft-state hidden></p><div class="draft-actions"><button class="btn" type="submit" data-system-control disabled>Save basic instructions</button><button class="text-button" type="button" data-reset-org-instructions hidden>Discard draft</button></div></form>
    <form class="panel-form" data-role-instructions-form><label for="role-select">Authoritative role<select id="role-select" data-role-select data-system-control disabled></select></label><label for="role-instructions">Selected role standing instructions<textarea id="role-instructions" data-role-instructions data-system-control aria-describedby="role-instructions-draft-state" rows="7" required disabled></textarea></label><p id="role-instructions-draft-state" class="draft-state-cue" data-role-instructions-draft-state hidden></p><div class="draft-actions"><button class="btn" type="submit" data-system-control disabled>Save role instructions</button><button class="text-button" type="button" data-reset-role-instructions hidden>Discard draft</button></div></form>
  </div>
  <p class="receipt" data-config-receipt role="status" aria-live="polite"></p>
</section>
<script type="module" src="/js/system.js"></script></body></html>`;

const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname === '/') return send(res, 200, HARNESS, 'text/html; charset=utf-8');
  if (url.pathname === '/js/system.js') return send(res, 200, readFileSync(SYSTEM_JS, 'utf8'), 'text/javascript; charset=utf-8');
  if (url.pathname === '/js/page-common.js') return send(res, 200, readFileSync(COMMON_JS, 'utf8'), 'text/javascript; charset=utf-8');
  if (url.pathname === '/css/portal.css') return send(res, 200, readFileSync(PORTAL_CSS, 'utf8'), 'text/css; charset=utf-8');
  if (url.pathname === '/api/organization/portal' && req.method === 'GET') {
    const plan = state.organizationPlans.shift();
    if (plan) {
      if (plan.status && plan.status >= 400) return send(res, plan.status, plan.body || { title: 'unavailable' });
      if (plan.organization) state.organization = plan.organization;
      return send(res, 200, plan.organization || state.organization);
    }
    return send(res, 200, state.organization);
  }
  if (url.pathname === '/__stub' && req.method === 'POST') {
    const patch = JSON.parse((await readBody(req)) || '{}');
    if (patch.organization) state.organization = patch.organization;
    if (patch.organizationPlans) state.organizationPlans.push(...patch.organizationPlans);
    if (patch.mutationPlans) state.mutationPlans.push(...patch.mutationPlans);
    if (patch.resetMutations) state.mutations.length = 0;
    return send(res, 200, { ok: true });
  }
  if (['PATCH', 'PUT', 'POST'].includes(req.method)) {
    const body = JSON.parse((await readBody(req)) || '{}');
    state.mutations.push({ path: url.pathname, body });
    const plan = state.mutationPlans.shift();
    if (plan) {
      if (plan.organization) state.organization = plan.organization;
      return send(res, plan.status || 200, plan.body || plan.organization || {});
    }
    return send(res, 200, state.organization);
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
const value = (selector) => page.$eval(selector, (el) => el.value);
const options = (select) => page.$$eval(`${select} option`, (nodes) => nodes.map((node) => ({ value: node.value, text: node.textContent })));
const draftState = (selector) => page.getAttribute(selector, 'data-draft-state');

try {
  await page.goto(base, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-system-forms]:not([hidden])');

  // ---- 1. initial role selection ---------------------------------------
  record('all authoritative roles are selectable and the first is selected',
    JSON.stringify(await options('[data-role-select]')) === JSON.stringify([{ value: 'role-a', text: 'Role A' }, { value: 'role-b', text: 'Role B' }])
      && (await value('[data-role-select]')) === 'role-a'
      && (await value('[data-role-instructions]')) === 'A standing 1',
    { options: await options('[data-role-select]') });

  // ---- 2. switch to role B and draft -----------------------------------
  await page.selectOption('[data-role-select]', 'role-b');
  await page.fill('[data-role-instructions]', 'role B draft');
  record('switching roles renders the selected role and marks its draft dirty and resettable',
    (await value('[data-role-instructions]')) === 'role B draft'
      && (await draftState('[data-role-instructions]')) === 'dirty'
      && await page.isVisible('[data-role-instructions-draft-state]')
      && await page.isVisible('[data-reset-role-instructions]'),
    { draftState: await draftState('[data-role-instructions]') });

  // ---- 3. unrelated organization save/reload retains role B draft -------
  // Only the organization revision advances; the role definitions are unchanged
  // so the role B draft must stay dirty, not become a conflict.
  await stub({ resetMutations: true, organizationPlans: [{ organization: overview(2, 'Renamed organization', 'org instructions 1', [role('role-a', 'Role A', 'A standing 1', 1), role('role-b', 'Role B', 'B standing 1', 1)]) }], mutationPlans: [{ status: 200, body: { revision: 2 } }] });
  await page.fill('[data-org-name-input]', 'new organization name');
  await page.click('[data-org-name-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('revision 2'));
  record('unrelated organization save/reload retains the selected role and its draft cue',
    (await value('[data-role-select]')) === 'role-b'
      && (await value('[data-role-instructions]')) === 'role B draft'
      && (await draftState('[data-role-instructions]')) === 'dirty'
      && await page.isVisible('[data-role-instructions-draft-state]')
      && await page.isVisible('[data-reset-role-instructions]'),
    { selected: await value('[data-role-select]'), draftState: await draftState('[data-role-instructions]') });

  // ---- 4. authoritative change to role B conflicts only role B ----------
  // The role's revision advances with the injected authority while the draft
  // keeps its original base revision, so the next submit is rejected 409.
  await stub({ resetMutations: true, mutationPlans: [{ status: 409, body: { title: 'conflict' } }] });
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('Update failed'));
  record('role draft retains its frozen base revision and conflict cue after a 409',
    state.mutations[0]?.path === '/api/roles/role-b/instructions'
      && state.mutations[0]?.body.revision === 1
      && state.mutations[0]?.body.standingInstructions === 'role B draft'
      && (await value('[data-role-instructions]')) === 'role B draft'
      && (await draftState('[data-role-instructions]')) === 'conflict'
      && (await page.getAttribute('[data-role-instructions]', 'aria-invalid')) === 'true'
      && await page.isVisible('[data-reset-role-instructions]'),
    { mutation: state.mutations[0], draftState: await draftState('[data-role-instructions]') });

  // ---- 5. reset adopts the authoritative value -------------------------
  await page.click('[data-reset-role-instructions]');
  record('resetting a conflicted role draft adopts the current authority and clears the cue',
    (await value('[data-role-instructions]')) === 'B standing 1'
      && (await draftState('[data-role-instructions]')) === 'clean'
      && await page.locator('[data-role-instructions-draft-state]').isHidden()
      && await page.locator('[data-reset-role-instructions]').isHidden(),
    { value: await value('[data-role-instructions]') });

  // ---- 6. successful role save returns to the same role, clean ----------
  await page.fill('[data-role-instructions]', 'role B saved');
  await stub({ resetMutations: true, organizationPlans: [{ organization: { id: 'org-1', slug: 'stub', displayName: 'Renamed organization', description: 'Authoritative stub', basicInstructions: 'org instructions 1', revision: 3, roles: [{ id: 'role-a', departmentId: 'dept-ops', slug: 'role-a-slug', displayName: 'Role A', instructionProfile: 'role/role-a@1', permissionProfile: 'policy/role-a@1', standingInstructions: 'A standing 3', revision: 3 }, { id: 'role-b', departmentId: 'dept-ops', slug: 'role-b-slug', displayName: 'Role B', instructionProfile: 'role/role-b@1', permissionProfile: 'policy/role-b@1', standingInstructions: 'role B saved', revision: 4 }] } }], mutationPlans: [{ status: 200, body: {} }] });
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('Saved'));
  record('saving role B returns to role B authority and a clean draft',
    state.mutations[0]?.path === '/api/roles/role-b/instructions'
      && (await value('[data-role-select]')) === 'role-b'
      && (await value('[data-role-instructions]')) === 'role B saved'
      && (await draftState('[data-role-instructions]')) === 'clean'
      && await page.locator('[data-reset-role-instructions]').isHidden(),
    { mutation: state.mutations[0], selected: await value('[data-role-select]') });

  // ---- 7. a vanished selected role falls back to the first --------------
  await stub({ organizationPlans: [{ organization: { id: 'org-1', slug: 'stub', displayName: 'Renamed organization', description: 'Authoritative stub', basicInstructions: 'org instructions 1', revision: 4, roles: [role('role-a', 'Role A', 'A standing 4', 4), role('role-c', 'Role C', 'C standing 4', 4)] } }] });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-system-forms]:not([hidden])');
  record('a selected role that disappears from authority falls back to the first role',
    (await value('[data-role-select]')) === 'role-a'
      && (await value('[data-role-instructions]')) === 'A standing 4',
    { selected: await value('[data-role-select]') });

  // ---- 7b. failed save, then a successful retry reports an ok receipt ---
  await page.fill('[data-role-instructions]', 'role A retry');
  await stub({ resetMutations: true, mutationPlans: [{ status: 500, body: { title: 'stub server error' } }] });
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]').textContent.includes('Update failed'));
  record('a failed save reports an error receipt and keeps the dirty draft',
    (await page.getAttribute('[data-config-receipt]', 'data-status')) === 'error'
      && (await value('[data-role-instructions]')) === 'role A retry'
      && (await draftState('[data-role-instructions]')) === 'dirty'
      && state.mutations[0]?.path === '/api/roles/role-a/instructions',
    { receipt: await page.locator('[data-config-receipt]').innerText(), status: await page.getAttribute('[data-config-receipt]', 'data-status') });

  const authorityAfterRetry = { id: 'org-1', slug: 'stub', displayName: 'Renamed organization', description: 'Authoritative stub', basicInstructions: 'org instructions 1', revision: 5, roles: [role('role-a', 'Role A', 'role A retry', 5), role('role-c', 'Role C', 'C standing 4', 4)] };
  await stub({ resetMutations: true, organizationPlans: [{ organization: authorityAfterRetry }], mutationPlans: [{ status: 200, body: {} }] });
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('revision 5'));
  record('a successful save after a failed update clears the error receipt to ok',
    (await page.getAttribute('[data-config-receipt]', 'data-status')) === 'ok'
      && (await value('[data-role-instructions]')) === 'role A retry'
      && (await draftState('[data-role-instructions]')) === 'clean',
    { receipt: await page.locator('[data-config-receipt]').innerText(), status: await page.getAttribute('[data-config-receipt]', 'data-status') });

  // ---- 7c. a failed authority reload after a save recovers on retry -----
  await page.fill('[data-role-instructions]', 'role A reload retry');
  await stub({ resetMutations: true, organizationPlans: [{ status: 503, body: { title: 'reload unavailable' } }], mutationPlans: [{ status: 200, body: {} }] });
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('unavailable'));
  record('a save whose authority reload fails surfaces an error page status, not a stale error',
    (await page.getAttribute('[data-page-status]', 'data-status')) === 'error'
      && (await page.getAttribute('[data-config-receipt]', 'data-status')) === 'error'
      && (await page.locator('[data-config-receipt]').innerText()).includes('Saved, but reloading authority failed'),
    { pageStatus: await page.locator('[data-page-status]').innerText(), receipt: await page.locator('[data-config-receipt]').innerText() });

  const authorityRecovered = { id: 'org-1', slug: 'stub', displayName: 'Renamed organization', description: 'Authoritative stub', basicInstructions: 'org instructions 1', revision: 6, roles: [role('role-a', 'Role A', 'role A reload retry', 6), role('role-c', 'Role C', 'C standing 4', 4)] };
  await page.fill('[data-role-instructions]', 'role A reload retry');
  await stub({ resetMutations: true, organizationPlans: [{ organization: authorityRecovered }], mutationPlans: [{ status: 200, body: {} }] });
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('revision 6'));
  record('a successful authority reload after a load failure clears the error page status to ok',
    (await page.getAttribute('[data-page-status]', 'data-status')) === 'ok'
      && (await page.getAttribute('[data-config-receipt]', 'data-status')) === 'ok'
      && (await value('[data-role-instructions]')) === 'role A reload retry',
    { pageStatus: await page.locator('[data-page-status]').innerText(), receipt: await page.locator('[data-config-receipt]').innerText() });

  // ---- 8. organization load failure reports an error status -------------
  await stub({ organizationPlans: [{ status: 503, body: { title: 'unavailable' } }] });
  await page.goto(base, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('unavailable'));
  record('a failed organization load reports a visible error status',
    (await page.getAttribute('[data-page-status]', 'data-status')) === 'error'
      && await page.locator('[data-system-forms]').isHidden(),
    { status: await page.locator('[data-page-status]').innerText() });

  record('no uncaught page errors during the system draft suite', pageErrors.length === 0, { pageErrors });
} catch (error) {
  record('system draft suite completed without fatal error', false, { fatal: String(error && error.stack ? error.stack : error) });
} finally {
  await browser.close();
  server.close();
}

const failed = results.filter((result) => result.passed === false).length;
console.log('---');
console.log(`RESULT: ${results.length - failed}/${results.length} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);

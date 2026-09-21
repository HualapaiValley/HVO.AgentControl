// Hermetic enabled-runtime routed owner portal check. No provider or remote host.
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { chmodSync, copyFileSync, createWriteStream, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const ROOT = process.env.REPO_ROOT || fileURLToPath(new URL('../../', import.meta.url));
const PROJECT_DIR = join(ROOT, 'src', 'HVO.AgentControl');
const DLL = process.env.APP_DLL || join(PROJECT_DIR, 'bin', 'Release', 'net10.0', 'HVO.AgentControl.dll');
const FAKE_ACP = join(ROOT, 'tests', 'HVO.AgentControl.Tests', 'Fixtures', 'fake_acp.py');
const OUT = process.env.ARTIFACTS_DIR || join(ROOT, 'artifacts', 'browser-organization');
const PASSWORD = 'organization-browser-owner-password-0000';
const BASIC_AUTH = 'Basic ' + Buffer.from(`owner:${PASSWORD}`, 'utf8').toString('base64');
const results = [];
const record = (name, passed, detail = {}) => { results.push({ name, passed: !!passed, detail }); console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${Object.keys(detail).length ? ' :: ' + JSON.stringify(detail) : ''}`); };
const freePort = () => new Promise((resolve, reject) => { const server = createServer(); server.once('error', reject); server.listen(0, '127.0.0.1', () => { const port = server.address().port; server.close(() => resolve(port)); }); });
async function waitFor(base, timeout = 90000) { const deadline = Date.now() + timeout; while (Date.now() < deadline) { try { if ((await fetch(`${base}/health/live`)).ok) return true; } catch {} await sleep(200); } return false; }
async function waitForOrganization(base, timeout = 90000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const remaining = deadline - Date.now();
    try {
      // Bound each fetch too: an accepted connection that never returns headers
      // must not outlive the readiness deadline.
      const response = await fetch(`${base}/api/organization/portal`, {
        headers: { Authorization: BASIC_AUTH, Accept: 'application/json' },
        signal: AbortSignal.timeout(Math.max(1, Math.min(5000, remaining))),
      });
      if (response.ok) return true;
    } catch {}
    await sleep(Math.min(200, Math.max(0, deadline - Date.now())));
  }
  return false;
}
const departmentIdFromHref = (href, base) => new URL(href, base).searchParams.get('departmentId');

mkdirSync(OUT, { recursive: true });
let child; let browser; let fatal;
try {
  if (!existsSync(DLL)) throw new Error(`Build first: ${DLL}`);
  const port = await freePort(); const base = `http://127.0.0.1:${port}`;
  const runtime = join(tmpdir(), `agentcontrol-routes-${randomBytes(8).toString('hex')}`);
  mkdirSync(join(runtime, 'data'), { recursive: true }); mkdirSync(join(runtime, 'private'), { recursive: true });
  const passwordPath = join(runtime, 'owner-password'); writeFileSync(passwordPath, PASSWORD);
  const fake = join(runtime, 'fake_acp.py'); copyFileSync(FAKE_ACP, fake); chmodSync(fake, 0o755); writeFileSync(join(runtime, 'scenario'), 'prompt_fast');
  const env = { ...process.env }; for (const key of Object.keys(env)) if (/^Control__/i.test(key)) delete env[key];
  Object.assign(env, { Control__Enabled: 'true', Control__DataDirectory: join(runtime, 'data'), Control__PrivateDataDirectory: join(runtime, 'private'), Control__OpenCodeExecutable: fake, Control__OwnerPasswordFile: passwordPath, Control__EnableTerminal: 'false', Control__NativePort: String(port + 1), ASPNETCORE_URLS: base, ASPNETCORE_ENVIRONMENT: 'Development' });
  const log = createWriteStream(join(OUT, 'app.log')); child = spawn('dotnet', [DLL], { cwd: PROJECT_DIR, env, stdio: ['ignore', 'pipe', 'pipe'] }); child.stdout.pipe(log); child.stderr.pipe(log);
  const healthy = await waitFor(base); record('enabled process becomes live', healthy, { base }); if (!healthy) throw new Error('app did not start');
  // Liveness precedes organization-store adoption and ACP startup. Portal checks
  // need the authoritative store, so wait for the exact authenticated API they
  // consume rather than racing the asynchronous control-host initialization.
  const organizationReady = await waitForOrganization(base); record('enabled organization store becomes ready', organizationReady, { base }); if (!organizationReady) throw new Error('organization store did not become ready');
  browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, httpCredentials: { username: 'owner', password: PASSWORD } });
  const page = await context.newPage(); const pageErrors = []; page.on('pageerror', (error) => pageErrors.push(error.message));

  const routeCases = [
    ['/organization', 'organization', ['data-hire-form', 'data-terminal', 'data-system-forms', 'data-department-detail', 'data-organization-departments-page']],
    ['/organization/departments', 'organization-departments', ['data-hire-form', 'data-terminal', 'data-system-forms', 'data-department-detail', 'data-employee-directory']],
    ['/employees', 'employees', ['data-hire-form', 'data-terminal', 'data-system-forms', 'data-department-detail', 'data-organization-page']],
    ['/hiring', 'hiring', ['data-terminal', 'data-system-forms', 'data-employee-directory', 'data-department-detail', 'data-profile-form']],
    ['/profiles', 'profiles', ['data-hire-form', 'data-terminal', 'data-system-forms', 'data-employee-directory', 'data-department-detail']],
    ['/system', 'system', ['data-hire-form', 'data-terminal', 'data-employee-directory', 'data-department-detail', 'data-profile-form']],
  ];
  for (const [path, marker, absent] of routeCases) {
    const response = await page.goto(`${base}${path}`, { waitUntil: 'domcontentloaded' });
    const html = await page.content();
    record(`${path} returns its routed SSR page`, response.status() === 200 && html.includes(`data-page=\"${marker}\"`), { status: response.status() });
    record(`${path} has persistent active navigation`, await page.locator('.primary-nav a.active').count() === 1);
    const aria = await page.evaluate(() => {
      const current = [...document.querySelectorAll('.primary-nav a[aria-current="page"]')];
      return { count: current.length, text: current.map((node) => node.textContent.trim()), matchesActive: current.every((node) => node.classList.contains('active')) };
    });
    record(`${path} sets aria-current=page on its single active link`,
      aria.count === 1 && aria.matchesActive, aria);
    record(`${path} omits other page DOM`, absent.every((value) => !html.includes(value)), { absent });
  }

  await page.goto(`${base}/organization`); await page.waitForSelector('[data-org-department-cards]:not([hidden])');
  record('organization renders dashboard cards and failures', await page.locator('.summary-card').count() === 3 && await page.locator('[data-organization-failures]').isVisible());

  // Organization nav is a grouped parent with Overview/Departments children and
  // the correct parent active state on both child routes.
  const groupLinks = await page.locator('[data-nav-group="organization"] .nav-sublinks a').allTextContents();
  record('organization nav is a grouped parent with Overview and Departments', groupLinks.join('|') === 'Overview|Departments', { groupLinks });
  record('organization group is active on overview', await page.getAttribute('[data-nav-group="organization"]', 'data-nav-group-active') === 'true');
  await page.goto(`${base}/organization/departments`); await page.waitForSelector('[data-department-directory]:not([hidden])');
  record('organization group is active on departments', await page.getAttribute('[data-nav-group="organization"]', 'data-nav-group-active') === 'true');
  record('departments child link is the single active link', await page.locator('.primary-nav a.active').innerText() === 'Departments');
  await page.goto(`${base}/employees`); await page.waitForSelector('[data-employee-directory]:not([hidden])');
  record('organization group is inactive outside organization routes', await page.getAttribute('[data-nav-group="organization"]', 'data-nav-group-active') === 'false');

  // Overview: authoritative department cards link by stable id.
  await page.goto(`${base}/organization`); await page.waitForSelector('[data-org-department-cards]:not([hidden])');
  const cards = await page.$$eval('[data-org-department-cards] .department-card', (nodes) => nodes.map((node) => ({ id: node.dataset.departmentId, slug: node.dataset.departmentSlug, href: node.getAttribute('href') })));
  record('overview renders exactly the three authoritative department cards', cards.length === 3 && cards.map((card) => card.slug).join(',') === 'development,operations,qa', { cards });
  record('overview department cards link by stable department id', cards.every((card) => card.id?.startsWith('dept-') && card.href === `/organization/departments/${card.id}`), { cards });
  const bySlug = Object.fromEntries(cards.map((card) => [card.slug, card.id]));
  const operationsId = bySlug.operations; const developmentId = bySlug.development; const qaId = bySlug.qa;

  // Department detail route is distinct DOM and resolves the exact department.
  const detailPath = `/organization/departments/${operationsId}`;
  const detailResponse = await page.goto(`${base}${detailPath}`, { waitUntil: 'domcontentloaded' });
  const detailHtml = await page.content();
  record('department detail returns its routed SSR page', detailResponse.status() === 200 && detailHtml.includes('data-page="department-detail"') && detailHtml.includes(`data-department-id="${operationsId}"`), { status: detailResponse.status() });
  record('department detail omits other page DOM', ['data-employee-directory', 'data-hire-form', 'data-system-forms'].every((value) => !detailHtml.includes(value)));
  record('department detail has persistent active navigation', await page.locator('.primary-nav a.active').innerText() === 'Departments');
  await page.waitForSelector('[data-department-content]:not([hidden])');

  // Directory: exactly the authoritative departments, in authoritative order.
  await page.goto(`${base}/organization/departments`); await page.waitForSelector('[data-department-directory]:not([hidden])');
  const directory = await page.$$eval('[data-department-directory] .department-directory-card', (nodes) => nodes.map((node) => ({ id: node.dataset.departmentId, slug: node.dataset.departmentSlug })));
  record('department directory is exactly the authoritative hierarchy', directory.length === 3 && directory.map((item) => item.slug).join(',') === 'development,operations,qa' && !directory.some((item) => item.slug === 'finance'), { directory });
  const directoryStyle = await page.locator('[data-department-directory]').evaluate((node) => {
    const style = getComputedStyle(node);
    return { listStyleType: style.listStyleType, padding: style.padding, display: style.display, className: node.className };
  });
  record('department directory is an unstyled grid list with the directory-list class',
    directoryStyle.className.includes('directory-list') && directoryStyle.listStyleType === 'none'
      && directoryStyle.padding === '0px' && directoryStyle.display === 'grid',
    directoryStyle);

  // Operations detail: authoritative identity, role, roster and CTA.
  await page.goto(`${base}${detailPath}`); await page.waitForSelector('[data-department-content]:not([hidden])');
  const operationsName = await page.locator('[data-department-name]').innerText();
  const identity = await page.locator('[data-department-identity]').innerText();
  const roleSummaries = await page.locator('[data-department-roles] .role-summary').count();
  const roleText = await page.locator('[data-department-roles]').innerText();
  const roster = await page.locator('[data-department-roster] .department-roster-card').count();
  const rosterLink = await page.locator('[data-department-roster] .department-roster-card a').first().getAttribute('href');
  const rosterBadge = await page.locator('[data-department-roster] .availability-pill').first().getAttribute('data-availability');
  const hireHref = await page.getAttribute('[data-department-hire]', 'href');
  record('operations detail shows authoritative identity and revision', operationsName === 'Operations' && identity.includes(operationsId) && identity.includes('operations') && /revision/i.test(identity), { operationsName, identity });
  record('operations detail lists its single authoritative role', roleSummaries === 1 && roleText.includes('Operations / IT'), { roleSummaries });
  record('operations detail roster has one employee with an availability badge and stable link', roster === 1 && rosterLink?.startsWith('/employees/') && Boolean(rosterBadge), { roster, rosterLink, rosterBadge });
  record('operations detail CTA targets hiring with the exact department id', departmentIdFromHref(hireHref, base) === operationsId, { hireHref });
  record('operations detail reports availability counts for the department', await page.locator('[data-department-availability] dt').count() >= 1 && (await page.locator('[data-department-availability]').innerText()).trim().length > 0);

  // Development and QA are real empty departments with their own request CTA.
  for (const [slug, id] of [['development', developmentId], ['qa', qaId]]) {
    await page.goto(`${base}/organization/departments/${id}`); await page.waitForSelector('[data-department-content]:not([hidden])');
    const emptyVisible = await page.locator('[data-department-empty]').isVisible();
    const emptyRoster = await page.locator('[data-department-roster] .department-roster-card').count();
    const emptyHire = await page.getAttribute('[data-department-hire]', 'href');
    record(`${slug} empty department renders an empty state and request CTA`, emptyVisible && emptyRoster === 0 && departmentIdFromHref(emptyHire, base) === id, { id, emptyHire });
  }

  // A legacy mutable-slug hash on the overview resolves to the stable department id.
  await page.goto(`${base}/organization#qa`);
  await page.waitForURL(`**/organization/departments/${qaId}`);
  record('legacy department hash resolves to the stable department id after data load', page.url().endsWith(`/organization/departments/${qaId}`), { url: page.url() });

  // Invalid and unknown ids fail safely in-page with no exception.
  const invalid = await page.goto(`${base}/organization/departments/dept-`);
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('Invalid department id'));
  record('invalid department id fails safely in-page', invalid.status() === 200 && await page.locator('[data-department-content]').isHidden() && (await page.locator('[data-page-status]').innerText()).includes('Invalid department id'));
  const unknown = await page.goto(`${base}/organization/departments/dept-does-not-exist`);
  await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('not found'));
  record('unknown department id fails safely in-page', unknown.status() === 200 && await page.locator('[data-department-content]').isHidden() && (await page.locator('[data-page-status]').innerText()).includes('not found'));

  // Hiring query preselects only an authoritative department and filters roles.
  await page.goto(`${base}/hiring?departmentId=${developmentId}`); await page.waitForSelector('[data-hire-requests]:not([hidden])');
  record('hiring preselects the exact requested department', await page.inputValue('#hire-department') === developmentId, { developmentId });
  record('hiring explains zero roles and disables submission for Development', await page.locator('#hire-role option').count() === 0 && await page.locator('#hire-role').isDisabled() && await page.locator('[data-hire-submit]').isDisabled() && await page.locator('[data-no-hire-roles]').isVisible());
  await page.goto(`${base}/hiring?departmentId=${qaId}`); await page.waitForSelector('[data-hire-requests]:not([hidden])');
  record('hiring explains zero roles and disables submission for QA', await page.inputValue('#hire-department') === qaId && await page.locator('#hire-role option').count() === 0 && await page.locator('[data-hire-submit]').isDisabled() && await page.locator('[data-no-hire-roles]').isVisible());
  await page.goto(`${base}/hiring?departmentId=${operationsId}`); await page.waitForSelector('[data-hire-requests]:not([hidden])');
  const selectedDepartment = await page.inputValue('#hire-department');
  const roleLabels = await page.locator('#hire-role option').allTextContents();
  record('hiring preselects Operations, lists its roles, and re-enables submission', selectedDepartment === operationsId && roleLabels.join('|') === 'Operations / IT' && !(await page.locator('[data-hire-submit]').isDisabled()) && await page.locator('[data-no-hire-roles]').isHidden(), { selectedDepartment, roleLabels });
  await page.goto(`${base}/hiring?departmentId=dept-forged`); await page.waitForSelector('[data-hire-requests]:not([hidden])');
  record('hiring ignores a forged department query and never trusts it', await page.inputValue('#hire-department') === operationsId, { forged: true });

  // Employee directory and exact detail remain routed and safe.
  await page.goto(`${base}/employees?department=operations`); await page.waitForSelector('[data-employee-directory]:not([hidden])');
  const links = await page.locator('[data-employee-directory] a').count(); record('directory lists employees with stable detail links', links === 1 && (await page.locator('[data-employee-directory] a').first().getAttribute('href')).startsWith('/employees/'), { links });
  await page.fill('[data-employee-search]', 'no matching employee'); record('directory search has an accessible empty state', (await page.locator('[data-employee-directory]').innerText()).includes('No employees match'));
  const directoryUrl = page.url();
  await page.press('[data-employee-search]', 'Enter');
  record('pressing Enter in employee filters preserves URL, state, and results', page.url() === directoryUrl && await page.inputValue('[data-employee-search]') === 'no matching employee' && (await page.locator('[data-employee-directory]').innerText()).includes('No employees match'), { directoryUrl, currentUrl: page.url() });
  await page.fill('[data-employee-search]', ''); const employeeDetailPath = await page.locator('[data-employee-directory] a').first().getAttribute('href');
  const routedEmployeeId = employeeDetailPath.split('/').pop(); let rebuildBody = null; let taskBody = null; let taskActionPaths = []; let holdBody = null;
  await page.route(`**/api/employees/${routedEmployeeId}`, async (route) => {
    const response = await route.fetch(); const body = await response.json();
    await route.fulfill({ response, json: { ...body, revision: 1, profileStatus: { currentProfileId: 'prof-browser', currentProfileDisplayName: 'Browser profile', currentProfileRevisionId: 'prev-browser-1', currentRevisionNumber: 1, currentImageDigest: `sha256:${'1'.repeat(64)}`, currentPlatform: 'linux/amd64', workerId: 'wrk-browser', hostId: 'local-docker', newerRevisionAvailable: true, newerRevisionId: 'prev-browser-2', newerRevisionNumber: 2, activeRebuildState: null, activeRebuildId: null } } });
  });
  await page.route(`**/api/employees/${routedEmployeeId}/rebuilds`, (route) => route.fulfill({ json: [{ id: 'reb-browser-applied', state: 'Applied', toProfileRevisionId: 'prev-browser-2', resetWorkspace: false, resetHome: false, failureSummary: null, updatedAt: '2026-09-19T00:00:00Z' }] }));
  await page.route(`**/api/employees/${routedEmployeeId}/rebuild`, async (route) => { rebuildBody = route.request().postDataJSON(); await route.fulfill({ json: { rebuild: { id: 'reb-browser-new', state: 'Applied' }, profileStatus: {} } }); });
  await page.route(`**/api/employees/${routedEmployeeId}/tasks`, async (route) => {
    if (route.request().method() === 'POST') { taskBody = route.request().postDataJSON(); return route.fulfill({ json: { task: { id: 'tsk-browser-new', state: 'Running', revision: 1, createdAt: '2026-09-19T00:00:00Z', updatedAt: '2026-09-19T00:00:00Z', modelReportJson: null, modelReportHash: null, failureDetail: null, workerId: 'wrk-browser' }, spec: {}, request: { id: 'req-browser-new', state: 'Forwarded', nativeSessionId: 'session-browser', ownershipEpoch: 1, processGeneration: 1 }, verification: null, displayState: 'running' } }); }
    return route.continue();
  });
  await page.route(`**/api/tasks/**`, async (route) => { taskActionPaths.push(new URL(route.request().url()).pathname); await route.fulfill({ json: { task: { id: 'tsk-browser', state: 'Completed', revision: 4 }, cancellation: { state: 'Forwarded' }, verification: { state: 'Passed' }, detail: 'remote-completed' } }); });
  await page.route(`**/api/employees/${routedEmployeeId}/dispatch-hold`, async (route) => { holdBody = route.request().postDataJSON(); await route.fulfill({ json: { employee: {}, status: { state: 'delivered' } } }); });
  await page.route(`**/api/employees/${routedEmployeeId}/orientation/deliver`, (route) => route.fulfill({ json: { status: { state: 'delivered' } } }));
  await page.route(`**/api/employees/${routedEmployeeId}/orientation/comprehension/run`, (route) => route.fulfill({ json: { status: { state: 'comprehended' } } }));
  await page.goto(`${base}${employeeDetailPath}`); await page.waitForSelector('[data-employee-content]:not([hidden])');
  const employeeId = await page.locator('[data-employee-detail]').getAttribute('data-employee-id');
  record('detail loads exact URL employee and terminal module', await page.locator('[data-selected-employee-name]').first().innerText() !== '—' && await page.locator('[data-terminal]').count() === 1, { employeeId });
  record('detail selection event targets exact employee', await page.locator('[data-portal]').getAttribute('data-selected-employee-id') === employeeId);
  record('employee detail reports a newer revision without acting', (await page.locator('[data-profile-update-note]').innerText()).includes('will not be adopted automatically') && rebuildBody === null);
  record('employee detail renders Applied rebuild history', (await page.locator('[data-rebuild-history]').innerText()).includes('Applied'));
  await page.check('[data-reset-home]');
  record('employee rebuild reset stays disabled without exact confirmation', await page.locator('[data-rebuild-submit]').isDisabled() && (await page.locator('[data-reset-confirmation-phrase]').innerText()) === 'reset-home');
  await page.fill('[data-reset-confirmation]', 'reset-home'); await page.click('[data-rebuild-submit]');
  await page.waitForFunction(() => document.querySelector('[data-rebuild-receipt]').dataset.status === 'ok');
  record('employee rebuild sends expected employee revision and target revision', rebuildBody?.expectedRevision === 1 && rebuildBody?.targetProfileRevisionId === 'prev-browser-2' && rebuildBody?.resetConfirmation === 'reset-home', rebuildBody || {});

  // Enabled-fixture terminal surface: exact route assets, restored mount class,
  // CSS geometry that fills the stage, focus affordance, and live regions.
  const terminalRoles = await page.evaluate(() => {
    const stage = document.querySelector('.terminal-stage').getBoundingClientRect();
    const mount = document.querySelector('#terminal.terminal-mount').getBoundingClientRect();
    const controls = document.querySelector('.controls');
    const alert = document.querySelector('[data-field="error-banner"]');
    return {
      stage: { width: Math.round(stage.width), height: Math.round(stage.height) },
      mount: { width: Math.round(mount.width), height: Math.round(mount.height), top: Math.round(mount.top - stage.top), left: Math.round(mount.left - stage.left) },
      controlsRole: controls?.getAttribute('role'),
      controlsLabel: controls?.getAttribute('aria-label'),
      alertRole: alert?.getAttribute('role'),
      modelReceiptLive: document.querySelector('[data-model-receipt]')?.getAttribute('aria-live'),
      connectionLive: document.querySelector('[data-field="connection"]')?.getAttribute('aria-live'),
      overlayLive: document.querySelector('[data-field="terminal-overlay"]')?.getAttribute('aria-live'),
    };
  });
  record('terminal mount is the restored .terminal-mount element inside .terminal-stage',
    terminalRoles.mount.width > 0 && terminalRoles.mount.height > 0
      && Math.abs(terminalRoles.mount.width - terminalRoles.stage.width) <= 1
      && Math.abs(terminalRoles.mount.height - terminalRoles.stage.height) <= 1
      && Math.abs(terminalRoles.mount.top) <= 1 && Math.abs(terminalRoles.mount.left) <= 1,
    terminalRoles);
  record('terminal controls expose role=group with an accessible label',
    terminalRoles.controlsRole === 'group' && terminalRoles.controlsLabel === 'Terminal controls', terminalRoles);
  record('terminal error banner is a role=alert live surface',
    terminalRoles.alertRole === 'alert', terminalRoles);
  record('model receipt, connection and overlay status are polite live regions',
    terminalRoles.modelReceiptLive === 'polite'
      && terminalRoles.connectionLive === 'polite'
      && terminalRoles.overlayLive === 'polite', terminalRoles);

  await page.route('**/api/control', async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, error: 'browser injected runtime fault' } });
  });
  await page.waitForFunction(() => {
    const banner = document.querySelector('[data-portal] [data-field="error-banner"]');
    return banner && !banner.hidden && banner.textContent.includes('browser injected runtime fault');
  });
  const faultGeometry = await page.evaluate(() => {
    const deck = document.querySelector('[data-portal]').getBoundingClientRect();
    const banner = document.querySelector('[data-portal] [data-field="error-banner"]').getBoundingClientRect();
    return { deck: { left: deck.left, right: deck.right, width: deck.width }, banner: { top: banner.top, left: banner.left, right: banner.right, width: banner.width }, viewportHeight: innerHeight };
  });
  record('control response fault is visible across the terminal deck within the viewport',
    await page.locator('[data-portal] [data-field="error-banner"]').isVisible()
      && (await page.locator('[data-portal] [data-field="error-banner"]').innerText()).includes('browser injected runtime fault')
      && faultGeometry.banner.top >= 0 && faultGeometry.banner.top < faultGeometry.viewportHeight
      && Math.abs(faultGeometry.banner.left - faultGeometry.deck.left) <= 1
      && Math.abs(faultGeometry.banner.right - faultGeometry.deck.right) <= 1,
    faultGeometry);
  await page.unroute('**/api/control');

  const authoritativeEmployee = await page.evaluate((id) => fetch(`/api/employees/${encodeURIComponent(id)}`, {
    credentials: 'same-origin', cache: 'no-store',
  }).then((response) => response.json()), employeeId);
  let remoteReads = 0;
  let hostControlPosts = 0;
  await page.route('**/api/control/**', async (route) => {
    if (route.request().method() === 'POST') hostControlPosts += 1;
    await route.continue();
  });
  await page.route(`**/api/employees/${employeeId}`, async (route) => {
    remoteReads += 1;
    const changed = true;
    await route.fulfill({
      contentType: 'application/json',
      json: {
        ...authoritativeEmployee,
        availability: changed ? 'reconciliation-required' : 'ready',
        runtime: {
          ...authoritativeEmployee.runtime,
          hostOwned: false,
          remoteOwned: true,
          nativeSessionId: changed ? 'remote-session-refreshed' : 'remote-session-initial',
          controlStatus: changed ? 'held' : 'authenticated',
          sessionState: changed ? 'stopped' : 'running',
          controlModel: null,
        },
        terminal: { ...authoritativeEmployee.terminal, available: false, url: null, reason: 'Remote fixture unavailable.' },
      },
    });
  });
  await page.reload();
  await page.waitForSelector('[data-employee-content]:not([hidden])');
  await page.waitForFunction(() => document.querySelector('[data-portal]')?.dataset.portalActive === 'true');
  // Both route modules are independent. Dispatch the authoritative employee
  // selection after the page content is ready so this fixture tests terminal
  // behavior, not ES-module evaluation timing.
  const remoteEmployee = {
    ...authoritativeEmployee,
    availability: 'reconciliation-required',
    runtime: {
      ...authoritativeEmployee.runtime,
      hostOwned: false,
      remoteOwned: true,
      nativeSessionId: 'remote-session-refreshed',
      controlStatus: 'held',
      sessionState: 'stopped',
      controlModel: null,
    },
    terminal: { ...authoritativeEmployee.terminal, available: false, url: null, reason: 'Remote fixture unavailable.' },
  };
  await page.evaluate((employee) => {
    const portal = document.querySelector('[data-portal]');
    portal.agentControlSelectedEmployee = employee;
    portal.dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: employee }));
  }, remoteEmployee);
  const firstRemoteSync = await page.locator('[data-field="syncedAt"]').innerText();
  const readyAvailability = await page.locator('[data-employee-availability]').evaluate((node) => ({ dataset: node.dataset.availability, className: node.className, color: getComputedStyle(node).color }));
  await page.waitForFunction(() => document.querySelector('[data-field="sessionId"]')?.textContent === 'remote-session-refreshed');
  const refreshedRemote = {
    state: await page.locator('[data-field="state-detail"]').innerText(),
    sessionId: await page.locator('[data-field="sessionId"]').innerText(),
    syncedAt: await page.locator('[data-field="syncedAt"]').innerText(),
  };
  record('remote employee telemetry renders the authoritative employee endpoint',
    remoteReads >= 1 && refreshedRemote.state.toLowerCase() === 'reconciliation required'
      && refreshedRemote.sessionId === 'remote-session-refreshed'
      && firstRemoteSync !== '—',
    { remoteReads, firstRemoteSync, refreshedRemote });
  const heldStateDetail = await page.locator('[data-field="state-detail"]').evaluate((node) => ({ tag: node.tagName, className: node.className, dataset: node.dataset.state, color: getComputedStyle(node).color }));
  record('employee availability carries the availability-pill class and authoritative dataset',
    readyAvailability.className.includes('availability-pill') && readyAvailability.dataset === 'reconciliation-required',
    readyAvailability);
  record('runtime state detail is a held state-pill distinct from the critical availability badge',
    heldStateDetail.tag === 'SPAN' && heldStateDetail.className.includes('state-pill')
      && heldStateDetail.dataset === 'held' && heldStateDetail.color !== readyAvailability.color,
    { heldStateDetail, availabilityColor: readyAvailability.color });
  const remoteModel = await page.locator('[data-model-select]').evaluate((node) => ({ disabled: node.disabled, text: node.options[0]?.textContent || '', count: node.options.length }));
  record('remote employee model control shows an explicit Unavailable placeholder and stays disabled',
    remoteModel.disabled && remoteModel.text === 'Unavailable', remoteModel);
  await page.evaluate(() => {
    document.querySelector('[data-action="interrupt"]')?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    const select = document.querySelector('[data-model-select]');
    const option = document.createElement('option');
    option.value = 'forged/remote-model';
    option.textContent = option.value;
    select.appendChild(option);
    select.value = option.value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });
  await sleep(100);
  record('programmatic remote interrupt and model events never POST host control endpoints',
    hostControlPosts === 0
      && (await page.locator('[data-field="receipt"]').innerText()).includes('remote turn cancellation is unavailable')
      && (await page.locator('[data-model-receipt]').innerText()).includes('remote employee'),
    { hostControlPosts });
  await page.unroute(`**/api/employees/${employeeId}`);
  await page.unroute('**/api/control/**');

  await page.locator('#terminal').focus();
  const focusRing = await page.evaluate(() => {
    const mount = document.querySelector('#terminal.terminal-mount');
    return { focused: document.activeElement === mount, boxShadow: getComputedStyle(mount).boxShadow };
  });
  record('terminal mount shows a focus affordance when the session is focused',
    focusRing.focused && /inset/.test(focusRing.boxShadow), focusRing);

  // Managed employee task, hold and orientation controls are routed, enabled for
  // a ready managed employee, and keep report and host verification separate.
  // This runs after the terminal fault-injection block so its own control fetches
  // cannot consume that block's injected fault. The managed runtime projection is
  // registered last (Playwright routes are LIFO) and removed afterwards so the
  // earlier host-owned projection still governs the terminal block.
  await page.route(`**/api/employees/${routedEmployeeId}`, async (route) => {
    const response = await route.fetch(); const body = await response.json();
    await route.fulfill({ response, json: { ...body, revision: 1, runtime: { ...body.runtime, hostOwned: false, remoteOwned: true, placement: 'DeveloperContainer', nativeSessionId: 'session-browser', controlStatus: 'authenticated', sessionState: 'running' }, orientation: { assignmentId: 'assign-browser', orientationVersion: 1, state: 'delivered', revision: 2, restartRequired: false, dispatchHeld: false, holdReasons: [], evidenceSource: 'owner', lastError: null }, recentTasks: [{ task: { id: 'tsk-browser', state: 'Completed', revision: 3, createdAt: '2026-09-19T00:00:00Z', updatedAt: '2026-09-19T00:02:00Z', modelReportJson: '{"summary":"bounded"}', modelReportHash: `sha256:${'a'.repeat(64)}`, failureDetail: null, workerId: 'wrk-browser' }, spec: { description: 'Bounded task', workspaceRoot: '/workspace/acceptance-220', allowedPaths: ['src'], allowedTools: ['read'], forbiddenActions: ['network egress'], maximumSeconds: 300, testRecipeId: 'dotnet-test-release' }, request: { id: 'req-browser', state: 'Completed', nativeSessionId: 'session-browser', ownershipEpoch: 1, processGeneration: 1, forwardedAt: '2026-09-19T00:00:30Z', completedAt: '2026-09-19T00:01:30Z' }, verification: { id: 'ver-browser', state: 'Passed', verifierVersion: 'workspace-task-verify-v1', manifestHash: `sha256:${'b'.repeat(64)}`, testSummaryHash: `sha256:${'c'.repeat(64)}`, deniedActionHash: null, failureDetail: null, verifiedAt: '2026-09-19T00:03:00Z', revision: 1 }, displayState: 'verified' }], profileStatus: { currentProfileId: 'prof-browser', currentProfileDisplayName: 'Browser profile', currentProfileRevisionId: 'prev-browser-1', currentRevisionNumber: 1, currentImageDigest: `sha256:${'1'.repeat(64)}`, currentPlatform: 'linux/amd64', workerId: 'wrk-browser', hostId: 'local-docker', newerRevisionAvailable: true, newerRevisionId: 'prev-browser-2', newerRevisionNumber: 2, activeRebuildState: null, activeRebuildId: null } } });
  });
  await page.goto(`${base}${employeeDetailPath}`); await page.waitForSelector('[data-employee-tasks]:not([hidden])');
  const heldTaskCard = page.locator('[data-task-list] .task-card').first();
  const taskCardText = await heldTaskCard.innerText();
  record('managed employee page renders the bounded task card with id, state and timestamps',
    taskCardText.includes('tsk-browser') && /Created/.test(taskCardText) && /updated/.test(taskCardText), { taskCardText });
  record('task card separates the unverified model report from the independent host verification',
    taskCardText.includes('Model-reported (unverified)') && taskCardText.includes('Host verification (independent)') && taskCardText.includes('Passed') && taskCardText.includes('workspace-task-verify-v1'), { taskCardText });
  record('task card keeps the cancellation-is-not-rollback help text', taskCardText.includes('never a rollback'));
  record('the task form is enabled for a managed ready employee and prefills the acceptance workspace',
    await page.inputValue('[data-task-workspace]') === '/workspace/acceptance-220',
    { workspace: await page.inputValue('[data-task-workspace]') });
  await page.fill('[data-task-description]', 'Bounded browser task');
  await page.fill('[data-task-paths]', 'src');
  await page.fill('[data-task-forbidden]', 'network egress');
  await page.check('[data-task-tool="read"]');
  await page.click('[data-task-submit]');
  await page.waitForFunction(() => document.querySelector('[data-task-receipt]').dataset.status === 'ok');
  record('task create sends the bounded spec, an idempotency key and the employee revision',
    taskBody?.expectedEmployeeRevision === 1 && typeof taskBody?.idempotencyKey === 'string' && taskBody.idempotencyKey.length > 0
      && taskBody?.taskSpec?.workspaceRoot === '/workspace/acceptance-220' && taskBody?.taskSpec?.testRecipeId === 'dotnet-test-release',
    taskBody || {});
  await page.click('[data-task-list] .task-card [data-task-action="sync"]');
  await page.waitForFunction(() => document.querySelector('[data-task-receipt]').textContent.includes('Synced'));
  record('task sync reaches the exact task sync route', taskActionPaths.some((path) => /\/api\/tasks\/[^/]+\/sync$/.test(path)), { taskActionPaths });
  await page.click('[data-orientation-hold]');
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').dataset.status === 'ok');
  record('manual hold writes the employee revision and never implies clearing other holds',
    holdBody?.expectedEmployeeRevision === 1 && typeof holdBody?.held === 'boolean'
      && (await page.locator('[data-hold-note]').innerText()).includes('never cleared here'),
    { holdBody, holdNote: await page.locator('[data-hold-note]').innerText() });
  await page.click('[data-orientation-deliver]');
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]').dataset.status === 'ok');
  record('orientation deliver uses the employee-scoped revision-bound route',
    (await page.locator('[data-orientation-receipt]').innerText()).includes('Saved. State: delivered'),
    { receipt: await page.locator('[data-orientation-receipt]').innerText() });
  await page.unroute(`**/api/employees/${routedEmployeeId}`);
  await page.unroute(`**/api/tasks/**`);

  // Delayed authoritative read: system forms stay hidden and every control
  // disabled until the response arrives, then become visible and enabled.
  await page.route('**/api/organization/portal', async (route) => { await sleep(1200); await route.continue(); });
  const systemPending = page.goto(`${base}/system`, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-system-page]');
  const systemPre = await page.evaluate(() => {
    const forms = document.querySelector('[data-system-forms]');
    const controls = [...document.querySelectorAll('[data-system-control]')];
    return { hidden: forms.hidden, display: getComputedStyle(forms).display, disabled: controls.length > 0 && controls.every((node) => node.disabled), count: controls.length };
  });
  record('system controls are hidden and disabled before the authoritative read resolves',
    systemPre.hidden && systemPre.display === 'none' && systemPre.disabled, systemPre);
  await systemPending;
  await page.waitForSelector('[data-system-forms]:not([hidden])');
  const systemPost = await page.evaluate(() => {
    const forms = document.querySelector('[data-system-forms]');
    const controls = [...document.querySelectorAll('[data-system-control]')];
    return { hidden: forms.hidden, display: getComputedStyle(forms).display, enabled: controls.length > 0 && controls.every((node) => !node.disabled) };
  });
  record('system controls become visible and enabled after the authoritative read resolves',
    !systemPost.hidden && systemPost.display !== 'none' && systemPost.enabled, systemPost);
  await page.unroute('**/api/organization/portal');
  const bad = await page.goto(`${base}/employees/emp-does-not-exist`); await page.waitForFunction(() => document.querySelector('[data-page-status]').textContent.includes('not found'));
  record('unknown employee detail route fails safely in-page', bad.status() === 200 && (await page.locator('[data-page-status]').innerText()).includes('not found'));

  // Hiring lifecycle remains truthful. Capture the authoritative pre-submit
  // state so a created request is proven additive rather than assumed to be the
  // only row, and wait for the exact created id instead of racing the async
  // list reload that the receipt is written before.
  await page.goto(`${base}/hiring`); await page.waitForSelector('[data-hire-requests]:not([hidden])');
  const beforeRequests = await page.evaluate(() => fetch('/api/hire-requests', { credentials: 'same-origin', cache: 'no-store' }).then((response) => response.json()));
  const beforeIds = beforeRequests.map((request) => request.id);
  const beforeCardIds = await page.$$eval('.request-card', (nodes) => nodes.map((node) => node.dataset.requestId));
  record('hiring pre-submit list is authoritative and consistent', Array.isArray(beforeRequests) && beforeCardIds.join('|') === beforeIds.join('|'), { beforeCount: beforeIds.length });
  await page.fill('#hire-name', 'Browser Developer'); await page.fill('#hire-purpose', 'Validate durable request lifecycle.');
  const [createResponse] = await Promise.all([
    page.waitForResponse((response) => response.url().endsWith('/api/hire-requests') && response.request().method() === 'POST'),
    page.click('[data-hire-form] button[type="submit"]'),
  ]);
  let created = null; try { created = await createResponse.json(); } catch { created = null; }
  const createdId = typeof created?.id === 'string' ? created.id : null;
  record('hiring POST returns 200 with a new Requested durable request',
    createResponse.status() === 200 && createdId?.startsWith('hire-') && created.state === 'Requested'
      && !beforeIds.includes(createdId) && typeof created.requestedDisplayName === 'string',
    { status: createResponse.status(), createdId, state: created?.state });
  if (!createdId) {
    record('hiring list count increments by exactly one', false, { reason: 'no created id' });
    record('hiring card shows the exact created name and Requested state', false, { reason: 'no created id' });
    record('hiring receipt truthfully reports the created id and no employee', false, { reason: 'no created id' });
  } else {
    await page.waitForFunction((id) => document.querySelectorAll(`.request-card[data-request-id="${id}"]`).length === 1, createdId);
    const afterCardIds = await page.$$eval('.request-card', (nodes) => nodes.map((node) => node.dataset.requestId));
    const createdCard = page.locator(`.request-card[data-request-id="${createdId}"]`);
    const createdText = await createdCard.innerText();
    const receiptText = await page.locator('[data-hire-receipt]').innerText();
    record('hiring list count increments by exactly one',
      afterCardIds.length === beforeIds.length + 1 && afterCardIds.filter((id) => id === createdId).length === 1,
      { beforeCount: beforeIds.length, afterCount: afterCardIds.length, createdId });
    record('hiring card shows the exact created name and Requested state',
      createdText.includes('Browser Developer') && createdText.includes('Requested'),
      { heading: await createdCard.locator('h3').innerText(), meta: await createdCard.locator('p').first().innerText() });
    record('hiring receipt truthfully reports the created id and no employee',
      receiptText.includes(`Created request ${createdId}.`) && receiptText.includes('No employee has been created.'),
      { receipt: receiptText });
  }
  const approvalText = await page.locator('.request-card').first().innerText();
  const hiringHeading = await page.locator('[data-hiring-page] .page-heading').innerText();
  const hiringPageText = await page.locator('[data-hiring-page]').innerText();
  // The default request is an InternalSharedContainer, which has no profile build
  // to select: its card offers no Approve control and explains the placement.
  record('an internal shared container request offers no approval and explains the placement',
    await page.locator('.request-card button:text("Approve")').count() === 0
      && approvalText.includes('Only a DeveloperContainer hire can be approved') && !approvalText.includes('#217'), { approvalText });
  record('hiring heading carries the approval truth and no superseded #217 gate',
    !hiringHeading.includes('#217') && hiringHeading.includes('creates the managed employee identity and runtime binding')
      && hiringHeading.includes('durably queues provisioning and orientation to Ready'), { hiringHeading });
  record('hiring page-wide copy carries no superseded #217 gate and no owner identity',
    !hiringPageText.includes('#217') && !hiringPageText.includes('owner-basic-auth'), { hiringPageText });

  await page.addInitScript(() => {
    try { Object.defineProperty(Crypto.prototype, 'randomUUID', { configurable: true, value: undefined }); } catch {}
  });
  await page.reload(); await page.waitForSelector('[data-hire-requests]:not([hidden])');
  await page.fill('#hire-name', 'Fallback UUID Developer'); await page.fill('#hire-purpose', 'Verify secure UUID fallback.');
  const [fallbackResponse] = await Promise.all([
    page.waitForResponse((response) => response.url().endsWith('/api/hire-requests') && response.request().method() === 'POST'),
    page.click('[data-hire-form] button[type="submit"]'),
  ]);
  const fallbackRequest = fallbackResponse.request();
  const fallbackKey = fallbackRequest.headers()['idempotency-key'];
  record('secure UUID fallback submits when randomUUID is unavailable', fallbackResponse.status() === 200 && /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(fallbackKey || ''), { status: fallbackResponse.status(), fallbackKey });
  await page.reload(); await page.waitForSelector(`.request-card[data-request-id="${createdId}"]`); const browserCard = page.locator(`.request-card[data-request-id="${createdId}"]`); record('hire request survives reload', (await browserCard.innerText()).includes('Browser Developer'));
  await browserCard.locator('button:text("Reject")').click(); await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').textContent.includes('Rejected'));
  record('requested hire can be revision-bound rejected', (await browserCard.innerText()).includes('Rejected'));

  // ---- approval selection, pending, error and success -----------------
  // A Requested DeveloperContainer request becomes selectable only when an active
  // current profile revision has a verified built row on the controller-local
  // Docker target. The exact selection reaches the approve body; the receipt
  // stays pending, then errors, then succeeds and renders the frozen identities
  // without provisioning.
  const requestedApproval = { id: 'hire-approve-live', state: 'Requested', revision: 4, createdAt: '2026-01-01T00:00:00.0000000+00:00', requestedDisplayName: 'Approve Live', departmentDisplayName: 'Operations', roleDisplayName: 'Operations / IT', placement: 'DeveloperContainer', cpuLimit: 2, memoryLimitMiB: 2048, pidsLimit: 256, purpose: 'Approve this developer container.' };
  const approvedApproval = { ...requestedApproval, state: 'Approved', revision: 5, containerProfileRevisionId: 'prev-live', profileBuildId: 'build-live', approvedImageDigest: 'sha256:' + '9'.repeat(64), approvedHostId: 'local-docker', employeeId: 'emp-managed', runtimeBindingId: 'rtb-managed', workerId: null, statusDetail: null };
  let approvalPhase = 'pending';
  await page.route('**/api/hire-requests', async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const body = approvalPhase === 'success' ? [approvedApproval] : [requestedApproval];
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route('**/api/hire-requests/*/approve', async (route) => {
    if (approvalPhase === 'pending') { await sleep(400); return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(requestedApproval) }); }
    if (approvalPhase === 'error') return route.fulfill({ status: 409, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Hire request approval conflicted.', detail: 'The hire request changed; reload and retry.' }) });
    approvalPhase = 'success';
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(approvedApproval) });
  });
  await page.route('**/api/profiles', async (route) => route.request().method() === 'GET'
    ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ id: 'prof-live', slug: 'generic-employee', displayName: 'Generic Employee', status: 'active', currentRevisionId: 'prev-live', currentRevisionNumber: 1 }]) })
    : route.continue());
  await page.route('**/api/profiles/*/revisions/*/builds', async (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ id: 'build-live', profileRevisionId: 'prev-live', hostId: 'local-docker', state: 'built', verified: true, imageDigest: 'sha256:' + '9'.repeat(64) }]) }));

  const statusAttr = (selector) => page.getAttribute(selector, 'data-status');
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.request-card[data-request-id="hire-approve-live"] [data-approve-profile]:not([disabled])');
  record('a verified build on the controller-local Docker target enables the approval selection',
    await page.locator('[data-approve-host="hire-approve-live"]').count() === 0
      && await page.locator('[data-approve-profile="hire-approve-live"] option').count() === 1,
    { profileOptions: await page.locator('[data-approve-profile="hire-approve-live"] option').allTextContents() });

  const pendingApprove = page.waitForRequest((request) => request.url().endsWith('/api/hire-requests/hire-approve-live/approve'));
  await page.click('.request-card[data-request-id="hire-approve-live"] button:text("Approve")');
  const selection = (await pendingApprove).postDataJSON();
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').dataset.status === 'pending');
  record('an in-flight approval reports a pending receipt and sends the exact selection',
    (await statusAttr('[data-hire-receipt]')) === 'pending' && selection.expectedRevision === 4 && selection.profileRevisionId === 'prev-live' && selection.hostId === undefined,
    { receipt: await page.locator('[data-hire-receipt]').innerText(), selection });

  // Let the in-flight approval settle before the next phase so its receipt does
  // not race the assertions that follow.
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').dataset.status === 'ok');

  approvalPhase = 'error';
  await page.click('.request-card[data-request-id="hire-approve-live"] button:text("Approve")');
  await page.waitForFunction(() => document.querySelector('[data-hire-receipt]').dataset.status === 'error');
  record('a failed approval reports an error receipt with the server detail',
    (await statusAttr('[data-hire-receipt]')) === 'error' && (await page.locator('[data-hire-receipt]').innerText()).includes('The hire request changed'),
    { status: await statusAttr('[data-hire-receipt]'), receipt: await page.locator('[data-hire-receipt]').innerText() });

  approvalPhase = 'success';
  await page.click('.request-card[data-request-id="hire-approve-live"] button:text("Approve")');
  await page.waitForSelector('.request-card[data-request-id="hire-approve-live"] [data-frozen-approval]');
  const frozenApproval = await page.locator('.request-card[data-request-id="hire-approve-live"] [data-frozen-approval]').innerText();
  record('a successful approval clears the receipt and renders the frozen build, digest, target, employee and binding',
    (await statusAttr('[data-hire-receipt]')) === 'ok' && frozenApproval.includes('prev-live') && frozenApproval.includes('build-live') && frozenApproval.includes('emp-managed') && frozenApproval.includes('rtb-managed') && frozenApproval.includes('Controller-local Docker'),
    { status: await statusAttr('[data-hire-receipt]'), frozenApproval });
  record('the approved card states provisioning is durably queued and never exposes the owner identity',
    (await page.locator('.request-card[data-request-id="hire-approve-live"] [data-provisioning-note]').innerText()).includes('durably queued provisioning and orientation')
      && !(await page.locator('[data-hiring-page]').innerText()).includes('owner-basic-auth'),
    { provisioningNote: await page.locator('.request-card[data-request-id="hire-approve-live"] [data-provisioning-note]').innerText() });

  await page.unroute('**/api/hire-requests');
  await page.unroute('**/api/hire-requests/*/approve');
  await page.unroute('**/api/profiles');
  await page.unroute('**/api/profiles/*/revisions/*/builds');

  await page.goto(`${base}/system`); await page.waitForSelector('[data-system-forms]:not([hidden])');
  const original = await page.inputValue('[data-org-name-input]'); await page.fill('[data-org-name-input]', `${original} draft`);
  const dirtyStyle = await page.locator('[data-org-name-input]').evaluate((node) => ({ borderColor: getComputedStyle(node).borderColor, boxShadow: getComputedStyle(node).boxShadow }));
  record('system optimistic draft is visibly retained before submit', await page.inputValue('[data-org-name-input]') === `${original} draft` && await page.getAttribute('[data-org-name-input]', 'data-draft-state') === 'dirty' && await page.locator('[data-org-name-draft-state]').isVisible());
  const authorityChange = await page.evaluate(async (displayName) => {
    const current = await (await fetch('/api/organization/portal', { cache: 'no-store' })).json();
    const response = await fetch('/api/organization', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ organizationId: current.id, revision: current.revision, displayName }),
    });
    return response.status;
  }, `${original} authoritative`);
  await page.click('[data-org-name-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-org-name-input]')?.dataset.draftState === 'conflict');
  const conflictStyle = await page.locator('[data-org-name-input]').evaluate((node) => ({ borderColor: getComputedStyle(node).borderColor, boxShadow: getComputedStyle(node).boxShadow }));
  record('authoritative revision change preserves a distinct accessible conflict draft',
    authorityChange === 200
      && await page.inputValue('[data-org-name-input]') === `${original} draft`
      && await page.getAttribute('[data-org-name-input]', 'aria-invalid') === 'true'
      && await page.locator('[data-org-name-draft-state]').isVisible()
      && (await page.locator('[data-org-name-draft-state]').innerText()).startsWith('Conflict:')
      && conflictStyle.borderColor !== dirtyStyle.borderColor,
    { authorityChange, dirtyStyle, conflictStyle });
  await page.click('[data-reset-org-name]'); record('system conflict draft can reset to new authority', await page.inputValue('[data-org-name-input]') === `${original} authoritative` && await page.getAttribute('[data-org-name-input]', 'aria-invalid') === null);

  // Container profiles: seeded generic-employee, create, detail, immutable revision, retire, friendly errors.
  await page.goto(`${base}/profiles`); await page.waitForSelector('[data-profile-list]:not([hidden])');
  record('profiles page lists the seeded generic-employee profile', await page.locator('[data-profile-list] .request-card').count() === 1 && (await page.locator('[data-profile-list] .request-card p').first().innerText()).includes('generic-employee · active · revision 1 (unbuilt)'));
  await page.fill('#profile-slug', 'browser-profile'); await page.fill('#profile-name', 'Browser profile'); await page.fill('#profile-description', 'Created by the routed browser suite.');
  await page.fill('#profile-definition', '{"image":"agentcontrol-worker-base","privileged":true}');
  await page.click('[data-profile-submit]'); await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').dataset.status === 'error');
  record('profiles create rejects a forbidden devcontainer key with the server reason', (await page.locator('[data-profile-receipt]').innerText()).includes("'privileged' is not allowed"));
  await page.fill('#profile-definition', '{"name":"Browser profile","image":"agentcontrol-worker-base","containerEnv":{"TZ":"UTC"}}');
  await page.click('[data-profile-submit]'); await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').dataset.status === 'ok');
  record('profiles create records revision 1 and states nothing was built or hired', (await page.locator('[data-profile-receipt]').innerText()).includes('No image was built and no employee was created') && await page.locator('[data-profile-list] .request-card').count() === 2);
  const profileDetailPath = await page.locator('[data-profile-list] .request-card a[href^="/profiles/prof-"]').first().getAttribute('href');
  await page.goto(`${base}${profileDetailPath}`); await page.waitForSelector('[data-profile-content]:not([hidden])');
  record('profile detail loads the exact profile with one revision and a prefilled editor', await page.locator('[data-profile-revisions] .request-card').count() === 1 && (await page.inputValue('#revision-definition')).includes('agentcontrol-worker-base') && await page.locator('.primary-nav a.active').count() === 1);
  await page.fill('#revision-definition', '{"build":{"dockerfile":"Dockerfile"},"name":"Browser profile"}'); await page.fill('#revision-fragment', 'FROM agentcontrol-worker-base\nRUN true\n');
  await page.click('[data-revision-submit]'); await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').dataset.status === 'ok');
  record('profile detail appends revision 2 without rebuilding employees', (await page.locator('[data-profile-receipt]').innerText()).includes('Created revision 2') && await page.locator('[data-profile-revisions] .request-card').count() === 2 && (await page.locator('[data-profile-revisions] .request-card h3').first().innerText()).includes('Revision 2 (current)'));
  await page.click('[data-profile-retire]'); await page.waitForFunction(() => document.querySelector('[data-profile-receipt]').textContent.includes('Profile retired'));
  record('profile retire disables new revisions and keeps history readable', await page.locator('[data-profile-retire]').isHidden() && await page.locator('[data-revision-submit]').isDisabled() && await page.locator('[data-profile-revisions] .request-card').count() === 2);
  await page.goto(`${base}/profiles/prof-doesnotexist`); await page.waitForFunction(() => document.querySelector('[data-page-status]').dataset.status === 'error');
  record('unknown profile id reports a friendly not-found status', (await page.locator('[data-page-status]').innerText()).includes('Profile not found'));
  await page.goto(`${base}/profiles/not-a-profile`); await page.waitForFunction(() => document.querySelector('[data-page-status]').dataset.status === 'error');
  record('malformed profile id reports a friendly invalid-id status', (await page.locator('[data-page-status]').innerText()).includes('Invalid profile id'));

  // Responsive navigation and overflow across the hierarchy routes.
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    for (const path of ['/organization', '/organization/departments', detailPath, '/hiring', '/profiles', profileDetailPath, '/employees', '/system', employeeDetailPath]) {
      await page.goto(`${base}${path}`);
      if (path === '/organization') await page.waitForSelector('[data-org-department-cards]:not([hidden])');
      else if (path === '/organization/departments') await page.waitForSelector('[data-department-directory]:not([hidden])');
      else if (path === detailPath) await page.waitForSelector('[data-department-content]:not([hidden])');
      else if (path === '/hiring') await page.waitForSelector('[data-hire-requests]:not([hidden])');
      else if (path === '/profiles') await page.waitForSelector('[data-profile-list]:not([hidden])');
      else if (path === profileDetailPath) await page.waitForSelector('[data-profile-content]:not([hidden])');
      else if (path === '/employees') await page.waitForSelector('[data-employee-directory]:not([hidden])');
      else if (path === '/system') await page.waitForSelector('[data-system-forms]:not([hidden])');
      else await page.waitForSelector('[data-employee-content]:not([hidden])');
      const widths = await page.evaluate(() => ({ document: document.documentElement.scrollWidth, viewport: innerWidth }));
      record(`${path} has no horizontal overflow at ${viewport.width}px`, widths.document <= widths.viewport + 1, widths);
    }
    await page.goto(`${base}/organization`); await page.waitForSelector('[data-org-department-cards]:not([hidden])');
    const navToggleVisible = await page.locator('[data-nav-toggle]').isVisible();
    if (viewport.width < 800) {
      record('mobile nav toggle is reachable and reveals grouped organization links', navToggleVisible && await page.locator('[data-primary-nav]').isHidden() === true);
      await page.click('[data-nav-toggle]'); await page.waitForSelector('[data-primary-nav][data-open="true"]');
      record('mobile grouped nav exposes Overview and Departments', await page.locator('[data-primary-nav] a').count() >= 5 && await page.locator('[data-primary-nav] a:text("Overview")').count() === 1 && await page.locator('[data-primary-nav] a:text("Departments")').count() === 1);
    } else {
      record('desktop nav is persistent without the toggle', !navToggleVisible && await page.locator('[data-primary-nav]').isVisible() && await page.locator('.primary-nav a.active').count() === 1);
    }
  }
  record('routed suite has no page errors', pageErrors.length === 0, { pageErrors });
} catch (error) { fatal = error; record('organization browser suite completed without fatal error', false, { fatal: error.stack || String(error) }); }
finally { if (browser) await browser.close(); if (child && child.exitCode === null) { child.kill('SIGTERM'); await Promise.race([new Promise((resolve) => child.once('exit', resolve)), sleep(5000)]); if (child.exitCode === null) child.kill('SIGKILL'); } }
const failed = results.filter((item) => !item.passed); console.log(`---\nRESULT: ${results.length - failed.length}/${results.length} passed, ${failed.length} failed`); if (fatal) console.error(fatal.stack || fatal); if (failed.length) process.exitCode = 1;

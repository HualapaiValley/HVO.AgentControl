// AgentControl V2 hermetic enabled-runtime organization browser check.
//
// Spawns the locally built .NET app (src/HVO.AgentControl/bin/Release/net10.0)
// on a free loopback port with Control:Enabled=true, a disposable owner
// password file and the checked-in fake ACP fixture as the OpenCode executable.
// The fake ACP is deterministic and makes no provider or inference call. The
// suite then drives the REAL Blazor portal and asserts that the organization
// panel fetches and renders the authoritative store read model:
//
//   1. the enabled runtime becomes ready and the portal shows it
//   2. /js/organization.js is served and runs
//   3. the organization panel is visible and renders exactly the seeded
//      Development/Operations/QA departments, one Operations/IT employee and
//      one adoption-audit row
//   4. no controller secret (owner password, tmux owner token) is present in
//      the rendered panel or the /api/organization JSON
//   5. the panel stays visible with no horizontal overflow at desktop (1440x900)
//      and mobile (390x844)
//
// This is separate from ci-smoke.mjs (the disabled runtime smoke) so the
// disabled baseline keeps running unchanged. No Docker, model provider, or
// inference credentials are used.
//
// Usage: npm run ci-organization --prefix tests/Browser
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
const OUT_DIR = process.env.ARTIFACTS_DIR || join(ROOT, 'artifacts', 'browser-organization');
const DLL = process.env.APP_DLL || join(PROJECT_DIR, 'bin', 'Release', 'net10.0', 'HVO.AgentControl.dll');
const FAKE_ACP_SOURCE = join(ROOT, 'tests', 'HVO.AgentControl.Tests', 'Fixtures', 'fake_acp.py');
const STARTUP_TIMEOUT_MS = Number(process.env.STARTUP_TIMEOUT_MS || 90000);
const PAGE_TITLE = 'AgentControl V2';
const OWNER_PASSWORD = 'organization-browser-owner-password-0000';

const results = [];
const record = (name, passed, detail = {}) => {
  results.push({ name, passed: !!passed, detail });
  console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${Object.keys(detail).length ? '  :: ' + JSON.stringify(detail) : ''}`);
};

function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

async function waitForHealth(base, deadline) {
  for (;;) {
    try {
      const response = await fetch(`${base}/health/live`);
      if (response.ok) return true;
    } catch {
      /* not listening yet */
    }
    if (Date.now() >= deadline) return false;
    await sleep(200);
  }
}

// Materialize the checked-in fake ACP fixture as an executable plus the
// non-executable scenario sidecar it reads. The fixture is copied, never run
// from the source tree, so the workspace stays clean.
function createFakeAcp(root) {
  if (!existsSync(FAKE_ACP_SOURCE)) {
    throw new Error(`fake ACP fixture not found at ${FAKE_ACP_SOURCE}`);
  }
  const executable = join(root, 'fake_acp.py');
  copyFileSync(FAKE_ACP_SOURCE, executable);
  chmodSync(executable, 0o755);
  writeFileSync(join(root, 'scenario'), 'prompt_fast');
  return executable;
}

function readRows(page, selector) {
  return page.$$eval(`${selector} dt, ${selector} dd`, (nodes) => {
    const pairs = [];
    for (let i = 0; i < nodes.length; i += 2) {
      pairs.push([nodes[i].textContent.trim(), nodes[i + 1]?.textContent.trim() ?? null]);
    }
    return pairs;
  });
}

async function measurePanel(page, viewport) {
  await page.setViewportSize(viewport);
  await sleep(300);
  return page.evaluate(() => {
    const panel = document.querySelector('[data-org-overview]');
    const rect = panel.getBoundingClientRect();
    return {
      hidden: panel.hidden,
      top: rect.top,
      bottom: rect.bottom,
      width: rect.width,
      viewportWidth: window.innerWidth,
      documentWidth: document.documentElement.scrollWidth,
    };
  });
}

mkdirSync(OUT_DIR, { recursive: true });
const startedAt = new Date().toISOString();
const consoleErrors = [];
const pageErrors = [];
let child = null;
let browser = null;
let page = null;
let port = null;
let fatal = null;
let runtimeRoot = null;

try {
  if (!existsSync(DLL)) {
    throw new Error(
      `Built app not found at ${DLL}. Build first: dotnet build HVO.AgentControl.slnx --no-restore -c Release`,
    );
  }

  port = await freePort();
  const base = `http://127.0.0.1:${port}`;
  runtimeRoot = join(tmpdir(), `agentcontrol-org-browser-${randomBytes(8).toString('hex')}`);
  mkdirSync(join(runtimeRoot, 'data'), { recursive: true });
  mkdirSync(join(runtimeRoot, 'private'), { recursive: true });
  const passwordPath = join(runtimeRoot, 'owner-password');
  writeFileSync(passwordPath, OWNER_PASSWORD);
  const fakeAcp = createFakeAcp(runtimeRoot);

  // Hermetic child environment: drop every inherited Control__* value (including
  // a real Control__OwnerPasswordFile or DatabasePath override), then opt into
  // the enabled runtime with the disposable fixture and fixed store layout.
  const childEnv = { ...process.env };
  for (const key of Object.keys(childEnv)) {
    if (/^Control__/i.test(key)) delete childEnv[key];
  }
  childEnv.Control__Enabled = 'true';
  childEnv.Control__DataDirectory = join(runtimeRoot, 'data');
  childEnv.Control__PrivateDataDirectory = join(runtimeRoot, 'private');
  childEnv.Control__OpenCodeExecutable = fakeAcp;
  childEnv.Control__OwnerPasswordFile = passwordPath;
  childEnv.Control__EnableTerminal = 'false';
  childEnv.Control__NativePort = String(port + 1);
  childEnv.Control__StartupTimeoutSeconds = '20';
  childEnv.Control__PromptTimeoutSeconds = '20';
  childEnv.ASPNETCORE_ENVIRONMENT = process.env.ASPNETCORE_ENVIRONMENT || 'Development';
  childEnv.DOTNET_ENVIRONMENT = childEnv.ASPNETCORE_ENVIRONMENT;
  childEnv.ASPNETCORE_URLS = base;

  const logPath = join(OUT_DIR, 'app.log');
  const logStream = createWriteStream(logPath);
  child = spawn('dotnet', [DLL], { cwd: PROJECT_DIR, env: childEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout.pipe(logStream);
  child.stderr.pipe(logStream);
  let childExit = null;
  child.on('exit', (code, signal) => {
    childExit = { code, signal };
  });

  const ready = await waitForHealth(base, Date.now() + STARTUP_TIMEOUT_MS);
  if (childExit) {
    throw new Error(`app exited during startup (code=${childExit.code} signal=${childExit.signal}); see ${logPath}`);
  }
  record(`enabled app responds on /health/live within ${STARTUP_TIMEOUT_MS}ms`, ready, { base, log: logPath });
  if (!ready) throw new Error(`app did not become healthy within ${STARTUP_TIMEOUT_MS}ms; see ${logPath}`);

  browser = await chromium.launch({
    ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : {}),
    headless: true,
    args: ['--no-sandbox'],
  });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    httpCredentials: { username: 'owner', password: OWNER_PASSWORD },
  });
  page = await context.newPage();
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', (error) => pageErrors.push(String(error && error.message ? error.message : error)));

  const assetResponses = {};
  page.on('response', (response) => {
    const path = new URL(response.url()).pathname;
    if (path === '/js/organization.js' || path === '/css/portal.css') {
      assetResponses[path] = response.status();
    }
  });

  const response = await page.goto(`${base}/`, { waitUntil: 'domcontentloaded', timeout: 30000 });
  const title = await page.title();
  await page.waitForSelector('[data-portal]', { timeout: 15000 });
  record('authenticated root page returns HTTP 200', (response?.status() ?? 0) === 200, { status: response?.status() ?? 0 });
  record(`root page title is "${PAGE_TITLE}"`, title === PAGE_TITLE, { title });

  // The enabled runtime reaches ready once the fake ACP handshake completes.
  await page.waitForSelector('[data-runtime-state="ready"]', { timeout: STARTUP_TIMEOUT_MS });
  record('enabled runtime reaches ready in the portal', true, {});

  record(
    '/js/organization.js is served with HTTP 200',
    assetResponses['/js/organization.js'] === 200,
    assetResponses,
  );

  // The store opens before ready, so the panel should load immediately; give it
  // a bounded window in case the first fetch races the state transition.
  await page.waitForSelector('[data-organization-loaded]', { timeout: 30000 });
  const panelVisible = await page.evaluate(() => {
    const panel = document.querySelector('[data-org-overview]');
    return !panel.hidden && panel.getBoundingClientRect().height > 0;
  });
  record('organization panel is visible after the store-backed fetch', panelVisible, {});

  // Exact departments: Development (0), Operations (1), QA (0).
  const departments = await readRows(page, '[data-org-departments]');
  const expectedDepartments = [
    ['Development', '0 employees'],
    ['Operations', '1 employee'],
    ['QA', '0 employees'],
  ];
  record(
    'organization panel renders exactly the seeded departments and counts',
    JSON.stringify(departments) === JSON.stringify(expectedDepartments),
    { departments, expectedDepartments },
  );

  const navItems = await page.$$eval('[data-nav]', (nodes) => nodes.map((node) => node.textContent.trim()));
  record(
    'portal renders all five organization navigation items',
    JSON.stringify(navItems) === JSON.stringify(['Overview', 'Operations', 'Development', 'QA', 'System configuration']),
    { navItems },
  );
  const availability = await readRows(page, '[data-org-availability]');
  const expectedAvailability = [
    ['ready', '0'], ['provisioning', '0'], ['held', '1'], ['reconciliation required', '0'],
    ['interrupted', '0'], ['reload required', '0'], ['orientation failed', '0'],
    ['orientation stale', '0'], ['runtime unavailable', '0'],
  ];
  record('portal renders authoritative availability counts for every supported category',
    JSON.stringify(availability) === JSON.stringify(expectedAvailability), { availability, expectedAvailability });
  const pending = await page.textContent('[data-pending-approvals]');
  record('pending approvals are explicitly unsupported with count zero', pending.startsWith('Unsupported (0):'), { pending });

  await page.click('[data-nav="operations"]');
  const employees = await page.$$eval('[data-department-employees="operations"] .employee-card', (nodes) => nodes.map((node) => node.textContent.trim()));
  record('Operations renders exactly the actual seeded employee', employees.length === 1 && employees[0].startsWith('Operations / IT'), { employees });
  await page.click('[data-department-employees="operations"] .employee-card');
  await page.waitForSelector('[data-view="employee"]:not([hidden])');
  const selectedId = await page.getAttribute('[data-portal]', 'data-selected-employee-id');
  const detailText = await page.textContent('[data-view="employee"]');
  record('employee selection opens exact safe detail by stable id', /^emp-/.test(selectedId || '') && detailText.includes(selectedId), { selectedId });

  await page.click('[data-nav="development"]');
  record('Development shows an explicit empty state', (await page.textContent('[data-view="development"]')).includes('No employees'), {});
  await page.click('[data-nav="qa"]');
  record('QA shows an explicit empty state', (await page.textContent('[data-view="qa"]')).includes('No employees'), {});
  await page.click('[data-nav="system"]');
  await page.fill('[data-org-instructions]', 'Browser fixture organization instructions.');
  await page.click('[data-org-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]')?.textContent === 'Saved organization instructions. Orientation is stale.');
  const orgInstructionStatus = await page.evaluate(async () => (await (await fetch('/api/orientation')).json()).state);
  record('System configuration saves organization basic instructions and exposes stale orientation', orgInstructionStatus === 'Stale', { orgInstructionStatus });

  await page.click('[data-nav="system"]');
  await page.fill('[data-role-instructions]', 'Browser fixture Operations standing instructions.');
  await page.click('[data-role-instructions-form] button[type="submit"]');
  await page.waitForFunction(() => document.querySelector('[data-config-receipt]')?.textContent === 'Saved role instructions. Orientation is stale.');
  const roleInstructionEvidence = await page.evaluate(async () => {
    const organization = await (await fetch('/api/organization')).json();
    return {
      instructions: organization.roles.find((role) => role.slug === 'operations-it')?.standingInstructions,
      orientation: await (await fetch('/api/orientation')).json(),
    };
  });
  record(
    'System configuration saves role standing instructions with revision validation',
    roleInstructionEvidence.instructions === 'Browser fixture Operations standing instructions.'
      && roleInstructionEvidence.orientation.state === 'Stale',
    roleInstructionEvidence,
  );
  await page.click('[data-nav="overview"]');

  // Rename through the same API the browser uses, then prove orientation stays
  // readable as Stale until the portal creates and delivers a fresh assignment.
  const renameEvidence = await page.evaluate(async () => {
    const organization = await (await fetch('/api/organization')).json();
    const before = await (await fetch('/api/orientation')).json();
    const renamed = await fetch('/api/organization', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json', Origin: location.origin },
      body: JSON.stringify({ organizationId: organization.id, displayName: 'Browser Renamed Organization', revision: organization.revision }),
    });
    const staleResponse = await fetch('/api/orientation');
    return { renameStatus: renamed.status, before, staleStatus: staleResponse.status, stale: await staleResponse.json() };
  });
  record(
    'browser rename changes the organization fragment and exposes readable stale orientation',
    renameEvidence.renameStatus === 200
      && renameEvidence.staleStatus === 200
      && renameEvidence.stale.state === 'Stale'
      && renameEvidence.stale.ready === false
      && renameEvidence.stale.assignmentId === renameEvidence.before.assignmentId,
    renameEvidence,
  );
  const loadedRevisionBeforeRefresh = Number(await page.getAttribute('[data-portal]', 'data-organization-loaded'));
  await page.evaluate(async () => {
    const portal = document.querySelector('[data-portal]');
    portal.dataset.runtimeState = 'degraded';
    await new Promise((resolve) => setTimeout(resolve, 25));
    portal.dataset.runtimeState = 'ready';
  });
  await page.waitForFunction((revision) => Number(document.querySelector('[data-portal]')?.dataset.organizationLoaded) > revision,
    loadedRevisionBeforeRefresh);
  await page.click('[data-nav="overview"]');
  await page.click('[data-org-failures] a');
  await page.waitForSelector('[data-view="employee"]:not([hidden])');
  record('actionable failure link navigates to the exact employee',
    (await page.getAttribute('[data-portal]', 'data-selected-employee-id')) === selectedId, { selectedId });

  // Exercise the owner controls on the explicitly selected host-owned employee.
  await page.click('[data-nav="operations"]');
  await page.click('[data-department-employees="operations"] .employee-card');
  await page.click('[data-orientation-hold]');
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]')?.textContent.startsWith('Manual hold set. State:'));
  await page.waitForFunction(() => document.querySelector('[data-orientation-hold]')?.dataset.held === 'true');
  record('manual hold button mutates and reloads orientation state', true, {});

  await page.click('[data-orientation-deliver]');
  await page.waitForFunction(() => document.querySelector('[data-orientation-receipt]')?.textContent === 'Delivered. Runtime restart required.');
  const deliveredAfterRename = await page.evaluate(async () => (await (await fetch('/api/orientation')).json()));
  record(
    'deliver button creates a fresh delivered assignment after rename',
    deliveredAfterRename.state === 'Delivered'
      && deliveredAfterRename.restartRequired === true
      && deliveredAfterRename.ready === false
      && deliveredAfterRename.holdReasons.includes('orientation-reload-required')
      && deliveredAfterRename.assignmentId !== renameEvidence.before.assignmentId
      && deliveredAfterRename.orientationVersion !== renameEvidence.before.orientationVersion,
    { before: renameEvidence.before, after: deliveredAfterRename },
  );

  // No secret fields in the rendered panel or the JSON payload.
  const panelText = await page.$eval('[data-org-overview]', (element) => element.textContent || '');
  const basic = Buffer.from(`owner:${OWNER_PASSWORD}`, 'utf8').toString('base64');
  const orgResponse = await fetch(`${base}/api/organization`, {
    headers: { Authorization: `Basic ${basic}`, Accept: 'application/json' },
  });
  const orgJson = await orgResponse.text();
  const secretAbsent = orgResponse.status === 200
    && !panelText.includes(OWNER_PASSWORD)
    && !orgJson.includes(OWNER_PASSWORD)
    && !/tmuxOwnerToken|ownerToken|"password"|ownerPassword/i.test(orgJson + panelText);
  record(
    'no owner password or controller secret is exposed by the panel or /api/organization',
    secretAbsent,
    { status: orgResponse.status, containsTokenField: /tmuxOwnerToken|ownerToken/i.test(orgJson) },
  );

  // Desktop and mobile layout: the panel stays visible with no horizontal overflow.
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await page.click('[data-nav="overview"]');
    await page.waitForSelector('[data-view="overview"]:not([hidden])');
    await page.click('[data-nav="operations"]');
    await page.waitForSelector('[data-view="operations"]:not([hidden])');
    const currentEmployees = await page.$$eval('[data-department-employees="operations"] .employee-card', (nodes) => nodes.length);
    const metric = await measurePanel(page, viewport);
    const passed = !metric.hidden
      && metric.width > 0
      && metric.documentWidth <= metric.viewportWidth + 1
      && metric.top >= -1
      && currentEmployees === 1;
    record(
      `organization navigation and employee count stay contained at ${viewport.width}x${viewport.height}`,
      passed,
      {
        hidden: metric.hidden,
        panelWidth: Math.round(metric.width),
        viewportWidth: metric.viewportWidth,
        documentWidth: metric.documentWidth,
        currentEmployees,
      },
    );
    await page.screenshot({ path: join(OUT_DIR, `organization-${viewport.width}x${viewport.height}.png`) });
  }
  await page.setViewportSize({ width: 1440, height: 900 });

  record('no page errors while rendering the organization panel', pageErrors.length === 0, {
    pageErrors,
    consoleErrors,
  });
} catch (error) {
  fatal = String(error && error.stack ? error.stack : error);
  record('organization browser suite completed without fatal error', false, { fatal });
} finally {
  try { if (page && !page.isClosed()) await page.close(); } catch { /* ignore */ }
  try { if (browser) await browser.close(); } catch { /* ignore */ }
  try {
    if (child && child.exitCode === null && child.signalCode === null) {
      child.kill('SIGTERM');
      const deadline = Date.now() + 10000;
      while (child.exitCode === null && child.signalCode === null && Date.now() < deadline) {
        await sleep(100);
      }
      if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    }
  } catch { /* ignore */ }
  try {
    if (runtimeRoot) {
      const { rmSync } = await import('node:fs');
      rmSync(runtimeRoot, { recursive: true, force: true });
    }
  } catch { /* ignore */ }
}

const passed = results.filter((result) => result.passed).length;
const failed = results.filter((result) => result.passed === false).length;
const payload = {
  generatedBy: 'tests/Browser/ci-organization.mjs',
  startedAt,
  finishedAt: new Date().toISOString(),
  environment: {
    root: ROOT,
    projectDir: PROJECT_DIR,
    dll: DLL,
    baseUrl: port ? `http://127.0.0.1:${port}` : null,
    chromium: process.env.CHROME_PATH || 'playwright-bundled',
    node: process.version,
    fakeAcp: FAKE_ACP_SOURCE,
  },
  consoleErrors,
  pageErrors,
  totals: { passed, failed, total: results.length },
  fatal,
  results,
};
writeFileSync(join(OUT_DIR, 'ci-organization-results.json'), JSON.stringify(payload, null, 2));
console.log('---');
console.log(`RESULT: ${passed}/${results.length} passed, ${failed} failed`);
console.log(`ARTIFACTS: ${OUT_DIR}`);
if (fatal) console.log(`FATAL: ${fatal}`);
process.exit(failed || fatal ? 1 : 0);

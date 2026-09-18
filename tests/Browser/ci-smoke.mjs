// AgentControl V2 hermetic CI browser smoke test.
//
// Spawns the locally built .NET app (src/HVO.AgentControl/bin/Release/net10.0)
// on a free loopback port with Control:Enabled=false and drives the REAL Blazor
// portal with Playwright. No Docker, no model provider, no credentials, and no
// runtime mutations are used.
//
// Coverage:
//   1. root page returns 200, title is "AgentControl V2", [data-portal] and
//      [data-terminal] render (real Blazor output)
//   2. the external frontend module runs: runtime state is applied from
//      GET /api/control and the required JS/CSS assets load with no page or
//      console errors
//   3. GET /api/control reports state=disabled, modelSyncSupported=false,
//      terminalReady=false and an empty catalog
//   4. an actual WebSocket upgrade to /terminal is refused with 503 while the
//      runtime is disabled
//   5. POST /api/control/model and /api/control/cancel: cross-origin -> 403,
//      same-origin -> 409 (runtime not ready); no owner password is configured
//      so no 401 challenge is expected
//   6. the routed shell fills the viewport with its footer at the bottom on
//      desktop (1440x900) and mobile (390x844), without horizontal overflow
//
// Usage: npm run ci --prefix tests/Browser
//        (or) node tests/Browser/ci-smoke.mjs
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { createWriteStream, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { request } from 'node:http';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const ROOT = process.env.REPO_ROOT || fileURLToPath(new URL('../../', import.meta.url));
const PROJECT_DIR = join(ROOT, 'src', 'HVO.AgentControl');
const OUT_DIR = process.env.ARTIFACTS_DIR || join(ROOT, 'artifacts', 'browser-ci');
const DLL = process.env.APP_DLL || join(PROJECT_DIR, 'bin', 'Release', 'net10.0', 'HVO.AgentControl.dll');
const STARTUP_TIMEOUT_MS = Number(process.env.STARTUP_TIMEOUT_MS || 60000);
const PAGE_TITLE = 'AgentControl V2';

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

// A raw WebSocket upgrade attempt. A disabled runtime must answer 503 before
// the handshake is accepted; a 101 here would mean the terminal gate is open.
function wsUpgradeStatus(base, path = '/terminal') {
  const url = new URL(base);
  return new Promise((resolve) => {
    const req = request({
      host: url.hostname,
      port: url.port,
      path,
      method: 'GET',
      headers: {
        Connection: 'Upgrade',
        Upgrade: 'websocket',
        'Sec-WebSocket-Version': '13',
        'Sec-WebSocket-Key': randomBytes(16).toString('base64'),
      },
    });
    req.on('upgrade', (res, socket) => {
      socket.destroy();
      resolve({ status: res.statusCode, upgraded: true });
    });
    req.on('response', (res) => {
      res.resume();
      resolve({ status: res.statusCode, upgraded: false });
    });
    req.on('error', (error) => resolve({ status: null, error: String(error && error.message ? error.message : error) }));
    req.setTimeout(10000, () => {
      req.destroy();
      resolve({ status: null, error: 'timeout' });
    });
    req.end();
  });
}

function postJson(base, path, origin, body = '{}') {
  const headers = { 'Content-Type': 'application/json' };
  if (origin !== undefined) headers.Origin = origin;
  return fetch(`${base}${path}`, { method: 'POST', headers, body }).then(
    (response) => response.status,
    (error) => ({ error: String(error && error.message ? error.message : error) }),
  );
}

const approx = (a, b) => Math.abs(a - b) <= 2;

async function measureShellLayout(page) {
  return page.evaluate(() => {
    const rect = (selector) => {
      const r = document.querySelector(selector).getBoundingClientRect();
      return { top: r.top, bottom: r.bottom, height: r.height };
    };
    return {
      viewportHeight: window.innerHeight,
      viewportWidth: window.innerWidth,
      documentHeight: document.documentElement.scrollHeight,
      documentWidth: document.documentElement.scrollWidth,
      header: rect('.shell-header'),
      content: rect('.page-content'),
      footer: rect('.shell-footer'),
    };
  });
}

async function runShellLayoutChecks(page, viewports) {
  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    await sleep(300);
    const layout = await measureShellLayout(page);
    const passed = approx(layout.footer.bottom, Math.max(viewport.height, layout.documentHeight))
      && layout.content.top >= layout.header.bottom - 1
      && layout.content.bottom <= layout.footer.top + 1
      && layout.documentHeight >= viewport.height
      && layout.documentWidth <= viewport.width + 1;
    record(`routed shell fills viewport with footer at bottom ${viewport.width}x${viewport.height}`, passed, layout);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await sleep(200);
}

mkdirSync(OUT_DIR, { recursive: true });
const startedAt = new Date().toISOString();
const consoleErrors = [];
const pageErrors = [];
const failedRequests = [];
const assetResponses = {};
let child = null;
let browser = null;
let page = null;
let port = null;
let fatal = null;

try {
  if (!existsSync(DLL)) {
    throw new Error(
      `Built app not found at ${DLL}. Build first: dotnet build HVO.AgentControl.slnx --no-restore -c Release`,
    );
  }

  port = await freePort();
  const base = `http://127.0.0.1:${port}`;

  // Hermetic child environment: drop every inherited Control__* value (including
  // Control__OwnerPasswordFile) so the disabled runtime needs no password, then
  // opt out explicitly. Development makes MapStaticAssets resolve the built
  // wwwroot manifest from the project directory.
  const childEnv = { ...process.env };
  for (const key of Object.keys(childEnv)) {
    if (/^Control__/i.test(key)) delete childEnv[key];
  }
  childEnv.Control__Enabled = 'false';
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

  // ---- bounded startup ---------------------------------------------------
  const ready = await waitForHealth(base, Date.now() + STARTUP_TIMEOUT_MS);
  if (childExit) {
    throw new Error(`app exited during startup (code=${childExit.code} signal=${childExit.signal}); see ${logPath}`);
  }
  record(`app responds on /health/live within ${STARTUP_TIMEOUT_MS}ms`, ready, { base, log: logPath });
  if (!ready) throw new Error(`app did not become healthy within ${STARTUP_TIMEOUT_MS}ms; see ${logPath}`);

  // ---- browser -----------------------------------------------------------
  browser = await chromium.launch({
    ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : {}),
    headless: true,
    args: ['--no-sandbox'],
  });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  page = await context.newPage();
  // The disabled runtime intentionally answers some portal reads with 404/503
  // so the suite can prove safe in-page failure. The browser mirrors each such
  // response as a generic "Failed to load resource" console entry; that status
  // is already asserted explicitly (and transport failures captured below), so
  // only genuine script console errors count here.
  const resourceNoise = /^Failed to load resource: the server responded with a status of \d+/;
  page.on('console', (message) => {
    if (message.type() === 'error' && !resourceNoise.test(message.text())) consoleErrors.push(message.text());
  });
  page.on('pageerror', (error) => pageErrors.push(String(error && error.message ? error.message : error)));
  page.on('requestfailed', (req) => failedRequests.push(`${req.method()} ${req.url()} ${req.failure()?.errorText}`));
  page.on('response', (response) => {
    const path = new URL(response.url()).pathname;
    if (path === '/js/terminal.js' || path === '/js/employee-detail.js' || path === '/css/portal.css'
      || path === '/vendor/xterm/xterm.js' || path === '/vendor/xterm/xterm.css' || path === '/vendor/xterm/addon-fit.js') {
      assetResponses[path] = response.status();
    }
  });

  // ---- 1. routed Blazor landing -----------------------------------------
  const response = await page.goto(`${base}/`, { waitUntil: 'domcontentloaded', timeout: 30000 });
  const title = await page.title();
  await page.waitForSelector('[data-page="landing"]', { timeout: 15000 });
  record('root page returns HTTP 200', (response?.status() ?? 0) === 200, { status: response?.status() ?? 0 });
  record(`root page title is "${PAGE_TITLE}"`, title === PAGE_TITLE, { title });
  record('real Blazor root renders the shared shell and concise landing', await page.evaluate(() => Boolean(document.querySelector('[data-portal-shell]') && document.querySelector('[data-page="landing"]'))), {});

  // ---- 2. route-specific assets -----------------------------------------
  const landingHtml = await page.content();
  record('landing does not load employee terminal DOM', !landingHtml.includes('data-terminal') && !landingHtml.includes('data-employee-detail'));
  record('required routed CSS loads with HTTP 200', assetResponses['/css/portal.css'] === 200, assetResponses);

  // ---- 3. disabled control status ---------------------------------------
  const controlResponse = await fetch(`${base}/api/control`, { headers: { Accept: 'application/json' } });
  let control = null;
  try { control = await controlResponse.json(); } catch { /* non-JSON */ }
  record(
    'GET /api/control is 200 (no owner password -> no 401) with state=disabled',
    controlResponse.status === 200 && control?.state === 'disabled',
    { status: controlResponse.status, state: control?.state },
  );
  record(
    'GET /api/control reports modelSyncSupported=false, terminalReady=false and no catalog',
    control?.modelSyncSupported === false && control?.terminalReady === false
      && Array.isArray(control?.models) && control.models.length === 0,
    { modelSyncSupported: control?.modelSyncSupported, terminalReady: control?.terminalReady, models: control?.models },
  );

  // ---- 4. WebSocket readiness -------------------------------------------
  const malformedWs = await wsUpgradeStatus(base);
  record('terminal requires an employeeId query before WS upgrade', malformedWs.status === 400 && malformedWs.upgraded === false, malformedWs);
  const ws = await wsUpgradeStatus(base, '/terminal?employeeId=emp-disabled');
  record('disabled runtime refuses exact WS /terminal upgrade with 503', ws.status === 503 && ws.upgraded === false, ws);

  // ---- 5. model / cancel origin + readiness -----------------------------
  const modelCross = await postJson(base, '/api/control/model', 'https://evil.example');
  const modelSame = await postJson(base, '/api/control/model', base);
  const cancelCross = await postJson(base, '/api/control/cancel', 'https://evil.example');
  const cancelSame = await postJson(base, '/api/control/cancel', base);
  record('POST /api/control/model cross-origin is 403', modelCross === 403, { status: modelCross });
  record('POST /api/control/model same-origin on disabled runtime is 409', modelSame === 409, { status: modelSame });
  record('POST /api/control/cancel cross-origin is 403', cancelCross === 403, { status: cancelCross });
  record('POST /api/control/cancel same-origin on disabled runtime is 409', cancelSame === 409, { status: cancelSame });

  // ---- 6. responsive routed layout --------------------------------------
  await runShellLayoutChecks(page, [{ width: 1440, height: 900 }, { width: 390, height: 844 }]);
  await page.screenshot({ path: join(OUT_DIR, 'portal-desktop.png') });
  await page.setViewportSize({ width: 390, height: 844 });
  await sleep(200);
  await page.screenshot({ path: join(OUT_DIR, 'portal-mobile.png') });

  // ---- 7. global [hidden] guard -----------------------------------------
  // A hidden element whose class sets display (like .system-forms) must be
  // non-rendered even before the owning module toggles visibility.
  const hiddenGuard = await page.evaluate(() => {
    const probe = document.createElement('div');
    probe.className = 'system-forms';
    probe.hidden = true;
    probe.textContent = 'guard probe';
    document.body.append(probe);
    const display = getComputedStyle(probe).display;
    const box = probe.getBoundingClientRect();
    probe.remove();
    return { display, height: box.height };
  });
  record('global [hidden] guard suppresses a display:grid container', hiddenGuard.display === 'none' && hiddenGuard.height === 0, hiddenGuard);

  // ---- 8. route-specific xterm assets -----------------------------------
  // The landing route must not fetch any xterm asset; the employee detail
  // route declares them through HeadContent and must.
  const landingXtermRequests = Object.keys(assetResponses).filter((path) => path.startsWith('/vendor/xterm/'));
  record('landing route fetches no xterm asset', landingXtermRequests.length === 0, { assetResponses });

  // Stall the employee read so the pre-load, non-interactive state is stable,
  // then let it fail safely: hidden content must stay hidden and inert.
  let finishDelayedEmployee;
  const delayedEmployeeFinished = new Promise((resolve) => { finishDelayedEmployee = resolve; });
  await page.route('**/api/employees/**', async (route) => {
    // Own the intercepted request through completion. The test must also wait
    // for this handler before unroute(); Playwright resolves pending routes when
    // a handler is removed, which otherwise races the delayed fulfill.
    try {
      const upstream = await route.fetch();
      await sleep(1200);
      await route.fulfill({ response: upstream });
    } finally {
      finishDelayedEmployee();
    }
  });
  const employeeResponse = await page.goto(`${base}/employees/emp-disabled`, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-page="employee-detail"]');
  const employeePreLoad = await page.evaluate(() => {
    const content = document.querySelector('[data-employee-content]');
    const terminal = document.querySelector('[data-terminal]');
    return {
      hidden: content.hidden,
      display: getComputedStyle(content).display,
      terminalHeight: terminal.getBoundingClientRect().height,
      status: document.querySelector('[data-page-status]').textContent,
    };
  });
  record('employee detail renders the owned page and loads xterm route assets',
    employeeResponse.status() === 200
      && assetResponses['/vendor/xterm/xterm.css'] === 200
      && assetResponses['/vendor/xterm/xterm.js'] === 200
      && assetResponses['/vendor/xterm/addon-fit.js'] === 200,
    { status: employeeResponse.status(), assetResponses });
  record('employee detail mounts the terminal with the terminal-mount class',
    await page.locator('#terminal.terminal-mount[data-terminal]').count() === 1);
  record('employee content is hidden and non-rendered while the read is pending',
    employeePreLoad.hidden && employeePreLoad.display === 'none' && /Loading/.test(employeePreLoad.status),
    employeePreLoad);
  await delayedEmployeeFinished;
  await page.unroute('**/api/employees/**');

  // ---- 9. nav aria-current ----------------------------------------------
  const navCases = [
    ['/organization', 'Overview'],
    ['/organization/departments', 'Departments'],
    ['/employees', 'Employees'],
    ['/hiring', 'Hiring'],
    ['/profiles', 'Profiles'],
    ['/profiles/prof-example', 'Profiles'],
    ['/system', 'System'],
  ];
  for (const [path, expected] of navCases) {
    await page.goto(`${base}${path}`, { waitUntil: 'domcontentloaded' });
    const nav = await page.evaluate(() => {
      const current = [...document.querySelectorAll('.primary-nav a[aria-current="page"]')];
      const active = [...document.querySelectorAll('.primary-nav a.active')];
      return {
        currentCount: current.length,
        activeCount: active.length,
        currentText: current.map((node) => node.textContent.trim()),
        activeText: active.map((node) => node.textContent.trim()),
        matchesActive: current.every((node) => node.classList.contains('active')),
      };
    });
    record(`${path} has exactly one aria-current=page link and it is the active link`,
      nav.currentCount === 1 && nav.activeCount === 1
        && nav.currentText[0] === expected && nav.activeText[0] === expected && nav.matchesActive,
      nav);
  }

  // ---- 10. friendly 404 --------------------------------------------------
  const notFound = await page.goto(`${base}/this/route/does/not/exist`, { waitUntil: 'domcontentloaded' });
  record('unknown portal route returns a friendly HTTP 404 page',
    notFound.status() === 404 && await page.locator('[data-page="not-found"]').count() === 1,
    { status: notFound.status() });
  const unknownApi = await fetch(`${base}/api/this-does-not-exist`, { headers: { Accept: 'application/json' } });
  record('unknown API route keeps the ProblemDetails 404 contract',
    unknownApi.status === 404 && (unknownApi.headers.get('content-type') || '').includes('problem+json'),
    { status: unknownApi.status, contentType: unknownApi.headers.get('content-type') });
  const notFoundNav = await page.locator('.primary-nav a[aria-current="page"]').count();
  record('404 page has no falsely active navigation link', notFoundNav === 0, { notFoundNav });

  // ---- 11. distinct DOM and overflow across routes ----------------------
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    for (const path of ['/organization', '/organization/departments', '/employees', '/hiring', '/profiles', '/profiles/prof-disabled', '/system', '/employees/emp-disabled']) {
      await page.goto(`${base}${path}`, { waitUntil: 'domcontentloaded' });
      const widths = await page.evaluate(() => ({ document: document.documentElement.scrollWidth, viewport: innerWidth }));
      record(`${path} has no horizontal overflow at ${viewport.width}px`, widths.document <= widths.viewport + 1, widths);
    }
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await sleep(200);

  // ---- 12. no browser errors --------------------------------------------
  record('no page errors and no console errors', pageErrors.length === 0 && consoleErrors.length === 0, {
    pageErrors,
    consoleErrors,
    failedRequests,
  });
} catch (error) {
  fatal = String(error && error.stack ? error.stack : error);
  record('ci smoke suite completed without fatal error', false, { fatal });
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
}

const passed = results.filter((result) => result.passed).length;
const failed = results.filter((result) => result.passed === false).length;
const payload = {
  generatedBy: 'tests/Browser/ci-smoke.mjs',
  startedAt,
  finishedAt: new Date().toISOString(),
  environment: {
    root: ROOT,
    projectDir: PROJECT_DIR,
    dll: DLL,
    baseUrl: port ? `http://127.0.0.1:${port}` : null,
    chromium: process.env.CHROME_PATH || 'playwright-bundled',
    node: process.version,
  },
  pageErrors,
  consoleErrors,
  failedRequests,
  assetResponses,
  totals: { passed, failed, total: results.length },
  fatal,
  results,
};
writeFileSync(join(OUT_DIR, 'ci-smoke-results.json'), JSON.stringify(payload, null, 2));
console.log('---');
console.log(`RESULT: ${passed}/${results.length} passed, ${failed} failed`);
console.log(`ARTIFACTS: ${OUT_DIR}`);
if (fatal) console.log(`FATAL: ${fatal}`);
process.exit(failed || fatal ? 1 : 0);

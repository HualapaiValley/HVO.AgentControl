// AgentControl V2 live browser portal smoke test.
//
// Drives the authenticated portal through a loopback-only SSH tunnel
// (home-dev-01:15054 -> home-docker:5054) against the real running container.
//
// Coverage:
//   1. unauthenticated GET /api/control -> 401 (and root page -> 401)
//   2. authenticated root Blazor page loads (title, no console/page errors)
//   3. GET /api/control -> ready, non-empty sessionId, terminalReady=true
//   4. xterm renders live OpenCode TUI output
//   5. browser keyboard submits an arithmetic prompt and the assistant reply
//      is verified in the terminal (and is provably not the prompt echo)
//   6. detach + reconnect keeps the same native sessionId
//   7. desktop + mobile (390x844) resize with no horizontal overflow
//   8. POST /api/control/cancel: cross-origin Origin -> 403, same-origin -> 202
//
// The owner password is read at runtime into memory only (never printed):
//   1) $OWNER_PASSWORD_FILE, else
//   2) <repo>/.secrets/owner-password, else
//   3) `docker --context home-docker exec agentcontrol-v2-control-1 cat
//       /run/agentcontrol-secrets/owner-password`
//
// Usage: node tests/Browser/portal-smoke.mjs
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const ROOT = (process.env.REPO_ROOT || fileURLToPath(new URL('../../', import.meta.url))).replace(/\/$/, '');
const OUT_DIR = process.env.ARTIFACTS_DIR || `${ROOT}/artifacts/portal-smoke`;
const BASE = (process.env.BASE_URL || 'http://127.0.0.1:15054').replace(/\/$/, '');
// Optional explicit browser; unset -> Chromium bundled by the `playwright` dep.
const EXE = process.env.CHROME_PATH || undefined;
const DOCKER_CONTEXT = process.env.DOCKER_CONTEXT || 'home-docker';
const CONTAINER = process.env.CONTROL_CONTAINER || 'agentcontrol-v2-control-1';
const SECRET_IN_CONTAINER = '/run/agentcontrol-secrets/owner-password';
const PAGE_TITLE = 'AgentControl V2';
const OPENCODE_MARKER = /OpenCode Zen|Big Pickle|ctrl\+p commands/;

// The task example is 512+137 -> 649. Fallbacks keep the assertion meaningful
// if that answer is already visible from an earlier turn in the same session.
const PROMPT_CANDIDATES = [
  { question: 'What is 512+137? Reply only the number.', answer: '649' },
  { question: 'What is 431+476? Reply only the number.', answer: '907' },
  { question: 'What is 483+529? Reply only the number.', answer: '1012' },
];

const results = [];
const record = (name, passed, detail = {}) => {
  results.push({ name, passed: !!passed, detail });
  console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${Object.keys(detail).length ? '  :: ' + JSON.stringify(detail) : ''}`);
};

function loadOwnerPassword() {
  const file = process.env.OWNER_PASSWORD_FILE;
  if (file && existsSync(file)) return readFileSync(file, 'utf8').trim();
  const local = `${ROOT}/.secrets/owner-password`;
  if (existsSync(local)) return readFileSync(local, 'utf8').trim();
  return execFileSync(
    'docker',
    ['--context', DOCKER_CONTEXT, 'exec', CONTAINER, 'cat', SECRET_IN_CONTAINER],
    { encoding: 'utf8' },
  ).trim();
}

const password = loadOwnerPassword();
if (!password) throw new Error('owner password could not be read');
const basicAuth = 'Basic ' + Buffer.from(`owner:${password}`, 'utf8').toString('base64');

async function getControl() {
  const response = await fetch(`${BASE}/api/control`, { headers: { Authorization: basicAuth, Accept: 'application/json' } });
  let body = null;
  try { body = await response.json(); } catch { /* non-JSON */ }
  return { status: response.status, body };
}

async function waitFor(fn, timeoutMs, label, intervalMs = 500) {
  const deadline = Date.now() + timeoutMs;
  let last;
  for (;;) {
    try { last = await fn(); } catch (error) { last = { error: String(error && error.message) }; }
    if (last) return last;
    if (Date.now() >= deadline) throw new Error(`timeout waiting for ${label} (${timeoutMs}ms)`);
    await sleep(intervalMs);
  }
}

const readTerminal = (page) => page.evaluate(() => document.querySelector('.xterm-rows')?.textContent || '');
const fieldText = (page, field) => page.evaluate((f) => document.querySelector(`[data-field="${f}"]`)?.textContent?.trim() ?? null, field);
const overflow = (page) => page.evaluate(() => ({
  docW: document.documentElement.scrollWidth,
  bodyW: document.body.scrollWidth,
  innerW: window.innerWidth,
}));

mkdirSync(OUT_DIR, { recursive: true });
const startedAt = new Date().toISOString();
const screenshots = {};
let sessionId = null;
let usedPrompt = null;
let fatal = null;

const browser = await chromium.launch({ ...(EXE ? { executablePath: EXE } : {}), headless: true, args: ['--no-sandbox'] });
const context = await browser.newContext({
  viewport: { width: 1440, height: 900 },
  httpCredentials: { username: 'owner', password },
});
const page = await context.newPage();
const consoleErrors = [];
const pageErrors = [];
const failedRequests = [];
page.on('console', (message) => { if (message.type() === 'error') consoleErrors.push(message.text()); });
page.on('pageerror', (error) => pageErrors.push(String(error && error.message ? error.message : error)));
page.on('requestfailed', (request) => failedRequests.push(`${request.method()} ${request.url()} ${request.failure()?.errorText}`));

try {
  // ---- 1. unauthenticated access --------------------------------------
  const anonApi = await fetch(`${BASE}/api/control`);
  const anonAuthHeader = anonApi.headers.get('www-authenticate') || '';
  record('unauthenticated GET /api/control returns 401',
    anonApi.status === 401, { status: anonApi.status, wwwAuthenticate: anonAuthHeader });
  const anonRoot = await fetch(`${BASE}/`);
  record('unauthenticated GET / returns 401', anonRoot.status === 401, { status: anonRoot.status });

  // ---- 2. authenticated root page -------------------------------------
  const response = await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded', timeout: 30000 });
  const title = await page.title();
  await page.waitForSelector('[data-portal]', { timeout: 15000 });
  record('authenticated root page returns HTTP 200', (response?.status() ?? 0) === 200, { status: response?.status() ?? 0 });
  record(`root page title is "${PAGE_TITLE}"`, title === PAGE_TITLE, { title });
  record('portal root and xterm mount exist', await page.evaluate(() =>
    Boolean(document.querySelector('[data-portal]') && document.querySelector('[data-terminal]'))), {});
  record('no console errors or page errors on root page',
    consoleErrors.length === 0 && pageErrors.length === 0,
    { consoleErrors, pageErrors });

  // ---- 3. authenticated status ----------------------------------------
  await waitFor(async () => {
    const { body } = await getControl();
    return body && body.state === 'ready' ? body : null;
  }, 30000, 'runtime state ready');
  const control = await getControl();
  sessionId = control.body?.sessionId ?? null;
  record('/api/control ready with non-empty sessionId',
    control.status === 200 && control.body?.state === 'ready' && typeof sessionId === 'string' && sessionId.length > 0,
    { state: control.body?.state, sessionId, model: control.body?.model });
  record('/api/control reports terminalReady=true', control.body?.terminalReady === true,
    { terminalReady: control.body?.terminalReady });
  record('UI reflects ready state and attached terminal',
    (await fieldText(page, 'state'))?.toLowerCase() === 'ready' &&
    (await fieldText(page, 'connection')) === 'Attached',
    { state: await fieldText(page, 'state'), connection: await fieldText(page, 'connection') });

  // ---- 4. live OpenCode terminal --------------------------------------
  await waitFor(async () => (OPENCODE_MARKER.test(await readTerminal(page)) ? true : null),
    30000, 'live OpenCode TUI output');
  record('xterm renders live OpenCode TUI output', true, { marker: String(OPENCODE_MARKER) });
  screenshots.live = `${OUT_DIR}/portal-terminal-live.png`;
  await page.screenshot({ path: screenshots.live });

  // ---- 5. arithmetic prompt via browser keyboard ----------------------
  const before = await readTerminal(page);
  usedPrompt = PROMPT_CANDIDATES.find((candidate) => !before.includes(candidate.answer)) || null;
  if (!usedPrompt) throw new Error('all candidate answers already present before prompting');
  record('assistant answer is not confused with the prompt text',
    !usedPrompt.question.includes(usedPrompt.answer),
    { question: usedPrompt.question, answer: usedPrompt.answer, chosen: usedPrompt === PROMPT_CANDIDATES[0] ? 'primary' : 'fallback' });
  record('chosen answer is absent from the terminal before prompting',
    !before.includes(usedPrompt.answer), { answer: usedPrompt.answer, terminalChars: before.length });

  // Clear any leftover input, focus the pane and type the prompt.
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Control+k');
  const screen = await page.locator('.xterm-screen').boundingBox();
  if (screen) await page.mouse.click(screen.x + screen.width / 2, screen.y + screen.height - 60);
  const expression = usedPrompt.question.match(/\d+\s*\+\s*\d+/)?.[0] || usedPrompt.question.slice(0, 12);
  await page.keyboard.type(usedPrompt.question, { delay: 30 });
  await waitFor(async () => ((await readTerminal(page)).includes(expression) ? true : null),
    8000, 'prompt text visible in terminal');
  await page.screenshot({ path: `${OUT_DIR}/portal-terminal-typed.png` });
  await page.keyboard.press('Enter');

  await waitFor(async () => ((await readTerminal(page)).includes(usedPrompt.answer) ? true : null),
    120000, `assistant answer ${usedPrompt.answer} in terminal`);
  const after = await readTerminal(page);
  const occurrences = after.split(usedPrompt.answer).length - 1;
  record(`browser keyboard submitted arithmetic prompt; terminal shows assistant reply ${usedPrompt.answer}`,
    after.includes(usedPrompt.answer) && !before.includes(usedPrompt.answer),
    { question: usedPrompt.question, answer: usedPrompt.answer, occurrences, notPromptEcho: true });
  screenshots.answer = `${OUT_DIR}/portal-terminal-answer.png`;
  await page.screenshot({ path: screenshots.answer });

  // ---- 6. detach / reconnect keeps the same session -------------------
  const sidBefore = (await getControl()).body?.sessionId;
  await page.click('[data-action="detach"]');
  await waitFor(async () => ((await fieldText(page, 'connection')) === 'Detached' ? true : null), 8000, 'connection Detached');
  const detachedControl = await getControl();
  record('detach leaves the native session id unchanged',
    detachedControl.body?.sessionId === sidBefore, { before: sidBefore, detached: detachedControl.body?.sessionId });

  await page.click('[data-action="reconnect"]');
  await waitFor(async () => ((await fieldText(page, 'connection')) === 'Attached' ? true : null), 30000, 'connection Attached');
  await waitFor(async () => (OPENCODE_MARKER.test(await readTerminal(page)) ? true : null), 30000, 'terminal redraw after reconnect');
  const reattached = await getControl();
  const reattachedText = await readTerminal(page);
  record('reconnect reattaches to the SAME native sessionId',
    sidBefore && reattached.body?.sessionId === sidBefore,
    { before: sidBefore, after: reattached.body?.sessionId });
  record('reconnect restores same-session terminal content',
    reattachedText.includes(usedPrompt.answer), { answerVisible: reattachedText.includes(usedPrompt.answer) });
  screenshots.reconnect = `${OUT_DIR}/portal-terminal-reconnect.png`;
  await page.screenshot({ path: screenshots.reconnect });

  // ---- 7. responsive layout / no horizontal overflow ------------------
  const viewports = [
    { label: 'desktop', width: 1440, height: 900, shot: `${OUT_DIR}/portal-desktop.png` },
    { label: 'mobile', width: 390, height: 844, shot: `${OUT_DIR}/portal-mobile.png` },
    { label: 'narrow', width: 320, height: 720, shot: `${OUT_DIR}/portal-narrow.png` },
  ];
  for (const vp of viewports) {
    await page.setViewportSize({ width: vp.width, height: vp.height });
    await sleep(600);
    const metric = await overflow(page);
    record(`no horizontal overflow ${vp.label} ${vp.width}x${vp.height}`,
      metric.docW <= metric.innerW + 1 && metric.bodyW <= metric.innerW + 1,
      metric);
    await page.screenshot({ path: vp.shot, fullPage: vp.label !== 'desktop' });
    screenshots[vp.label] = vp.shot;
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await sleep(400);

  // ---- 8. cancel endpoint origin policy -------------------------------
  const cancelHeadersBase = { Authorization: basicAuth, 'Content-Type': 'application/json', Accept: 'application/json' };
  const crossOrigin = await fetch(`${BASE}/api/control/cancel`, {
    method: 'POST',
    headers: { ...cancelHeadersBase, Origin: 'https://evil.example' },
    body: '{}',
  });
  record('cancel endpoint rejects cross-origin Origin with 403',
    crossOrigin.status === 403, { status: crossOrigin.status, origin: 'https://evil.example' });
  const sameOrigin = await fetch(`${BASE}/api/control/cancel`, {
    method: 'POST',
    headers: { ...cancelHeadersBase, Origin: BASE },
    body: '{}',
  });
  let sameOriginBody = null;
  try { sameOriginBody = await sameOrigin.json(); } catch { /* non-JSON */ }
  record('cancel endpoint accepts same-origin POST with 202',
    sameOrigin.status === 202, { status: sameOrigin.status, body: sameOriginBody });
  record('cancel response reports a receipt, not task success',
    sameOriginBody?.status === 'cancel-requested', { body: sameOriginBody, origin: BASE });
} catch (error) {
  fatal = String(error && error.stack ? error.stack : error);
  record('portal smoke suite completed without fatal error', false, { fatal });
  try { await page.screenshot({ path: `${OUT_DIR}/portal-failure.png` }); screenshots.failure = `${OUT_DIR}/portal-failure.png`; } catch { /* ignore */ }
} finally {
  await browser.close();
}

const passed = results.filter((result) => result.passed).length;
const failed = results.filter((result) => result.passed === false).length;
const payload = {
  generatedBy: 'tests/Browser/portal-smoke.mjs',
  startedAt,
  finishedAt: new Date().toISOString(),
  environment: {
    baseUrl: BASE,
    dockerContext: DOCKER_CONTEXT,
    container: CONTAINER,
    chromium: EXE,
    tunnel: 'home-dev-01 127.0.0.1:15054 -> home-docker 127.0.0.1:5054 (loopback only)',
  },
  sessionId,
  usedPrompt,
  consoleErrors,
  pageErrors,
  failedRequests,
  screenshots,
  totals: { passed, failed, total: results.length },
  fatal,
  results,
};
writeFileSync(`${OUT_DIR}/portal-smoke-results.json`, JSON.stringify(payload, null, 2));
console.log('---');
console.log(`RESULT: ${passed}/${results.length} passed, ${failed} failed`);
console.log(`ARTIFACTS: ${OUT_DIR}`);
if (consoleErrors.length || pageErrors.length) console.log('browser errors:', JSON.stringify({ consoleErrors, pageErrors }));
process.exit(failed || fatal ? 1 : 0);

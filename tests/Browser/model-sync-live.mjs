// AgentControl V2 LIVE model-sync verification against the running container.
//
// This suite talks to the real deployed runtime through the loopback-only SSH
// tunnel (home-dev-01 http://127.0.0.1:15054 -> home-docker 127.0.0.1:5054)
// and to the real OpenCode TUI inside the `agentcontrol` tmux session.
//
// It verifies BOTH sync directions:
//   direction 1 portal -> runtime -> TUI
//     select another advertised connected model in [data-model-select];
//     expect POST /api/control/model to be accepted, /api/control.model to
//     become that model, and the tmux bottom "Agentcontrol . <Model>" label
//     to change.
//   direction 2 TUI -> runtime -> portal
//     drive the ACTUAL TUI model picker (tmux send-keys: ctrl+x m, type the
//     model name, Enter); expect /api/control.model and the web header /
//     dropdown to reflect the selection within the 2s status-poll window.
//
// Additional contract checks: an empty/malformed selector -> 400, a
// cross-origin POST -> 403, and the native sessionId must not change.
//
// No inference prompt is ever typed and no setting other than the model is
// touched. In `finally` the original model is restored through the portal API
// (the same call the UI makes); if the deployed portal path cannot confirm the
// write it falls back to the native HTTP model endpoint so the runtime is left
// on the original model. The native endpoint is supplemental evidence only and
// is never reported as a TUI pass.
//
// The owner password is read at runtime into memory only (never printed):
//   1) $OWNER_PASSWORD_FILE, else
//   2) <repo>/.secrets/owner-password, else
//   3) `docker --context home-docker exec <container> cat
//       /run/agentcontrol-secrets/owner-password`
//
// Usage: node tests/Browser/model-sync-live.mjs
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const ROOT = (process.env.REPO_ROOT || fileURLToPath(new URL('../../', import.meta.url))).replace(/\/$/, '');
const OUT_DIR = process.env.ARTIFACTS_DIR || `${ROOT}/artifacts/model-sync-live`;
const BASE = (process.env.BASE_URL || 'http://127.0.0.1:15054').replace(/\/$/, '');
// Optional explicit browser; unset -> Chromium bundled by the `playwright` dep.
const EXE = process.env.CHROME_PATH || undefined;
const DOCKER_CONTEXT = process.env.DOCKER_CONTEXT || 'home-docker';
const CONTAINER = process.env.CONTROL_CONTAINER || 'agentcontrol-v2-control-1';
const TMUX_SESSION = process.env.TMUX_SESSION || 'agentcontrol';
const SECRET_IN_CONTAINER = '/run/agentcontrol-secrets/owner-password';
const TARGET_MODEL = process.env.TARGET_MODEL || 'opencode/mimo-v2.5-free';
const API_CONTROL = `${BASE}/api/control`;
const API_MODEL = `${BASE}/api/control/model`;
const MODEL_POLL_MS = Number(process.env.MODEL_POLL_MS || 12000);

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

async function waitFor(fn, timeoutMs, label, intervalMs = 250) {
  const deadline = Date.now() + timeoutMs;
  let last;
  for (;;) {
    try { last = await fn(); } catch (error) { last = { error: String(error && error.message) }; }
    if (last) return last;
    if (Date.now() >= deadline) throw new Error(`timeout waiting for ${label} (${timeoutMs}ms)`);
    await sleep(intervalMs);
  }
}

async function getJson(url) {
  const response = await fetch(url, { headers: { Authorization: basicAuth, Accept: 'application/json' } });
  let body = null;
  try { body = await response.json(); } catch { /* non-JSON */ }
  return { status: response.status, body };
}

async function getControl() {
  return getJson(API_CONTROL);
}

async function postControlModel(model, origin) {
  const response = await fetch(API_MODEL, {
    method: 'POST',
    headers: {
      Authorization: basicAuth,
      'Content-Type': 'application/json',
      Accept: 'application/json',
      ...(origin ? { Origin: origin } : {}),
    },
    body: JSON.stringify({ model }),
  });
  let body = null;
  try { body = await response.json(); } catch { /* non-JSON */ }
  return { status: response.status, body };
}

// --- docker / tmux helpers --------------------------------------------------

function dockerExec(args, input) {
  return execFileSync('docker', ['--context', DOCKER_CONTEXT, 'exec', '-i', CONTAINER, ...args], {
    encoding: 'utf8',
    input,
    maxBuffer: 8 * 1024 * 1024,
  });
}

function tmuxCapture() {
  return dockerExec(['tmux', 'capture-pane', '-pt', TMUX_SESSION]);
}

function tmuxKeys(keys) {
  return dockerExec(['tmux', 'send-keys', '-t', TMUX_SESSION, ...keys]);
}

function tmuxLiteral(text) {
  return dockerExec(['tmux', 'send-keys', '-t', TMUX_SESSION, '-l', text]);
}

// The bottom footer reads: "Agentcontrol . Big Pickle OpenCode Zen".
function parseTuiModel(text) {
  const lines = String(text || '').split('\n');
  for (let i = lines.length - 1; i >= 0; i -= 1) {
    const match = lines[i].match(/Agentcontrol\s*[·•]\s*(.*?)\s*OpenCode Zen/);
    if (match) return match[1].trim();
  }
  return null;
}

// Supplemental native HTTP API, executed inside the container where the
// OpenCode server binds to loopback. Credentials are read from the live
// process environment and never printed.
function nativeRequest(method, path, bodyObj) {
  const script = `
import json, os, base64, urllib.request, urllib.error
method = ${JSON.stringify(method)}
path = ${JSON.stringify(path)}
body = ${JSON.stringify(bodyObj)}
out = {"status": None, "body": None, "error": None}
def creds():
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            raw = open("/proc/%s/environ" % pid, "rb").read().split(b"\\0")
        except Exception:
            continue
        env = dict(x.split(b"=", 1) for x in raw if b"=" in x)
        if b"OPENCODE_SERVER_PASSWORD" in env and b"OPENCODE_SERVER_USERNAME" in env:
            return env[b"OPENCODE_SERVER_USERNAME"].decode(), env[b"OPENCODE_SERVER_PASSWORD"].decode()
    return None
c = creds()
if c is None:
    out["error"] = "opencode server credentials not found"
else:
    user, pw = c
    auth = "Basic " + base64.b64encode(("%s:%s" % (user, pw)).encode()).decode()
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request("http://127.0.0.1:4096" + path, data=data, method=method)
    req.add_header("Authorization", auth)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        resp = urllib.request.urlopen(req, timeout=15)
        out["status"] = resp.status
        text = resp.read().decode("utf-8", "replace")
        try:
            out["body"] = json.loads(text) if text else None
        except Exception:
            out["body"] = text[:500]
    except urllib.error.HTTPError as exc:
        out["status"] = exc.code
        try:
            out["body"] = json.loads(exc.read().decode("utf-8", "replace"))
        except Exception:
            out["body"] = None
    except Exception as exc:
        out["error"] = str(exc)
print(json.dumps(out))
`;
  const raw = dockerExec(['python3', '-'], script);
  const line = raw.trim().split('\n').filter(Boolean).pop() || '{}';
  try { return JSON.parse(line); } catch { return { status: null, body: null, error: 'unparseable native response' }; }
}

function nativeSetModel(sessionId, reference) {
  const slash = reference.indexOf('/');
  const providerID = reference.slice(0, slash);
  const id = reference.slice(slash + 1);
  return nativeRequest('POST', `/api/session/${encodeURIComponent(sessionId)}/model`, {
    model: { id, providerID },
  });
}

// --- TUI model picker automation -------------------------------------------

// Opens the real TUI model dialog with the documented ctrl+x m leader, filters
// by name and confirms with Enter. Returns the dialog capture used for
// evidence. This is the actual TUI picker, not the native HTTP endpoint.
async function tuiSelectModel(displayName) {
  tmuxKeys(['Escape']);
  await sleep(300);
  tmuxKeys(['C-x', 'm']);
  const dialog = await waitFor(async () => {
    const text = tmuxCapture();
    return /Select model/.test(text) ? text : null;
  }, 6000, 'TUI Select model dialog');
  tmuxKeys(['C-u']);
  await sleep(150);
  tmuxLiteral(displayName);
  await sleep(800);
  const filtered = tmuxCapture();
  tmuxKeys(['Enter']);
  await sleep(1800);
  return { dialog, filtered, showsTarget: filtered.includes(displayName) };
}

// ---------------------------------------------------------------------------

mkdirSync(OUT_DIR, { recursive: true });
const startedAt = new Date().toISOString();
const observations = {
  environment: {
    baseUrl: BASE,
    dockerContext: DOCKER_CONTEXT,
    container: CONTAINER,
    tmuxSession: TMUX_SESSION,
    chromium: EXE,
    targetModel: TARGET_MODEL,
  },
  tui: {},
  direction1: {},
  direction2: {},
  nativeSupplemental: {},
  restore: {},
  errors: {},
};
const screenshots = {};
let originalModel = null;
let originalModelName = null;
let originalSessionId = null;

const browser = await chromium.launch({ ...(EXE ? { executablePath: EXE } : {}), headless: true, args: ['--no-sandbox'] });
const context = await browser.newContext({
  viewport: { width: 1440, height: 900 },
  httpCredentials: { username: 'owner', password },
});
const page = await context.newPage();
const consoleErrors = [];
const pageErrors = [];
page.on('console', (message) => { if (message.type() === 'error') consoleErrors.push(message.text()); });
page.on('pageerror', (error) => pageErrors.push(String(error && error.message ? error.message : error)));

const headerModel = () => page.$eval('[data-field="model"]', (el) => el.textContent.trim());
const selectValue = () => page.$eval('[data-model-select]', (el) => el.value);
const selectDisabled = () => page.$eval('[data-model-select]', (el) => el.disabled);
const receipt = () => page.$eval('[data-model-receipt]', (el) => ({
  text: el.textContent.trim(),
  status: el.dataset.status || '',
  hidden: el.hidden,
}));

async function waitReceiptSettled(timeoutMs = MODEL_POLL_MS) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  for (;;) {
    last = await receipt().catch(() => null);
    if (last && (last.status === 'ok' || last.status === 'error')) return last;
    if (Date.now() >= deadline) return last;
    await sleep(200);
  }
}

async function waitControlModel(expected, timeoutMs = MODEL_POLL_MS) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  for (;;) {
    const { body } = await getControl().catch(() => ({ body: null }));
    last = body;
    if (body && body.model === expected) return { matched: true, at: new Date().toISOString(), model: body.model, sessionId: body.sessionId };
    if (Date.now() >= deadline) return { matched: false, model: body?.model ?? null, sessionId: body?.sessionId ?? null };
    await sleep(300);
  }
}

async function waitWebModel(expected, timeoutMs = MODEL_POLL_MS) {
  const deadline = Date.now() + timeoutMs;
  let last = {};
  for (;;) {
    last = {
      header: await headerModel().catch(() => null),
      select: await selectValue().catch(() => null),
    };
    if (last.header === expected && last.select === expected) return { matched: true, ...last };
    if (Date.now() >= deadline) return { matched: false, ...last };
    await sleep(300);
  }
}

try {
  const organization = await getJson(`${BASE}/api/organization/portal`);
  const hostEmployee = organization.body?.employees?.find((employee) => employee.runtime?.hostOwned === true);
  if (!hostEmployee?.id) throw new Error('host-owned employee was not found');
  await page.goto(`${BASE}/employees/${encodeURIComponent(hostEmployee.id)}`, { waitUntil: 'domcontentloaded', timeout: 30000 });
  await page.waitForSelector('[data-portal]', { timeout: 15000 });
  await waitFor(async () => {
    const { body } = await getControl();
    return body && body.state === 'ready' && body.terminalReady === true ? true : null;
  }, 30000, 'runtime ready');
  await waitFor(async () => ((await selectValue()) ? true : null), 15000, 'model dropdown populated');

  const initial = await getControl();
  originalModel = initial.body?.model ?? null;
  originalSessionId = initial.body?.sessionId ?? null;
  const catalog = Array.isArray(initial.body?.models) ? initial.body.models : [];
  const originalEntry = catalog.find((entry) => entry.id === originalModel) || null;
  originalModelName = originalEntry?.name ?? (originalModel || '').split('/').pop();
  const targetEntry = catalog.find((entry) => entry.id === TARGET_MODEL) || null;

  observations.initial = {
    state: initial.body?.state,
    model: originalModel,
    modelName: originalModelName,
    sessionId: originalSessionId,
    sessionState: initial.body?.sessionState,
    terminalReady: initial.body?.terminalReady,
    catalog,
    selectEnabled: !(await selectDisabled()),
    selectValue: await selectValue(),
    headerModel: await headerModel(),
  };
  observations.tui.baseline = parseTuiModel(tmuxCapture());
  screenshots.before = `${OUT_DIR}/portal-before.png`;
  await page.screenshot({ path: screenshots.before });

  record('runtime reports the expected original model opencode/big-pickle',
    originalModel === 'opencode/big-pickle', { originalModel, originalModelName });
  record(`target model ${TARGET_MODEL} is advertised and connected`,
    Boolean(targetEntry), { targetEntry, catalogIds: catalog.map((m) => m.id) });
  record('portal dropdown is enabled and shows the authoritative original model',
    !(await selectDisabled()) && (await selectValue()) === originalModel && (await headerModel()) === originalModel,
    { disabled: await selectDisabled(), select: await selectValue(), header: await headerModel() });
  if (!targetEntry) throw new Error(`target model ${TARGET_MODEL} is not in the advertised catalog`);

  // ---- contract checks: invalid selector + cross-origin ------------------
  const emptySelector = await postControlModel('', BASE);
  record('invalid selector (empty model) returns 400', emptySelector.status === 400,
    { status: emptySelector.status, body: emptySelector.body });
  const malformedSelector = await postControlModel('no-provider-slash', BASE);
  record('invalid selector (no provider/model) returns 400', malformedSelector.status === 400,
    { status: malformedSelector.status, body: malformedSelector.body });
  const crossOrigin = await postControlModel(TARGET_MODEL, 'https://evil.example');
  record('cross-origin model POST returns 403', crossOrigin.status === 403,
    { status: crossOrigin.status, origin: 'https://evil.example' });
  observations.contractChecks = {
    emptySelector: { status: emptySelector.status, body: emptySelector.body },
    malformedSelector: { status: malformedSelector.status, body: malformedSelector.body },
    crossOrigin: { status: crossOrigin.status, body: crossOrigin.body },
  };

  // ---- direction 1: portal -> runtime -> TUI -----------------------------
  const [modelResponse] = await Promise.all([
    page.waitForResponse((response) => response.url().endsWith('/api/control/model')
      && response.request().method() === 'POST', { timeout: MODEL_POLL_MS }),
    page.selectOption('[data-model-select]', TARGET_MODEL),
  ]);
  let portalPostBody = null;
  try { portalPostBody = await modelResponse.json(); } catch { portalPostBody = null; }
  const settled = await waitReceiptSettled();
  const apiD1 = await waitControlModel(TARGET_MODEL);
  const tmuxD1 = parseTuiModel(tmuxCapture());
  const controlD1 = await getControl();
  observations.direction1 = {
    targetModel: TARGET_MODEL,
    targetName: targetEntry.name,
    portalPostStatus: modelResponse.status(),
    portalPostBody,
    receipt: settled,
    api: apiD1,
    tmuxLabel: tmuxD1,
    sessionIdBefore: originalSessionId,
    sessionIdAfter: controlD1.body?.sessionId ?? null,
  };
  screenshots.afterDirection1 = `${OUT_DIR}/portal-after-direction1.png`;
  await page.screenshot({ path: screenshots.afterDirection1 });
  writeFileSync(`${OUT_DIR}/tmux-direction1.txt`, tmuxCapture());

  record('direction 1: portal POST /api/control/model accepted by the runtime',
    modelResponse.status() === 200, { status: modelResponse.status(), body: portalPostBody });
  record('direction 1: portal receipt reports runtime confirmation',
    settled?.status === 'ok', { receipt: settled });
  record('direction 1: /api/control.model reflects the selected model',
    apiD1.matched === true, { expected: TARGET_MODEL, observed: apiD1.model, waitedMs: MODEL_POLL_MS });
  record('direction 1: tmux bottom model label reflects the selected model',
    tmuxD1 === targetEntry.name, { expected: targetEntry.name, observed: tmuxD1 });

  // ---- direction 2: TUI picker -> runtime -> portal ----------------------
  // Seed the runtime to the target first (native endpoint is supplemental
  // setup only) so the TUI picker has a real change to make back to original.
  const beforeD2 = await getControl();
  let d2Seed = { usedNative: false, before: beforeD2.body?.model ?? null };
  if (beforeD2.body?.model !== TARGET_MODEL) {
    const seeded = nativeSetModel(originalSessionId, TARGET_MODEL);
    d2Seed = { usedNative: true, before: beforeD2.body?.model ?? null, nativeStatus: seeded.status, error: seeded.error || null };
    const seedWait = await waitControlModel(TARGET_MODEL);
    d2Seed.seedObserved = seedWait.model;
    d2Seed.tmuxLabelAfterSeed = parseTuiModel(tmuxCapture());
  }
  observations.nativeSupplemental.direction2Seed = d2Seed;
  // Supplemental evidence only: the native HTTP endpoint can move the runtime's
  // observed model. It is explicitly NOT counted as a TUI picker pass. Only
  // relevant when the portal path above failed to move the runtime.
  if (d2Seed.usedNative) {
    record('supplemental (not a TUI pass): native HTTP model write reaches /api/control.model',
      d2Seed.seedObserved === TARGET_MODEL,
      { nativeStatus: d2Seed.nativeStatus, seedObserved: d2Seed.seedObserved });
    record('supplemental (not a TUI pass): native HTTP model write does not move the TUI label',
      d2Seed.tmuxLabelAfterSeed !== targetEntry.name,
      { expectedTuiLabel: targetEntry.name, observedTuiLabel: d2Seed.tmuxLabelAfterSeed ?? observations.tui.baseline });
  }

  const picker = await tuiSelectModel(originalModelName);
  const tmuxD2 = parseTuiModel(tmuxCapture());
  const apiD2 = await waitControlModel(originalModel);
  const webD2 = await waitWebModel(originalModel);
  const controlD2 = await getControl();
  observations.direction2 = {
    originalModel,
    originalName: originalModelName,
    seed: d2Seed,
    pickerShowsTarget: picker.showsTarget,
    api: apiD2,
    web: webD2,
    tmuxLabel: tmuxD2,
    sessionIdBefore: originalSessionId,
    sessionIdAfter: controlD2.body?.sessionId ?? null,
  };
  screenshots.afterDirection2 = `${OUT_DIR}/portal-after-direction2.png`;
  await page.screenshot({ path: screenshots.afterDirection2 });
  writeFileSync(`${OUT_DIR}/tmux-direction2.txt`, tmuxCapture());
  writeFileSync(`${OUT_DIR}/tui-picker-direction2.txt`, `${picker.dialog}\n---FILTERED---\n${picker.filtered}`);

  record('direction 2: real TUI model picker opened and targeted the original model',
    picker.showsTarget === true, { originalName: originalModelName, pickerShowsTarget: picker.showsTarget });
  record('direction 2: TUI bottom model label shows the original model after TUI selection',
    tmuxD2 === originalModelName, { expected: originalModelName, observed: tmuxD2 });
  record('direction 2: /api/control.model reflects the TUI selection within the poll window',
    apiD2.matched === true, { expected: originalModel, observed: apiD2.model, waitedMs: MODEL_POLL_MS });
  record('direction 2: web header reflects the TUI selection within the poll window',
    webD2.header === originalModel, { expected: originalModel, observed: webD2.header });
  record('direction 2: web dropdown reflects the TUI selection within the poll window',
    webD2.select === originalModel, { expected: originalModel, observed: webD2.select });

  // ---- same native session throughout ------------------------------------
  const afterAll = await getControl();
  record('native sessionId is unchanged after model changes',
    Boolean(originalSessionId) && afterAll.body?.sessionId === originalSessionId,
    { before: originalSessionId, after: afterAll.body?.sessionId });
  const headerSession = await page.$eval('[data-field="sessionId"]', (el) => el.textContent.trim());
  record('web header and dropdown still belong to the same session view',
    headerSession === originalSessionId,
    { expected: originalSessionId, headerSession });
} catch (error) {
  observations.errors.fatal = String(error && error.stack ? error.stack : error);
  record('model-sync live suite completed without fatal error', false, { fatal: observations.errors.fatal });
  try { await page.screenshot({ path: `${OUT_DIR}/portal-failure.png` }); screenshots.failure = `${OUT_DIR}/portal-failure.png`; } catch { /* ignore */ }
} finally {
  // ---- restore the original model ---------------------------------------
  try {
    const portalRestore = await postControlModel(originalModel, BASE);
    observations.restore.portal = { status: portalRestore.status, body: portalRestore.body };
    let restored = await waitControlModel(originalModel, 8000);
    if (!restored.matched) {
      // The deployed portal path may not confirm the write; the native endpoint
      // is used only to leave the runtime on the original model.
      const nativeRestore = originalSessionId ? nativeSetModel(originalSessionId, originalModel) : { status: null };
      observations.restore.nativeFallback = { status: nativeRestore.status, body: nativeRestore.body, error: nativeRestore.error || null };
      restored = await waitControlModel(originalModel, 8000);
    }
    observations.restore.restoredModel = restored.model;
    observations.restore.matched = restored.matched;
    // Re-sync the TUI's own label to the original model (model-only action).
    try {
      const picker = await tuiSelectModel(originalModelName);
      observations.restore.tui = { pickerShowsTarget: picker.showsTarget, tmuxLabel: parseTuiModel(tmuxCapture()) };
    } catch (error) {
      observations.restore.tui = { error: String(error && error.message ? error.message : error) };
    }
    observations.restore.finalControl = await getControl();
    record('finally: runtime restored to the original model',
      restored.matched === true, { originalModel, restoredModel: restored.model });
  } catch (error) {
    observations.restore.error = String(error && error.message ? error.message : error);
    record('finally: runtime restored to the original model', false, { error: observations.restore.error });
  }
  await browser.close();
}

const passed = results.filter((result) => result.passed).length;
const failed = results.filter((result) => result.passed === false).length;
const payload = {
  generatedBy: 'tests/Browser/model-sync-live.mjs',
  startedAt,
  finishedAt: new Date().toISOString(),
  environment: observations.environment,
  originalModel,
  originalModelName,
  originalSessionId,
  consoleErrors,
  pageErrors,
  screenshots,
  observations,
  totals: { passed, failed, total: results.length },
  results,
};
writeFileSync(`${OUT_DIR}/model-sync-live-results.json`, JSON.stringify(payload, null, 2));
console.log('---');
console.log(`RESULT: ${passed}/${results.length} passed, ${failed} failed`);
console.log(`ARTIFACTS: ${OUT_DIR}`);
if (consoleErrors.length || pageErrors.length) console.log('browser errors:', JSON.stringify({ consoleErrors, pageErrors }));
process.exit(failed || observations.errors.fatal ? 1 : 0);

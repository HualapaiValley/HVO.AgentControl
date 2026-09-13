const { chromium, expect } = require('@playwright/test');
const fs = require('node:fs');
const path = require('node:path');
const { selectRuntime } = require('./terminal-target-selection.cjs');
const base = process.env.HVO_BASE_URL || 'http://127.0.0.1:5056';
const passwordFile = process.env.HVO_OWNER_PASSWORD_FILE || path.resolve(__dirname, '../../.fixture/secrets/owner-password');
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  try {
    const context = await browser.newContext({ viewport: { width: 1200, height: 900 } });
    expect((await context.request.get(base + '/api/v1/runtimes/unknown/terminal')).status()).toBe(401);
    const page = await context.newPage(); const errors = []; let output = '';
    page.on('pageerror', e => errors.push(e.message));
    page.on('websocket', ws => {
      if (ws.url().endsWith('/terminal')) ws.on('framereceived', frame => { output += Buffer.isBuffer(frame.payload) ? frame.payload.toString('utf8') : frame.payload; });
    });
    await page.goto(base + '/login');
    await page.getByLabel('Owner password').fill(fs.readFileSync(passwordFile, 'utf8').trim());
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
    const snapshot = await (await context.request.get(base + '/api/v1/snapshot')).json();
    const explicitRuntimeId = process.env.HVO_TERMINAL_RUNTIME_ID;
    const runtime = selectRuntime(snapshot.runtimes, explicitRuntimeId);
    if (!runtime) throw new Error(explicitRuntimeId ? `Requested SSH runtime '${explicitRuntimeId}' is not registered.` : 'A registered SSH runtime is required.');
    await page.goto(base + '/terminal?runtime=' + runtime.id);
    await expect(page.getByRole('button', { name: 'Open terminal', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'Open terminal', exact: true }).click();
    await expect.poll(() => output.length, {timeout:20000}).toBeGreaterThan(0);
    await page.locator('.xterm-helper-textarea').pressSequentially("printf '\\110\\126\\117\\137\\124\\105\\122\\115\\111\\116\\101\\114\\137\\117\\113\\n'; stty size", { delay: 1 });
    await page.keyboard.press('Enter');
    await expect.poll(() => output, {timeout:10000}).toContain('HVO_TERMINAL_OK');
    if (process.env.HVO_EXPECT_OPENCODE_VERSION) {
      await page.locator('.xterm-helper-textarea').pressSequentially('opencode --version');
      await page.keyboard.press('Enter');
      await expect.poll(() => output, {timeout:20000}).toContain(process.env.HVO_EXPECT_OPENCODE_VERSION);
    }
    const sizes = () => [...output.matchAll(/(?:^|[\r\n])(\d+) (\d+)[\r\n]+/g)].map(m => [Number(m[1]), Number(m[2])]);
    await expect.poll(() => sizes().length).toBeGreaterThan(0);
    const firstSize = sizes().at(-1);
    await page.setViewportSize({width:700,height:700});
    await page.waitForTimeout(250);
    await page.locator('.xterm-helper-textarea').pressSequentially('stty size'); await page.keyboard.press('Enter');
    await expect.poll(() => sizes().at(-1)[1]).toBeLessThan(firstSize[1]);
    await page.getByRole('button', { name: 'Close terminal', exact: true }).click();
    await expect.poll(async () => (await (await context.request.get(base + '/api/v1/events?after=' + snapshot.sequence)).json()).filter(e => e.type === 'TerminalClosed').length).toBeGreaterThan(0);
    // A WebSocket upgrade alone must never grant shell access.
    const before = (await (await context.request.get(base + '/api/v1/snapshot')).json()).sequence;
    await page.evaluate(async id => {
      await new Promise((resolve, reject) => {
        const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/v1/runtimes/${id}/terminal`);
        const timeout = setTimeout(() => { ws.close(); reject(new Error('Invalid token was not rejected')); }, 5000);
        ws.onopen = () => ws.send(JSON.stringify({ token:'invalid' }));
        ws.onclose = () => { clearTimeout(timeout); resolve(); };
      });
    }, runtime.id);
    const events = await (await context.request.get(base + '/api/v1/events?after=' + before)).json();
    expect(events.some(x => x.type === 'TerminalOpened')).toBe(false);
    await page.getByRole('button', { name: 'Open terminal', exact: true }).click();
    await expect(page.getByRole('status')).toHaveText('Terminal connected.');
    await page.screenshot({path:path.resolve(__dirname,'../../artifacts/browser/admin-terminal.png')});
    await page.close();
    await expect.poll(async () => (await (await context.request.get(base + '/api/v1/events?after=' + before)).json()).some(e => e.type === 'TerminalClosed')).toBe(true);
    expect(errors).toEqual([]);
    console.log('PASS: terminal SSH input/output, resize, explicit close, reopen/page close, anonymous denial and invalid-CSRF denial.');
  } finally { await browser.close(); }
})().catch(e => {console.error(e);process.exit(1);});

// Run against the published Docker application to catch missing framework assets.
const { chromium, expect } = require(process.env.HVO_PLAYWRIGHT || '@playwright/test');
const fs = require('node:fs');
const path = require('node:path');
const base = process.env.HVO_BASE_URL?.replace(/\/$/, '');
const passwordFile = process.env.HVO_OWNER_PASSWORD_FILE || path.resolve(__dirname, '../../.fixture/secrets/owner-password');
(async () => {
  if (!base) throw new Error('Set HVO_BASE_URL to the published application URL.');
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  try {
    const context = await browser.newContext();
    expect((await context.request.get(base + '/health/ready')).status()).toBe(200);
    expect((await context.request.get(base + '/api/v1/snapshot')).status()).toBe(401);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => {
      if (response.request().resourceType() === 'script' && response.status() >= 400)
        errors.push('Script HTTP ' + response.status() + ': ' + response.url());
    });
    page.on('requestfailed', request => {
      if (request.resourceType() === 'script') errors.push('Script failed: ' + request.url());
    });
    await page.goto(base + '/login');
    await page.getByLabel('Owner password').fill(fs.readFileSync(passwordFile, 'utf8').trim());
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true', { timeout: 15000 });
    for (const name of ['Runtimes', 'Workers', 'Coordination', 'Overview']) {
      const navigation = name === 'Runtimes' ? 'Administration' : 'Main navigation';
      await page.getByRole('navigation', { name: navigation }).getByRole('link', { name, exact: true }).click();
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await expect(page.getByRole('heading', { name, level: 1, exact: true })).toBeVisible();
      await page.reload();
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await expect(page.getByRole('heading', { name, level: 1, exact: true })).toBeVisible();
      if (name === 'Runtimes') {
        await page.getByRole('button', { name: 'Add runtime', exact: true }).click();
        const profile = page.getByRole('region', { name: 'Runtime profile' });
        await expect(profile).toBeVisible();
        await profile.getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(profile).toHaveCount(0);
      }
    }
    await page.goto(base + '/providers');
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
    await expect(page.getByRole('heading', { name: 'Model access', exact: true })).toBeVisible();
    const pools = page.getByRole('region', { name: 'Shared model access' });
    await expect(pools).toBeVisible();
    await expect(pools).toContainText('Remaining subscription allowance is unknown');
    if (process.env.HVO_PROVIDER_POOL_FIXTURE === '1') {
      await expect(pools).toContainText('fixture-provider · Exhausted');
      const resume = pools.getByRole('button', { name: 'Resume fixture-provider dispatch', exact: true });
      await expect(resume).toBeDisabled();
      await pools.getByRole('checkbox').check();
      await resume.click();
      await expect(pools).toContainText('fixture-provider · Available');
      await page.reload();
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await expect(pools).toContainText('fixture-provider · Available');
    }
    const keyPanel = page.getByRole('region', { name: 'OpenCode Go key' });
    await expect(keyPanel.getByLabel('OpenCode Go API key')).toHaveAttribute('type', 'password');
    // This mutation is only allowed against a disposable, explicitly opted-in fixture.
    if (process.env.HVO_PROVIDER_KEY_FIXTURE === '1') {
      await keyPanel.getByLabel('OpenCode Go API key').fill('browser-fixture-key-not-a-real-credential');
      await keyPanel.getByRole('button', { name: 'Save encrypted key' }).click();
      await expect(keyPanel.getByRole('status')).toContainText('Key saved');
      await expect(keyPanel.getByLabel('OpenCode Go API key')).toHaveValue('');
      await page.reload();
      await expect(keyPanel.getByRole('status')).toContainText('Key saved');
      await expect(keyPanel.getByLabel('OpenCode Go API key')).toHaveValue('');
      const keyMetadata = await (await context.request.get(base + '/api/v1/providers/opencode-go/key')).text();
      expect(keyMetadata).not.toContain('browser-fixture-key');
      expect(keyMetadata).not.toContain('secretReference');
    }
    const workerSearch = page.getByRole('searchbox', { name: 'Find a worker' });
    await workerSearch.fill('fixture-no-matching-worker-identity');
    await expect(page.getByRole('navigation', { name: 'Conversations' })).toContainText('No task workers match.');
    await workerSearch.fill('');
    await page.getByRole('button', { name: 'Collapse worker sidebar', exact: true }).click();
    await expect.poll(() => page.evaluate(() => localStorage.getItem("hvo.agentcontrol.sidebar-collapsed"))).toBe("true");
    await page.setViewportSize({ width: 390, height: 844 });
    for (const route of ['/', '/workers', '/runtimes', '/coordination', '/providers']) {
      await page.goto(base + route);
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
    }
    await page.getByRole('button', { name: 'Expand worker sidebar', exact: true }).click();
    await expect(page.getByRole('navigation', { name: 'Administration' })).toBeVisible();
    await page.getByRole('button', { name: 'Close worker sidebar', exact: true }).click({ position: { x: 382, y: 420 } });
    expect(errors).toEqual([]);
    console.log('PASS: published application readiness, anonymous API 401, sign-in, interactive navigation/reload on all four pages, runtime form and browser scripts.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });

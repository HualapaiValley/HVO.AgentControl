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
    await page.getByRole('button', { name: 'Collapse worker sidebar', exact: true }).click();
    await expect.poll(() => page.evaluate(() => localStorage.getItem("hvo.agentcontrol.sidebar-collapsed"))).toBe("true");
    await page.setViewportSize({ width: 390, height: 844 });
    for (const route of ['/', '/workers', '/runtimes', '/coordination']) {
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

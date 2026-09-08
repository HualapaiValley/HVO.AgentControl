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
    for (const name of ['Runtimes', 'Control services', 'Workers', 'Coordination', 'Overview']) {
      const navigation = ['Runtimes', 'Control services'].includes(name) ? 'Administration' : 'Main navigation';
      await page.getByRole('navigation', { name: navigation }).getByRole('link', { name, exact: true }).click();
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await expect(page.getByRole('heading', { name, level: 1, exact: true })).toBeVisible();
      await page.reload();
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await expect(page.getByRole('heading', { name, level: 1, exact: true })).toBeVisible();
      if (name === 'Control services') {
        await page.getByRole('button', { name: 'Register control service', exact: true }).click();
        const registration = page.getByRole('region', { name: 'Register control service', exact: true });
        await expect(registration).toBeVisible();
        await expect(registration.getByLabel('Private endpoint', { exact: true })).toHaveValue('http://opencode-control:4096');
        await expect(registration.getByRole('button', { name: 'Verify and register', exact: true })).toBeDisabled();
        await expect(registration.getByLabel('Password file reference')).toHaveAttribute('autocomplete', 'off');
        await expect(registration.getByLabel('SSH host', { exact: true })).toHaveCount(0);
        await registration.getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(registration).toHaveCount(0);
      }
      if (name === 'Runtimes') {
        await page.getByRole('button', { name: 'Add runtime', exact: true }).click();
        const profile = page.getByRole('region', { name: 'Runtime profile' });
        await expect(profile).toBeVisible();
        await profile.getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(profile).toHaveCount(0);
      }
    }
    if (process.env.HVO_CONTROL_SERVICE_FIXTURE === '1') {
      await require('./control-services-smoke.cjs')({ page, context, base, expect });
    }
    if (process.env.HVO_GITHUB_READINESS_FIXTURE === '1') {
      await page.goto(base + '/github');
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      const grantHeading = page.getByRole('heading', { name: 'GitHub readiness fixture MigrationRequired', exact: true });
      const grant = grantHeading.locator('..');
      await expect(grantHeading).toBeVisible();
      await expect(grant).toContainText("The current credential is stored in AgentControl's managed GitHub CLI directory");
      await expect(grant).toContainText('Drain work before restarting');
      await expect(grant).toContainText('owner-controlled stop action');
      const access = (await (await context.request.get(base + '/api/v1/github/access')).json()).find(x => x.id === 'github-readiness-runtime');
      expect(access.credentialState).toBe('Delivered');
      expect(access.exactCiInspectionState).toBe('CredentialUnavailable');
      expect(access.environmentPolicyVersion).toBeUndefined();
      expect(access.environmentPolicyFingerprint).toBeUndefined();
      expect(access.environmentProcessId).toBeUndefined();
      expect(access.environmentProcessIncarnation).toBeUndefined();
      expect(access.environmentVerifiedAt).toBeUndefined();
      expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
      await page.setViewportSize({ width: 390, height: 844 });
      await page.reload();
      await expect(grant).toContainText('Drain work before restarting');
      expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
      await page.setViewportSize({ width: 1280, height: 900 });
    }
    if (process.env.HVO_CONVERSATION_RACE_FIXTURE === '1') {
      const workerA = 'conversation-race-a';
      const workerB = 'conversation-race-b';
      await page.goto(base + '/');
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await page.getByLabel('Agent conversation').selectOption(workerA);
      await page.getByLabel('Agent conversation').selectOption(workerB);
      await page.getByLabel('Agent conversation').selectOption(workerA);
      const conversation = page.getByRole('region', { name: 'Worker conversation' });
      await expect(conversation.getByRole('heading', { name: 'Conversation race A', exact: true })).toBeVisible();
      await conversation.getByLabel('Task or follow-up').fill('Browser-selected worker identity');
      await conversation.getByRole('button', { name: 'Send instruction', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Instruction queued');
      const snapshot = await (await context.request.get(base + '/api/v1/snapshot')).json();
      const command = snapshot.commands.find(x => x.kind === 'Prompt' && JSON.parse(x.payload).text === 'Browser-selected worker identity');
      expect(command.workerId).toBe(workerA);
      expect(JSON.parse(command.payload).expectedRevision).toBe(7);
      expect(snapshot.commands.some(x => x.workerId === workerB && x.kind === 'Prompt')).toBe(false);
    }
    if (process.env.HVO_COORDINATION_SUPERVISION_FIXTURE === '1') {
      await page.goto(base + '/coordination');
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      const run = page.getByRole('region', { name: 'Coordination run', exact: true });
      await expect(run.getByRole('button', { name: 'Resume coordination', exact: true })).toBeDisabled();
      const renewal = run.getByRole('region', { name: 'Renew coordination', exact: true });
      await expect(renewal.getByLabel('Additional coordinator turns', { exact: true })).toHaveCount(0);
      await renewal.getByLabel('Replacement coordinator instruction', { exact: true }).fill('Continue existing browser task under service supervision.');
      await expect(renewal.getByLabel('Keep supervising until I pause or stop')).toBeChecked();
      await renewal.getByRole('button', { name: 'Apply checkpoint and continue' }).click();
      // The disconnected fixture moves from Ready to Waiting on the service tick.
      // Wait for that stable state before the next owner action so it uses the
      // current revision; continuous supervision does not pause itself here.
      await expect.poll(async () => (await (await context.request.get(base + '/api/v1/coordinations')).json())[0].state).toBe('Waiting');
      await expect(run.getByRole('heading', { name: 'Waiting', exact: true })).toBeVisible();
      await expect(run).toContainText('Continuous supervision');
      await expect(run).toContainText('no fixed turn cutoff');
      await expect.poll(async () => (await (await context.request.get(base + '/api/v1/coordinations')).json())[0].lastSupervisorAt).toBeGreaterThan(0);
      await page.reload();
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await expect(run).toContainText('no fixed turn cutoff');
      await page.getByRole('button', { name: 'Collapse worker sidebar', exact: true }).click();
      await page.setViewportSize({ width: 390, height: 844 });
      await run.getByRole('button', { name: 'Pause coordination', exact: true }).click();
      await expect(run.getByRole('heading', { name: 'Paused', exact: true })).toBeVisible();
      await expect(renewal).toBeVisible();
      await expect(renewal.getByLabel('Replacement coordinator instruction')).toHaveValue('Continue existing browser task under service supervision.');
      expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
      await run.getByRole('button', { name: 'Resume coordination', exact: true }).click();
      await expect.poll(async () => (await (await context.request.get(base + '/api/v1/coordinations')).json())[0].state).toBe('Waiting');
      await expect(run.getByRole('heading', { name: 'Waiting', exact: true })).toBeVisible();
      await run.getByRole('button', { name: 'Stop coordination', exact: true }).click();
      await expect(run.getByRole('heading', { name: 'Stopped', exact: true })).toBeVisible();
      const saved = (await (await context.request.get(base + '/api/v1/coordinations')).json())[0];
      expect(saved.id).toBe('supervision-browser-run');
      expect(saved.round).toBe(1);
      expect(saved.maxRounds).toBe(1);
      expect(saved.continuousSupervision).toBe(true);
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.getByRole('button', { name: 'Expand worker sidebar', exact: true }).click();
    }
    await page.goto(base + '/providers');
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
    await expect(page.getByRole('heading', { name: 'Model access', exact: true })).toBeVisible();
    const pools = page.getByRole('region', { name: 'Shared model access' });
    await expect(pools).toBeVisible();
    await expect(pools).toContainText('Remaining subscription allowance is unknown');
    if (process.env.HVO_PROVIDER_POOL_FIXTURE === '1') {
      await expect(pools).toContainText('fixture-provider · Exhausted');
      await expect(pools).toContainText('fixture-reserved-request');
      const resume = pools.getByRole('button', { name: 'Resume fixture-provider dispatch', exact: true });
      await expect(resume).toBeDisabled();
      await pools.getByRole('checkbox').check();
      await resume.click();
      await expect(pools).toContainText('fixture-provider · Available');
      await expect(pools).toContainText('Other requests wait for its settlement');
      const access = (await (await context.request.get(base + '/api/v1/providers/pools')).json()).find(x => x.id === 'provider:fixture-provider');
      expect(access.recoveryCommandId).toBe('fixture-reserved-request');
      await page.reload();
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      await expect(pools).toContainText('fixture-provider · Available');
      await expect(pools).toContainText('fixture-reserved-request');
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(pools).toContainText('Other requests wait for its settlement');
      await page.setViewportSize({ width: 1280, height: 900 });
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
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
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
    for (const route of ['/', '/workers', '/runtimes', '/control-services', '/coordination', '/providers']) {
      await page.goto(base + route);
      await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
      expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
    }
    await page.getByRole('button', { name: 'Expand worker sidebar', exact: true }).click();
    await expect(page.getByRole('navigation', { name: 'Administration' })).toBeVisible();
    await page.getByRole('button', { name: 'Close worker sidebar', exact: true }).click({ position: { x: 382, y: 420 } });
    expect(errors).toEqual([]);
    console.log('PASS: published application readiness, anonymous API 401, sign-in, interactive navigation/reload on all application pages, runtime form and browser scripts.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });

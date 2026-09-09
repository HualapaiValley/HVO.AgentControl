const { randomUUID } = require('node:crypto');

// Opt-in published-app fixture. Create all inventory through the owner API;
// there is no executor enrollment and no Docker/SSH effect in this scenario.
module.exports = async ({ page, context, base, expect }) => {
  const csrf = (await (await context.request.get(base + '/api/v1/csrf')).json()).token;
  const post = async (route, data) => {
    const response = await context.request.post(base + '/api/v1' + route, {
      data, headers: { 'X-CSRF-TOKEN': csrf }
    });
    if (!response.ok()) throw new Error(route + ': ' + await response.text());
    return response.json();
  };
  const id = () => randomUUID().replaceAll('-', '');
  const host = await post('/hosts', { requestId: id(), id: id(), name: 'Draft browser fixture', kind: 'PhysicalMachine' });
  const project = await post('/projects', {
    requestId: id(), id: id(), name: 'Draft browser configuration',
    repositoryUrl: 'https://github.com/example/draft-fixture-' + id()
  });
  const createDraft = name => post('/runtimes/drafts', {
    requestId: id(), runtimeId: id(), name, hostId: host.id,
    configurationProjectId: project.id, devcontainerPath: '.devcontainer/devcontainer.json',
    expectedHostRevision: host.revision, expectedProjectRevision: project.revision
  });
  const card = name => page.getByRole('article', { name: 'Managed runtime draft', exact: true }).filter({ hasText: name });
  const visit = async () => {
    await page.goto(base + '/runtimes');
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive', 'true');
    if (page.viewportSize().width <= 850) {
      await expect.poll(() => page.locator('.worker-sidebar').evaluate(el => el.getBoundingClientRect().right)).toBeLessThanOrEqual(0);
    }
  };

  for (const width of [1280, 390]) {
    if (width === 390) {
      // The preceding desktop fixture deliberately leaves navigation expanded.
      // Close it through the actual control before entering the mobile overlay layout.
      await page.getByRole('button', { name: 'Collapse worker sidebar', exact: true }).click();
      await expect.poll(() => page.evaluate(() => localStorage.getItem('hvo.agentcontrol.sidebar-collapsed'))).toBe('true');
    }
    await page.setViewportSize({ width, height: 900 });
    const unusedName = 'Unused draft ' + width;
    const unused = await createDraft(unusedName);
    await visit();
    await expect(card(unusedName)).toContainText('Pending enrollment');
    await page.reload();
    await expect(card(unusedName)).toBeVisible();
    await page.goto(base + '/workers');
    await visit();
    await card(unusedName).getByRole('button', { name: 'Delete draft', exact: true }).click();
    await card(unusedName).getByRole('button', { name: 'Keep draft', exact: true }).click();
    await expect(card(unusedName).getByRole('button', { name: 'Confirm delete draft', exact: true })).toHaveCount(0);
    expect((await context.request.get(base + '/api/v1/runtimes/' + unused.environment.runtimeId + '/environment')).status()).toBe(200);
    await card(unusedName).getByRole('button', { name: 'Delete draft', exact: true }).click();
    await card(unusedName).getByRole('button', { name: 'Confirm delete draft', exact: true }).click();
    await expect(card(unusedName)).toHaveCount(0);
    await expect(page.getByRole('status')).toContainText('Runtime draft deleted.');
    await page.reload();
    await expect(card(unusedName)).toHaveCount(0);
    expect((await context.request.get(base + '/api/v1/runtimes/' + unused.environment.runtimeId + '/environment')).status()).toBe(404);

    const retainedName = 'Retained draft ' + width;
    const retained = await createDraft(retainedName);
    await visit();
    await card(retainedName).getByRole('button', { name: 'Delete draft', exact: true }).click();
    // Race the confirmation with an operation accepted through the real API.
    // Deletion must consult current references, not the displayed snapshot.
    const operation = await post('/provisioning/operations', {
      requestId: id(), hostId: host.id, expectedHostRevision: host.revision,
      runtimeId: retained.environment.runtimeId, expectedRuntimeRevision: retained.environment.runtimeRevision,
      expectedEnvironmentRevision: retained.environment.revision, projectId: project.id,
      expectedProjectRevision: project.revision, workspaceId: id(), sourceRevision: 'a'.repeat(40),
      configurationPath: '.devcontainer/devcontainer.json', configurationSha256: 'b'.repeat(64),
      requestedBuildCpuMillis: 1000, requestedBuildMemoryBytes: 1073741824,
      requestedRuntimeCpuMillis: 1000, requestedRuntimeMemoryBytes: 1073741824
    });
    expect(operation.state).toBe('AwaitingHostAuthority');
    expect(operation.effectStarted).toBe(false);
    await card(retainedName).getByRole('button', { name: 'Confirm delete draft', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Retained provisioning operations');
    await expect(card(retainedName)).toBeVisible();
    await card(retainedName).getByRole('button', { name: 'Keep draft', exact: true }).click();
    await page.reload();
    await expect(card(retainedName)).toBeVisible();
    const cancelled = await post('/provisioning/operations/' + operation.id + '/cancel', { expectedRevision: operation.revision });
    expect(cancelled.state).toBe('Cancelled');
    await card(retainedName).getByRole('button', { name: 'Delete draft', exact: true }).click();
    await card(retainedName).getByRole('button', { name: 'Confirm delete draft', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Retained provisioning operations');
    expect((await context.request.get(base + '/api/v1/provisioning/operations/' + operation.id)).status()).toBe(200);
    await expect(card(retainedName)).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.getByRole('button', { name: 'Expand worker sidebar', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Collapse worker sidebar', exact: true })).toBeVisible();
  await expect.poll(() => page.evaluate(() => localStorage.getItem('hvo.agentcontrol.sidebar-collapsed'))).toBe('false');
  console.log('PASS: managed drafts on desktop/mobile persist across navigation; keep/delete unused drafts; reject a newly referenced or cancelled-operation draft and retain its card and receipt.');
};

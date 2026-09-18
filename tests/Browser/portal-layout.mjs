// Read-only routed shell regression: no prompts, cancellation or runtime mutations.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const base = process.env.BASE_URL || 'http://127.0.0.1:15054';
const password = execFileSync('docker', ['--context', process.env.DOCKER_CONTEXT || 'home-docker',
  'exec', 'agentcontrol-v2-control-1', 'python3', '-c',
  'from pathlib import Path; print(Path("/run/agentcontrol-secrets/owner-password").read_text().strip())'],
{ encoding: 'utf8' }).trim();
const browser = await chromium.launch({ headless: true,
  ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : {}) });
const context = await browser.newContext({ httpCredentials: { username: 'owner', password } });
const results = [];
try {
  const page = await context.newPage();
  for (const viewport of [{ width: 1440, height: 900 }, { width: 900, height: 700 },
    { width: 390, height: 844 }, { width: 320, height: 720 }]) {
    await page.setViewportSize(viewport);
    for (const route of ['/organization', '/employees', '/hiring', '/system']) {
      const response = await page.goto(`${base}${route}`, { waitUntil: 'domcontentloaded' });
      const measurement = await page.evaluate(() => {
        const shell = document.querySelector('[data-portal-shell]');
        const header = document.querySelector('.shell-header');
        const footer = document.querySelector('.shell-footer');
        const content = document.querySelector('.page-content');
        return {
          shell: Boolean(shell), header: Boolean(header), footer: Boolean(footer), content: Boolean(content),
          active: document.querySelectorAll('.primary-nav a.active').length,
          documentWidth: document.documentElement.scrollWidth,
          viewportWidth: innerWidth,
        };
      });
      const passed = response.status() === 200 && measurement.shell && measurement.header
        && measurement.footer && measurement.content && measurement.active === 1
        && measurement.documentWidth <= measurement.viewportWidth + 1;
      results.push({ viewport, route, passed, measurement });
      console.log(`${passed ? 'PASS' : 'FAIL'} ${route} ${viewport.width}x${viewport.height}`);
    }
  }
  mkdirSync(`${root}artifacts/portal-layout`, { recursive: true });
  writeFileSync(`${root}artifacts/portal-layout/results.json`, JSON.stringify(results, null, 2));
  if (results.some((result) => !result.passed)) process.exitCode = 1;
} finally {
  await browser.close();
}

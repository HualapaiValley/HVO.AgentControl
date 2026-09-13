// Read-only layout regression: no prompts, cancellation or runtime mutations.
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
  // Keep banner state deterministic while testing the real deployed styles.
  await page.route('**/api/control', route => route.fulfill({ json: {
    state: 'disabled', organizationName: 'Layout check', terminalReady: false,
    model: 'opencode/big-pickle', sessionId: null, error: null
  } }));
  await page.goto(base);
  for (const viewport of [{ width: 1440, height: 900 }, { width: 1440, height: 600 },
    { width: 900, height: 700 }, { width: 390, height: 844 }, { width: 320, height: 720 }]) {
    await page.setViewportSize(viewport);
    for (const bannerVisible of [false, true]) {
      await page.evaluate(visible => {
        const banner = document.querySelector('.error-banner');
        banner.hidden = !visible;
        banner.querySelector('.error-text').textContent = visible ? 'Test runtime status warning.' : '';
        document.querySelector('.control-deck').scrollTop = 0;
      }, bannerVisible);
      await page.waitForTimeout(150);
      const measurement = await page.evaluate(() => {
        const bounds = selector => {
          const r = document.querySelector(selector).getBoundingClientRect();
          return { top: r.top, bottom: r.bottom, height: r.height };
        };
        const deck = document.querySelector('.control-deck');
        const padding = getComputedStyle(deck);
        return {
          viewport: innerHeight,
          documentHeight: document.documentElement.scrollHeight,
          documentWidth: document.documentElement.scrollWidth,
          width: innerWidth,
          header: bounds('.app-bar'), footer: bounds('.app-foot'),
          deck: bounds('.control-deck'), terminal: bounds('.terminal-surface'),
          stage: bounds('.terminal-stage'),
          paddingTop: parseFloat(padding.paddingTop), paddingBottom: parseFloat(padding.paddingBottom)
        };
      });
      const near = (a, b) => Math.abs(a - b) <= 2;
      const passed = near(measurement.footer.bottom, viewport.height)
        && near(measurement.footer.height, viewport.width <= 720 ? 60 : 40)
        && near(measurement.deck.bottom, measurement.footer.top)
        && near(measurement.terminal.top, measurement.deck.top + measurement.paddingTop)
        && near(measurement.terminal.bottom, measurement.deck.bottom - measurement.paddingBottom)
        && measurement.stage.height > 0
        && measurement.documentHeight <= viewport.height + 1
        && measurement.documentWidth <= viewport.width;
      results.push({ viewport, bannerVisible, passed, measurement });
      console.log(`${passed ? 'PASS' : 'FAIL'} ${viewport.width}x${viewport.height} banner=${bannerVisible} footer=${measurement.footer.height} terminal=${measurement.terminal.height.toFixed(1)}`);
    }
  }
  mkdirSync(`${root}artifacts/portal-layout`, { recursive: true });
  writeFileSync(`${root}artifacts/portal-layout/results.json`, JSON.stringify(results, null, 2));
  if (results.some(r => !r.passed)) process.exitCode = 1;
} finally {
  await browser.close();
}

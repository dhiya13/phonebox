// OLED burn-in protection: with Night mode › "Also when the panel is idle", the night screen comes on after the idle
// minutes without a touch, unless music plays, a Claude Code prompt or waiting session needs you, or the webcam is on.
const { test, expect } = require('@playwright/test');

const NIGHT = { enabled: false, idle: true, idleMin: 1, start: '00:30', end: '07:30', brightness: 0.02 };
const config = (night, widgets) => JSON.stringify({
  night, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
  pages: [{ id: 'p', title: 'P', widgets: widgets || [{ id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 3, options: {} }] }],
});

test('the idle rule: minutes without a touch, and nothing that needs you', async ({ page }) => {
  await page.goto('/panel/');
  const r = await page.evaluate(async (night) => {
    const { idleNightDue } = await import('/panel/main.js');
    const base = { night, quietMs: 61_000, playing: false, prompt: false, waiting: false, webcam: false };
    return {
      idle: idleNightDue(base),
      tooSoon: idleNightDue({ ...base, quietMs: 59_000 }),
      music: idleNightDue({ ...base, playing: true }),
      prompt: idleNightDue({ ...base, prompt: true }),
      waiting: idleNightDue({ ...base, waiting: true }),
      webcam: idleNightDue({ ...base, webcam: true }),
      off: idleNightDue({ ...base, night: { ...night, idle: false } }),
      noNight: idleNightDue({ ...base, night: undefined }),
    };
  }, NIGHT);
  expect(r).toEqual({ idle: true, tooSoon: false, music: false, prompt: false, waiting: false, webcam: false, off: false, noNight: false });
});

test('music playing and a waiting Claude session keep the panel awake', async ({ page }) => {
  // the demo plays music and has a Claude Code session waiting for an approval
  await page.clock.install();
  await page.addInitScript((c) => localStorage.setItem('pb-demo-config', c), config(NIGHT));
  await page.goto('/panel/');
  await page.locator('[data-id="p"]').waitFor();
  await page.clock.fastForward(120_000);
  await expect(page.locator('body')).not.toHaveClass(/\bnight\b/);
});

test('the spectrum widget draws as a ring', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.addInitScript((c) => localStorage.setItem('pb-demo-config', c),
    config({ enabled: false }, [{ id: 'sp', type: 'spectrum', x: 0, y: 0, w: 6, h: 6, options: { shape: 'ring', bars: 24, style: 'classic' } }]));
  await page.goto('/panel/');
  await expect(page.locator('[data-id="p"] .spec-canvas')).toBeVisible();
  await page.waitForTimeout(500);
  expect(errors).toEqual([]);
});

test('a camera that is not running does not keep the panel awake', async ({ page }) => {
  // An app that keeps OBS Virtual Camera open in the background (NVIDIA Broadcast) makes the agent say webcamWanted;
  // only the phone camera actually running holds the idle night screen off.
  await page.goto('/panel/');
  const r = await page.evaluate(async () => {
    const w = await import('/panel/widgets/webcam.js');
    return { streaming: w.webcamStreaming(), holds: w.webcamHolds() };
  });
  expect(r).toEqual({ streaming: false, holds: false });
});

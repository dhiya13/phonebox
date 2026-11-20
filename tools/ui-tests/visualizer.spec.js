const { test, expect } = require('@playwright/test');

// Audio visualizer: the same frames as Spectrum bars (the demo answers /api/spectrum like the agent), drawn calmly.
async function show(page, options = {}, size = { w: 12, h: 3 }, pages) {
  await page.goto('/panel/');
  await page.evaluate(({ options, size, pages }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
      pages: pages || [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'viz', type: 'visualizer', x: 0, y: 0, ...size, options }] }] }));
  }, { options, size, pages });
  await page.reload();
}

// how many pixels of the (first) canvas are clearly drawn, and how many are close to a colour
const drawn = (page, rgb = null, i = 0, minAlpha = 150) => page.locator('.w-visualizer canvas').nth(i).evaluate((c, [rgb, minAlpha]) => {
  const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
  let n = 0;
  for (let k = 0; k < d.length; k += 4) {
    if (d[k + 3] < minAlpha) continue;
    if (!rgb || (Math.abs(d[k] - rgb[0]) < 40 && Math.abs(d[k + 1] - rgb[1]) < 40 && Math.abs(d[k + 2] - rgb[2]) < 40)) n++;
  }
  return n;
}, [rgb, minAlpha]);

for (const style of ['bars', 'wave', 'ring']) {
  test(`the audio visualizer draws the PC's sound (${style})`, async ({ page }) => {
    const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
    await show(page, { style });
    await expect(page.locator('.w-visualizer canvas')).toBeVisible();
    await expect.poll(() => drawn(page)).toBeGreaterThan(300);
    await expect(page.locator('.w-visualizer .viz-msg')).toBeHidden();
    // it moves between frames
    const a = await drawn(page);
    await expect.poll(async () => Math.abs((await drawn(page)) - a)).toBeGreaterThan(0);
    expect(errors).toEqual([]);
  });
}

test('the audio visualizer follows the accent colour, or uses its own', async ({ page }) => {
  await show(page, { style: 'bars', bars: 8 });
  await page.evaluate(() => document.documentElement.style.setProperty('--accent', 'rgb(250, 20, 20)'));
  await expect.poll(() => drawn(page, [250, 20, 20]), { timeout: 5000 }).toBeGreaterThan(200);

  await show(page, { style: 'ring', accent: false, color: '#2050ff' });
  await expect.poll(() => drawn(page, [32, 80, 255])).toBeGreaterThan(200);
});

test('the audio visualizer fits a 2 x 1 widget', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  for (const style of ['bars', 'wave', 'ring']) {
    await show(page, { style, title: 'Now' }, { w: 2, h: 1 });
    await expect.poll(() => drawn(page)).toBeGreaterThan(10);
  }
  expect(errors).toEqual([]);
});

test('the audio visualizer rests and says so when the computer cannot capture its sound', async ({ page }) => {
  await page.addInitScript(() => {
    let inner = window.fetch;
    Object.defineProperty(window, 'fetch', {
      configurable: true,
      get: () => {
        const fallback = inner;
        return (input, init) => {
          const url = typeof input === 'string' ? input : input.url;
          if (url.includes('/api/spectrum')) {
            return Promise.resolve(new Response(JSON.stringify({ shown: true, active: false, fps: 30, b: null }),
              { status: 200, headers: { 'Content-Type': 'application/json' } }));
          }
          return fallback(input, init);
        };
      },
      set: (v) => { inner = v; },
    });
  });
  await show(page, { style: 'bars' });
  await expect(page.locator('.w-visualizer .viz-msg')).toHaveText('Can’t capture PC audio (Windows only)', { timeout: 6000 });
  // the resting line: something faint is drawn, far less than with music
  expect(await drawn(page, null, 0, 20)).toBeGreaterThan(0);
  expect(await drawn(page)).toBe(0);
});

test('an audio visualizer on another page does not keep the agent capturing', async ({ page }) => {
  const viz = { id: 'v', type: 'visualizer', x: 0, y: 0, w: 12, h: 3, options: {} };
  const clock = { id: 'c', type: 'clock', x: 0, y: 0, w: 12, h: 3, options: {} };
  await show(page, {}, {}, [{ id: 'one', title: 'One', widgets: [clock] }, { id: 'two', title: 'Two', widgets: [viz] }]);
  await page.evaluate(() => {
    window.__asks = 0;
    const demo = window.fetch;
    window.fetch = (url, init) => { if (String(url).includes('/api/spectrum')) window.__asks++; return demo(url, init); };
  });
  await page.waitForTimeout(1500);
  expect(await page.evaluate(() => window.__asks)).toBe(0);
  expect(await drawn(page)).toBe(0);
});

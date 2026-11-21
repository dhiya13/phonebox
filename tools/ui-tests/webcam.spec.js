const { test, expect } = require('@playwright/test');

// Phone webcam: the Android bridge runs the camera (faked here) and keeps its picture for the preview, which the app
// answers at /agent/api/webcam/preview (faked through fetch). The fake has to be there before main.js builds the
// bridge, so it comes from an init script.
async function layout(page, options, { app = true, state } = {}) {
  if (app) {
    await page.addInitScript((initial) => {
      window.__starts = [];
      window.__stops = 0;
      window.__state = initial || { state: 'off' };
      window.Android = {
        canWebcam: () => true,
        webcamStart: (key, widget, facing, quality, send) => {
          window.__starts.push({ widget, facing, quality, send });
          window.__state = { state: 'starting', widget, facing, pc: '', seq: 0 };
          if (window.__next) setTimeout(() => { window.__state = { widget, facing, seq: 1, ...window.__next }; }, 30);
        },
        webcamStop: () => { window.__stops++; window.__state = { state: 'off' }; },
        webcamState: () => JSON.stringify(window.__state),
      };
    }, state);
  }
  await page.goto('/panel/');
  await page.evaluate((options) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'wc', type: 'webcam', x: 0, y: 0, w: 6, h: 6, options }] }] }));
  }, options);
  await page.reload();
  await expect(page.locator('.w.wc')).toBeVisible();
  await page.evaluate(() => {
    window.__previews = 0;
    const demo = window.fetch;
    // a 1 × 1 PNG stands in for the camera picture the app keeps
    const png = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='), (c) => c.charCodeAt(0));
    window.fetch = (url, init) => {
      if (String(url).endsWith('/webcam/preview')) {
        window.__previews++;
        return Promise.resolve(new Response(new Blob([png], { type: 'image/png' }), { status: 200 }));
      }
      return demo(url, init);
    };
  });
}

test('the camera starts for its own widget and says it is on the computer', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, { facing: 'front', quality: 'hd', pc: true });
  await expect(page.locator('.wc-note')).toContainText('Camera off');
  await expect(page.locator('.wc-live')).toBeHidden();
  await page.evaluate(() => { window.__next = { state: 'live', pc: 'obs' }; });
  await page.locator('.wc-power').click();
  await expect.poll(() => page.evaluate(() => window.__starts)).toEqual([{ widget: 'test/wc', facing: 'front', quality: 'hd', send: true }]);
  await expect(page.locator('.wc-cap')).toHaveText('On the computer as OBS Virtual Camera');
  await expect(page.locator('.wc-live')).toBeVisible();
  await expect(page.locator('.wc-power')).toHaveClass(/\bon\b/);
  // the preview is the picture the app keeps, shown like a mirror for the front camera
  await expect(page.locator('.wc-img')).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.wc-img')).toHaveClass(/\bmirror\b/);
  expect(await page.evaluate(() => window.__previews)).toBeGreaterThan(0);
  await page.locator('.wc-power').click();
  expect(await page.evaluate(() => window.__stops)).toBe(1);
  await expect(page.locator('.wc-note')).toContainText('Camera off');
  await expect(page.locator('.wc-live')).toBeHidden();
  await expect(page.locator('.wc-img')).not.toHaveAttribute('src', /blob:/);
  expect(errors).toEqual([]);
});

test('flipping restarts the running camera with the other one', async ({ page }) => {
  await layout(page, { facing: 'back', quality: 'sd', pc: true });
  await page.evaluate(() => { window.__next = { state: 'live', pc: 'obs' }; });
  await page.locator('.wc-power').click();
  await expect(page.locator('.wc-cap')).toHaveText('On the computer as OBS Virtual Camera');
  await page.locator('.wc-flip').click();
  await expect.poll(() => page.evaluate(() => window.__starts.map((s) => s.facing + '/' + s.quality))).toEqual(['back/sd', 'front/sd']);
  await expect(page.locator('.wc-img')).toHaveClass(/\bmirror\b/);
});

test('with sending off the picture stays on the phone', async ({ page }) => {
  await layout(page, { pc: false });
  await page.evaluate(() => { window.__next = { state: 'live', pc: 'phone' }; });
  await page.locator('.wc-power').click();
  await expect.poll(() => page.evaluate(() => window.__starts[0]?.send)).toBe(false);
  await expect(page.locator('.wc-cap')).toContainText('On this phone only');
  await expect(page.locator('.wc-live')).toBeHidden();
});

test('the widget says why the camera or the computer cannot take the picture', async ({ page }) => {
  await layout(page, {});
  await page.evaluate(() => { window.__next = { state: 'error', error: 'permission-blocked' }; });
  await page.locator('.wc-power').click();
  await expect(page.locator('.wc-note')).toHaveText('Turn on the camera for PhoneBox in Android’s app settings');
  await page.evaluate(() => { window.__next = { state: 'live', pc: 'missing' }; });
  await page.locator('.wc-power').click();
  await expect(page.locator('.wc-cap')).toContainText('needs OBS Studio');
  // a refusal from the agent is shown as the agent wrote it
  await page.evaluate(() => { window.__state = { ...window.__state, pc: 'refused:sending to the computer is off in this widget\'s settings' }; });
  await expect(page.locator('.wc-cap')).toHaveText('sending to the computer is off in this widget\'s settings');
});

test('a camera running for another widget leaves this one off', async ({ page }) => {
  await layout(page, {}, { state: { state: 'live', widget: 'other/wc', facing: 'front', pc: 'obs', seq: 3 } });
  await expect(page.locator('.wc-note')).toContainText('Camera off');
  await expect(page.locator('.wc-power')).not.toHaveClass(/\bon\b/);
  expect(await page.evaluate(() => window.__previews)).toBe(0);
});

test('without the app the widget asks for it', async ({ page }) => {
  await layout(page, {}, { app: false });
  await expect(page.locator('.wc-note')).toHaveText('Update the PhoneBox app to use the camera');
  await expect(page.locator('.wc-power')).toBeDisabled();
});

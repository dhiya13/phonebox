// "When the PC is off, show page" (Settings › Page switching) and the Wake this PC button, against the web demo with
// a mocked clock. The PC going off is every /api/ request failing, as it does on the phone when no route answers.
const { test, expect } = require('@playwright/test');

const PAGES = ['home', 'music', 'iot'].map((id) => ({ id, title: id[0].toUpperCase() + id.slice(1),
  widgets: [{ id: 'n-' + id, type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page ' + id } }] }));

async function show(page, display = {}, pages = PAGES) {
  await page.clock.install();
  await page.goto('/panel/style.css');
  await page.evaluate(({ pages, display }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], pages,
      display: { idleReturnSec: 0, minDwellSec: 0, touchGraceSec: 0, switchCountdownSec: 0, pcOffPage: 'iot', pcOffSec: 5, ...display },
    }));
  }, { pages, display });
  await page.goto('/panel/');
  if (pages.length > 1) await expect(page.locator('#dots i')).toHaveCount(pages.length);
  else await expect(page.locator('.w-button .key')).toHaveCount(1);
  // the demo answers /api/ inside the page; this sits in front of it
  await page.evaluate(() => {
    const f = window.fetch;
    window.__pcOff = false;
    window.fetch = (input, init) => (window.__pcOff && /\/api\//.test(String(input?.url || input))
      ? Promise.reject(new TypeError('Failed to fetch')) : f(input, init));
  });
}

const activeDot = (page) => page.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')));
async function step(page, ms) { for (let t = 0; t < ms; t += 250) await page.clock.fastForward(250); }
async function stepUntil(page, fn, max = 15_000) {
  for (let t = 0; t < max; t += 250) { if (await fn()) return; await page.clock.fastForward(250); }
  expect(await fn()).toBe(true);
}
const pcOff = (page, off) => page.evaluate((v) => { window.__pcOff = v; }, off);
const emit = (page, kind, data) => page.evaluate(([k, d]) => window.PhoneBoxDemo.emit(k, d), [kind, data]);

test('the panel shows the PC-off page when the PC stops answering, and goes home once when it is back', async ({ page }) => {
  await show(page);
  await page.evaluate(() => window.onAndroidKey('next'));
  await stepUntil(page, async () => (await activeDot(page)) === 1);
  await pcOff(page, true);
  await step(page, 2500);
  expect(await activeDot(page)).toBe(1); // not before the delay
  await stepUntil(page, async () => (await activeDot(page)) === 2);
  await expect(page.locator('body')).toHaveClass(/offline/);
  await pcOff(page, false);
  await stepUntil(page, async () => (await activeDot(page)) === 0);
  // once: moving away afterwards is left alone
  await page.evaluate(() => window.onAndroidKey('next'));
  await stepUntil(page, async () => (await activeDot(page)) === 1);
  await step(page, 5000);
  expect(await activeDot(page)).toBe(1);
});

test('a rule that asks for a page when the PC is back wins over the trip home', async ({ page }) => {
  await show(page);
  await pcOff(page, true);
  await stepUntil(page, async () => (await activeDot(page)) === 2);
  await pcOff(page, false);
  await stepUntil(page, async () => !(await page.locator('body').evaluate((b) => b.classList.contains('offline'))));
  await emit(page, 'goto', { page: 'music', rule: 'music', name: 'Music', hold: true, countdown: 0, priority: 5 });
  await stepUntil(page, async () => (await activeDot(page)) === 1);
  await step(page, 5000);
  expect(await activeDot(page)).toBe(1); // no bounce to Home afterwards
});

test('while the PC is off the idle return goes to the PC-off page', async ({ page }) => {
  await show(page, { idleReturnSec: 5 });
  await pcOff(page, true);
  await stepUntil(page, async () => (await activeDot(page)) === 2);
  await page.evaluate(() => window.onAndroidKey('prev'));
  await stepUntil(page, async () => (await activeDot(page)) === 1);
  await stepUntil(page, async () => (await activeDot(page)) === 2);
});

test('without the setting the panel stays where it is', async ({ page }) => {
  await show(page, { pcOffPage: null });
  await page.evaluate(() => window.onAndroidKey('next'));
  await stepUntil(page, async () => (await activeDot(page)) === 1);
  await pcOff(page, true);
  await step(page, 10_000);
  expect(await activeDot(page)).toBe(1);
});

test('Wake this PC sends the packet through the phone and says what happened', async ({ page }) => {
  await show(page, { pcOffPage: null }, [{ id: 'iot', title: 'IoT', widgets: [{ id: 'wol', type: 'button', x: 0, y: 0, w: 4, h: 6, options: { action: 'wol' } }] }]);
  const key = page.locator('[data-id="iot"] .w-button .key');
  await expect(key.locator('.k-label')).toHaveText('Wake PC');
  // a browser reaches the agent, which is on
  await key.click();
  await expect(page.locator('#toast')).toHaveText('The PC is already on');
  // the phone answers itself (WakeOnLan.kt)
  await page.evaluate(() => {
    const f = window.fetch;
    window.__wol = { ok: true, sent: 12 };
    window.fetch = (input, init) => (String(input?.url || input).endsWith('/api/wol/wake')
      ? Promise.resolve(new Response(JSON.stringify(window.__wol), { status: window.__wol.error ? 409 : 200, headers: { 'Content-Type': 'application/json' } }))
      : f(input, init));
  });
  await key.click();
  await expect(page.locator('#toast')).toHaveText('Wake-up sent to the PC');
  await page.evaluate(() => { window.__wol = { error: 'no-target' }; });
  await key.click();
  await expect(page.locator('#toast')).toHaveText(/does not know this PC's address yet/);
});

// Big button set to Home Assistant: no shortcut needed, its own picture fills it, a switch shows whether it is on,
// and a tap calls /ha/toggle (the agent, or the phone itself with the PC off) and flips the light at once. A refused
// switch is covered by the agent's tests (HomeAssistantTests).
const { test, expect } = require('@playwright/test');

const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 10 10"><rect width="10" height="10" fill="#0ea5e9"/></svg>';

// The demo answers /api/ha/* inside the page (light.desk, "Desk lamp", starts on); pictures are plain image requests,
// mocked here.
async function open(page, widgets, shortcuts = []) {
  const images = [];
  await page.route((url) => url.pathname.endsWith('/agent/api/button-picture'), (route) => {
    images.push('own:' + new URL(route.request().url()).searchParams.get('id'));
    route.fulfill({ contentType: 'image/svg+xml', body: svg });
  });
  await page.route((url) => url.pathname.endsWith('/agent/api/shortcuts/icon'), (route) => {
    images.push('shortcut');
    route.fulfill({ contentType: 'image/svg+xml', body: svg });
  });
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts,
    pages: [{ id: 'ha-btn', title: 'Buttons', widgets: widgets.map((w, i) => ({ id: 'b' + i, type: 'button', x: i * 4, y: 0, w: 4, h: 6, ...w })) }],
  }));
  await page.goto('/panel/');
  const key = (i) => page.locator('[data-id="ha-btn"] .w-button').nth(i).locator('.key');
  return { key, images };
}

test('a Home Assistant button needs no shortcut and shows its own picture', async ({ page }) => {
  const { key, images } = await open(page, [{ options: { action: 'ha', entity: 'script.room_focus', label: 'Focus', picture: '0123456789abcdef', cover: true } }]);
  await expect(key(0)).toHaveClass(/k-cover/);
  await expect(key(0).locator('.k-label')).toHaveText('Focus');
  expect(images).toContain('own:0123456789abcdef');
  await expect(page.locator('[data-id="ha-btn"] .empty')).toHaveCount(0); // no "pick a shortcut"
  await expect(key(0)).not.toHaveClass(/toggle/); // a script runs; it has no on / off to show
  await key(0).click();
  await expect(key(0)).toHaveClass(/\bok\b/);
});

test('a switch shows its state, takes the entity name, and flips when tapped', async ({ page }) => {
  const { key } = await open(page, [{ options: { action: 'ha', entity: 'light.desk' } }]);
  await expect(key(0)).toHaveClass(/toggle/);
  await expect(key(0)).toHaveClass(/\bon\b/);
  await expect(key(0).locator('.k-label')).toHaveText('Desk lamp');
  // "on" shows as a lit key, without the shortcut grid's corner dot
  expect(await key(0).evaluate((el) => getComputedStyle(el, '::before').display)).toBe('none');
  await key(0).click();
  await expect(key(0)).not.toHaveClass(/\bon\b/);
  await key(0).click();
  await expect(key(0)).toHaveClass(/\bon\b/);
});

test('a button without an entity asks for one', async ({ page }) => {
  await open(page, [{ options: { action: 'ha', entity: '' } }]);
  await expect(page.locator('[data-id="ha-btn"] .w-button .empty')).toHaveText('Pick a Home Assistant entity in the settings');
});

test('a shortcut button uses its own picture and label over the shortcut\'s', async ({ page }) => {
  const { key, images } = await open(page, [{ options: { id: 'art', label: 'Mine', picture: 'fedcba9876543210', cover: true } }],
    [{ id: 'art', label: 'Artwork', icon: '🎨', color: '#6b7280', iconRev: 1, iconUpload: true }]);
  await expect(key(0)).toHaveClass(/k-cover/);
  await expect(key(0).locator('.k-label')).toHaveText('Mine');
  expect(images).toContain('own:fedcba9876543210');
  expect(images).not.toContain('shortcut');
});

test('a PC lighting button shows its name and works as an on / off key', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  const { key } = await open(page, [{ options: { action: 'lighting' } }]);
  await expect(key(0).locator('.k-label')).toHaveText('PC lighting');
  await expect(key(0)).toHaveClass(/toggle/);
  expect(errors).toEqual([]);
});

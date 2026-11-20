const { test, expect } = require('@playwright/test');

// Claude actions: the Claude Code sessions that need you. Approve / Deny go through the same approval the full-screen
// card answers; Continue / Retry go to the session the Stop hook holds (the demo has one: blog-engine). Every request
// names this widget, and nothing is answered while its "Allow answering from the phone" option is off.
async function layout(page, options) {
  await page.goto('/panel/');
  await page.evaluate((options) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'ca', type: 'claude-actions', x: 0, y: 0, w: 12, h: 6, options }] }] }));
  }, options);
  await page.reload();
  await expect(page.locator('.w.ca')).toBeVisible();
  await page.evaluate(() => {
    window.__posts = [];
    const demo = window.fetch;
    window.fetch = (url, init) => {
      if (init && init.method === 'POST' && /\/(approvals|claude\/held)\//.test(String(url))) window.__posts.push(String(url).replace(/^.*\/agent\/api/, ''));
      return demo(url, init);
    };
  });
}

test('approve answers the prompt for its session through the approval, and the widget stands in for the card', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, { control: true });
  const id = await page.evaluate(() => window.PhoneBoxDemo.ask({ tool: 'Run a command', detail: 'npm test', description: '', project: 'api-gateway' }).id);
  const item = page.locator('.ca-item.perm');
  await expect(item.locator('.ca-name')).toHaveText('api-gateway');
  await expect(item.locator('.ca-what')).toHaveText('Run a command');
  await expect(item.locator('.ca-detail')).toHaveText('npm test');
  // the widget on screen answers it, so the full-screen card stays out of the way
  await expect(page.locator('#approval')).toBeHidden();
  await item.locator('.ca-btn.ok').click();
  await expect.poll(() => page.evaluate(() => window.__posts)).toEqual([`/approvals/${id}?decision=allow&widget=test%2Fca`]);
  await expect(page.locator('.toast').last()).toContainText('Approve → api-gateway');
  await expect(page.locator('.ca-item.perm')).toHaveCount(0);
  await expect(page.locator('#approval')).toBeHidden();
  expect(errors).toEqual([]);
});

test('continue goes to the held session by name, and it leaves the list', async ({ page }) => {
  await layout(page, { control: true });
  const item = page.locator('.ca-item.held');
  await expect(item.locator('.ca-name')).toHaveText('blog-engine');
  await expect(item.locator('.ca-time')).toContainText('waits');
  await expect(item.locator('.ca-btn')).toHaveText(['Continue', 'Retry', 'Done']);
  await item.locator('.ca-btn.ok').click();
  await expect.poll(() => page.evaluate(() => window.__posts)).toEqual(['/claude/held/held1?action=continue&widget=test%2Fca']);
  await expect(page.locator('.toast').last()).toContainText('Continue → blog-engine');
  await expect(page.locator('.ca-item.held')).toHaveCount(0);
});

test('without its switch the widget shows what waits but answers nothing, and the card stays', async ({ page }) => {
  await layout(page, {});
  await expect(page.locator('.ca-note')).toHaveText('Turn on “Allow answering from the phone” in this widget’s settings');
  await expect(page.locator('.ca-item.held .ca-name')).toHaveText('blog-engine');
  await page.evaluate(() => window.PhoneBoxDemo.ask({ tool: 'Edit a file', detail: 'a.txt', description: '', project: 'docs-site' }));
  await expect(page.locator('.ca-item.perm .ca-name')).toHaveText('docs-site');
  await expect(page.locator('.w.ca .ca-btn')).toHaveCount(0);
  // nothing here may answer it, so the full-screen card shows it as before
  await expect(page.locator('#approval')).toBeVisible();
  expect(await page.evaluate(() => window.__posts)).toEqual([]);
});

test('the demo agent refuses a widget that does not allow answering', async ({ page }) => {
  await layout(page, {});
  const status = await page.evaluate(async () =>
    (await fetch('/agent/api/claude/held/held1?action=continue&widget=test%2Fca', { method: 'POST', headers: { 'X-PhoneBox-Panel': '1' } })).status);
  expect(status).toBe(403);
});

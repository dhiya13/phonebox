// Agenda rows give the title the width: the calendar colour is a thin bar on the row's edge instead of a dot column,
// and the time column is only as wide as the time. Before, dot + fixed 54px time + gaps took ~85px before the title.
const { test, expect } = require('@playwright/test');

test.use({ timezoneId: 'Europe/London' });

test('agenda titles start right after a compact time, with the colour as an edge bar', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-11-09T09:00:00Z'));
  await page.addInitScript((c) => localStorage.setItem('pb-demo-config', c), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: [], display: { clock24: true },
    pages: [{ id: 'ag', title: 'Agenda', widgets: [{ id: 'a', type: 'agenda', x: 0, y: 0, w: 6, h: 6, options: { days: 7 } }] }],
  }));
  await page.goto('/panel/');
  const rows = page.locator('[data-id="ag"] .ag-row');
  await expect(rows.first()).toBeVisible();
  await expect(page.locator('[data-id="ag"] .ag-dot')).toHaveCount(0);

  const timed = rows.filter({ has: page.locator('.ag-time:not(.allday)') });
  const geo = await timed.evaluateAll((els) => els.slice(0, 4).map((r) => {
    const rb = r.getBoundingClientRect(), t = r.querySelector('.ag-title').getBoundingClientRect();
    const bar = getComputedStyle(r, '::before');
    return { titleOffset: t.left - rb.left, titleLeft: t.left, bar: bar.content !== 'none' && parseFloat(bar.width) > 0 ? bar.backgroundColor : '' };
  }));
  expect(geo.length).toBeGreaterThan(1);
  for (const g of geo) {
    expect(g.titleOffset).toBeLessThan(55); // was ~85px with the dot column and the fixed time column
    expect(g.bar).toMatch(/^rgb/); // the calendar colour, as a bar on the edge
  }
  // 24-hour times share one width, so the titles of timed rows line up
  expect(Math.max(...geo.map((g) => g.titleLeft)) - Math.min(...geo.map((g) => g.titleLeft))).toBeLessThan(1.5);
});

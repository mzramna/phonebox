// Community plugins in the panel and the settings page, against the demo's copy of extras/plugins/http-status.
const { test, expect } = require('@playwright/test');

const WIDGETS = [
  { id: 'eps', type: 'plugin.http-status.endpoints', x: 0, y: 0, w: 6, h: 4, options: { title: 'My sites', urls: ['https://example.com', 'https://down.example'] } },
  { id: 'sum', type: 'plugin.http-status.summary', x: 6, y: 0, w: 3, h: 2, options: {} },
  { id: 'ghost', type: 'plugin.not-installed.thing', x: 9, y: 0, w: 3, h: 2, options: {} },
];

// The config is written from a page without the panel, so a panel still starting cannot overwrite it with the defaults.
async function show(page, enabled = true) {
  await page.goto('/widgets.json');
  await page.evaluate(({ widgets, enabled }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], plugins: [{ id: 'http-status', enabled }],
      pages: [{ id: 'home', title: 'Home', widgets }] }));
  }, { widgets: WIDGETS, enabled });
  await page.goto('/panel/');
}

test('a plugin UI widget gets its own data in a sandbox and sends only declared actions', async ({ page }) => {
  await show(page);
  const frame = page.frameLocator('.plugin-frame');
  await expect(frame.locator('li')).toHaveCount(2);
  await expect(frame.locator('li').first()).toContainText('example.com');
  await expect(frame.locator('li.error')).toContainText('down.example');
  await expect(frame.locator('#title')).toHaveText('My sites');
  await expect(page.locator('.plugin-frame')).toHaveAttribute('sandbox', 'allow-scripts');

  // the demo answers in the page (no network request), so the panel's calls are recorded by wrapping fetch
  await page.evaluate(() => { const original = window.fetch; window.pluginActions = [];
    window.fetch = (url, init) => { if (String(url).includes('/plugins/http-status/action')) window.pluginActions.push(new URL(url, location.href).searchParams.get('name')); return original(url, init); }; });
  const actions = () => page.evaluate(() => window.pluginActions);
  await frame.locator('#check').click();
  await expect.poll(actions).toEqual(['check']);

  // the frame cannot ask for actions its widget does not declare, nor reach the network (no "network" permission)
  const child = page.frames().find((f) => f !== page.mainFrame());
  expect(await child.evaluate(() => phonebox.action('rm-rf').then(() => 'sent', (e) => e.message))).toContain('does not declare');
  expect(await child.evaluate(() => fetch('https://example.com').then(() => 'reached', () => 'blocked'))).toBe('blocked');
  expect(await child.evaluate(() => { try { return String(parent.document.title); } catch (_) { return 'isolated'; } })).toBe('isolated');
  expect(await actions()).toEqual(['check']);
});

test('a plugin view widget looks like a pushed value, and a missing plugin shows a placeholder', async ({ page }) => {
  await show(page);
  const summary = page.locator('.w-plugin.w-push');
  await expect(summary).toContainText('Endpoints up');
  await expect(summary).toContainText('1/2');
  await expect(page.locator('.w-plugin .empty')).toContainText('not-installed');
});

test('a plugin that is turned off shows placeholders instead of running', async ({ page }) => {
  await show(page, false);
  await expect(page.locator('.plugin-frame')).toHaveCount(0);
  await expect(page.locator('.w-plugin .empty')).toHaveCount(3);
});

test('the settings page lists plugins; a click opens Configure first, then turn on / off and uninstall', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/widgets.json');
  await page.evaluate(() => localStorage.clear());
  await page.goto('/settings/#plugins');
  const row = page.locator('.pl-row', { hasText: 'HTTP status' });
  await expect(row).toContainText('0.1.0');
  await row.click();
  const menu = page.locator('#plModal .pl-menu .btn');
  await expect(menu.first()).toHaveText(/Configure/);
  await expect(menu).toContainText([/Configure/, /Turn on/, /Update/, /Uninstall/]);

  await menu.filter({ hasText: 'Configure' }).click();
  await expect(page.locator('#plModalBody')).toContainText('Configure HTTP status');
  await expect(page.locator('#plModalBody input[type=password]')).toHaveCount(1); // the "secret" setting
  await page.locator('#plModalBody input[type=password]').fill('Bearer t0ken');
  await page.locator('#plModalBody .btn.primary', { hasText: 'Save' }).click();
  await expect(page.locator('#save')).toBeDisabled();
  await expect(menu.first()).toHaveText(/Configure/); // saving goes back to the plugin's menu

  let warned = '';
  page.once('dialog', (d) => { warned = d.message(); d.accept(); });
  await menu.filter({ hasText: 'Turn on' }).click();
  await expect(menu.filter({ hasText: 'Turn off' })).toBeVisible();
  expect(warned).toContain('python3 main.py');
  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).plugins);
  expect(saved).toEqual([{ id: 'http-status', enabled: true, settings: { authHeader: 'Bearer t0ken' } }]);
  // the plugin's widgets are in the widget list, and its secrets never reach the panel's config
  const catalog = await page.evaluate(async () => (await (await fetch('/api/catalog')).json()).widgets.filter((w) => w.category === 'Plugins').map((w) => w.type));
  expect(catalog).toEqual(['plugin.http-status.endpoints', 'plugin.http-status.summary']);
  expect(await page.evaluate(async () => JSON.stringify(await (await fetch('/agent/api/config')).json()))).not.toContain('t0ken');

  await menu.filter({ hasText: 'Turn off' }).click();
  await expect(menu.filter({ hasText: 'Turn on' })).toBeVisible();
  // the web demo cannot delete files: uninstalling explains that instead
  page.once('dialog', (d) => d.accept());
  await menu.filter({ hasText: 'Uninstall' }).click();
  await expect(page.locator('#toast')).toContainText('needs the PhoneBox agent');
});

test('the layout editor live preview draws plugin widgets like the phone does', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/widgets.json');
  await page.evaluate((widgets) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], plugins: [{ id: 'http-status', enabled: true, settings: { authHeader: 'secret-in-preview' } }],
      pages: [{ id: 'home', title: 'Home', widgets }] }));
  }, WIDGETS.slice(0, 2));
  await page.goto('/settings/#pages');
  await page.locator('#stageSeg button[data-v="live"]').click();
  const panel = page.frameLocator('#previewBox iframe');
  await expect(panel.locator('.plugin-frame')).toHaveCount(1);
  await expect(panel.frameLocator('.plugin-frame').locator('li')).toHaveCount(2);
  await expect(panel.locator('.w-plugin.w-push')).toContainText('Endpoints up');
  await expect(panel.locator('.w-plugin .empty')).toHaveCount(0);
});

test('the phone keeps a plugin UI and its last data, and forgets them when the plugin goes away', async ({ page }) => {
  await show(page);
  await expect(page.frameLocator('.plugin-frame').locator('li')).toHaveCount(2);
  const stored = () => page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('pb-plugin-ui:')));
  await expect.poll(stored).toEqual(['pb-plugin-ui:http-status@0.1.0/endpoints']);
  expect(await page.evaluate(() => Object.keys(JSON.parse(localStorage.getItem('pluginState')).plugins['http-status']))).toContain('home.eps');

  // the computer can no longer hand out the UI: the stored copy is drawn
  let blocked = 0;
  await page.route('**/plugins/http-status/ui/endpoints.html', (r) => { blocked++; return r.abort(); });
  await page.reload();
  await expect(page.frameLocator('.plugin-frame').locator('li')).toHaveCount(2);
  await expect(page.frameLocator('.plugin-frame').locator('#title')).toHaveText('My sites');
  expect(blocked).toBeGreaterThan(0);

  // turned off on the computer: the stored UI is removed with the next config
  await page.evaluate(() => { const c = JSON.parse(localStorage.getItem('pb-demo-config')); c.plugins[0].enabled = false;
    localStorage.setItem('pb-demo-config', JSON.stringify(c)); localStorage.setItem('pb-demo-version', JSON.stringify(Date.now())); });
  await expect(page.locator('.w-plugin .empty')).toHaveCount(3, { timeout: 15000 });
  await expect.poll(stored).toEqual([]);
});

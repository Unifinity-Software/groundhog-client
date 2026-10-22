const path = require('node:path');
const assert = require('node:assert/strict');
const http = require('node:http');
const net = require('node:net');
const fs = require('node:fs/promises');
const { randomBytes } = require('node:crypto');
const { chromium } = require('playwright-core');
const { runTests } = require('@vscode/test-electron');

(async () => {
  const portProbe = net.createServer();
  await new Promise(resolve => portProbe.listen(0, '127.0.0.1', resolve));
  const debugPort = portProbe.address().port;
  await new Promise(resolve => portProbe.close(resolve));
  const token = randomBytes(16).toString('hex');
  let browser;
  const server = http.createServer(async (request, response) => {
    if (request.url !== '/' + token || request.method !== 'POST') { response.writeHead(404).end(); return; }
    try {
      browser = await chromium.connectOverCDP('http://127.0.0.1:' + debugPort);
      const page = browser.contexts()[0].pages().find(page => page.url().includes('workbench')) ?? browser.contexts()[0].pages()[0];
      const frame = page.frameLocator('iframe.webview').frameLocator('iframe#active-frame');
      await frame.locator('#rows tr').first().waitFor({ timeout: 15000 });
      await frame.locator('#method').selectOption('POST');
      await frame.locator('#method').selectOption('');
      await frame.locator('#rows tr').last().click();
      await frame.locator('#detail').waitFor({ state: 'visible' });
      assert.ok(await frame.locator('#replay').isDisabled(), 'Truncated requests cannot be replayed');
      assert.equal(await frame.locator('#capture').isDisabled(), false, 'Owner can enable capture from the header');
      await frame.locator('#capture').check();
      await frame.locator('#detail[data-capture="true"]').waitFor();
      await frame.locator('#rows tr').first().click();
      await frame.getByText('Signed webhooks may reject old timestamps when sent again.').waitFor();
      await frame.getByRole('button', { name: 'Show QR code' }).click();
      assert.ok(await frame.getByAltText('QR code of the public tunnel URL').isVisible());
      await fs.mkdir('test-results', { recursive: true });
      await page.screenshot({ path: 'test-results/groundhog-demo.png' });
      await frame.locator('body').screenshot({ path: 'test-results/groundhog-inspector-demo.png' });
      response.writeHead(200).end('ok');
    } catch (error) { console.error(error); response.writeHead(500).end(String(error)); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    await runTests({
      version: '1.143.0',
      extensionDevelopmentPath: path.resolve(__dirname, '../..'),
      extensionTestsPath: path.resolve(__dirname, 'suite.cjs'),
      launchArgs: ['--disable-extensions', '--disable-gpu', '--no-sandbox', '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=' + debugPort],
      extensionTestsEnv: { GROUNDHOG_SMOKE_CAPTURE_URL: 'http://127.0.0.1:' + server.address().port + '/' + token }
    });
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });

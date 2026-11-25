import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const evidenceDir = path.join(root, 'docs/stage13-evidence');
const screenshotsDir = path.join(evidenceDir, 'screenshots');
fs.mkdirSync(screenshotsDir, { recursive: true });

const results = [];
function record(name, status, details = '') {
  results.push({ name, status, details, timestamp: new Date().toISOString() });
  console.log(`[PROVENANCE E2E] [${status}] ${name}: ${details}`);
}
function assert(cond, msg) {
  if (!cond) throw new Error(msg);
}

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const consoleErrors = [];
const failedApiRequests = [];
const inferenceCalls = [];

page.on('pageerror', (err) => consoleErrors.push(`pageerror: ${err.message}`));
page.on('console', (msg) => {
  if (msg.type() === 'error') consoleErrors.push(`console.error: ${msg.text()}`);
});
page.on('request', (req) => {
  if (req.method() === 'POST' && (req.url().includes('/api/v1/extractions') || req.url().includes('/sign'))) {
    inferenceCalls.push(`${req.method()} ${req.url()}`);
  }
});
page.on('response', (resp) => {
  if (resp.url().includes('/api/v1/') && resp.status() >= 400) {
    failedApiRequests.push(`${resp.status()} ${resp.request().method()} ${resp.url()}`);
  }
});

try {
  // 1. Open queue (read-only, zero new notes, zero inference)
  await page.goto('http://localhost:4200/', { waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 20000 });

  // 2. Open persisted Local ONNX extraction: P100234 v33
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v33' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.model-provenance-badge', { timeout: 15000 });

  const v33Badge = (await page.locator('.model-provenance-badge').innerText()).trim();
  assert(v33Badge === 'Local ONNX', `Expected P100234 v33 badge "Local ONNX", got "${v33Badge}"`);
  assert((await page.locator('.model-provenance-badge').getAttribute('data-model-provenance')) === 'onnx-primary', 'Expected onnx-primary variant for v33');

  // Verify exact positioning: badge sits inside results-header beneath Extracted results & subtitle
  const h2Box = await page.locator('.results-header h2').boundingBox();
  const badgeBox = await page.locator('.model-provenance-badge').boundingBox();
  assert(h2Box && badgeBox && badgeBox.y > h2Box.y, 'Model provenance badge is not positioned directly below Extracted results heading');
  await page.screenshot({ path: path.join(screenshotsDir, '11-provenance-onnx-P100234-v33.png'), fullPage: false });
  record('Persisted Local ONNX (P100234 v33) displays "Local ONNX"', 'PASS', `Badge="${v33Badge}"`);

  // 3. Switch version using within-patient dropdown to P100234 v27 (persisted Microsoft Foundry GPT-6.1 Sol)
  const verSelect = page.locator('.within-patient-selector select');
  const v27Value = await verSelect.locator('option', { hasText: 'v27' }).first().getAttribute('value');
  assert(v27Value, 'P100234 v27 option missing from within-patient selector');
  await verSelect.selectOption(v27Value);
  await page.waitForFunction(
    () => document.querySelector('.model-provenance-badge')?.textContent?.trim() === '✦ GPT-6.1 Sol · Microsoft Foundry',
    null,
    { timeout: 15000 }
  );
  const v27Badge = (await page.locator('.model-provenance-badge').innerText()).trim();
  assert(v27Badge === '✦ GPT-6.1 Sol · Microsoft Foundry', `Expected GPT badge for v27, got "${v27Badge}"`);
  await page.screenshot({ path: path.join(screenshotsDir, '12-provenance-gpt-P100234-v27.png'), fullPage: false });
  record('Within-patient version switch (v33 ONNX -> v27 GPT) updates badge immediately', 'PASS', `Badge="${v27Badge}"`);

  // 4. Switch to P100234 v21 (older persisted Microsoft Foundry GPT-6.1 Sol)
  const v21Value = await verSelect.locator('option', { hasText: 'v21' }).first().getAttribute('value');
  assert(v21Value, 'P100234 v21 option missing');
  await verSelect.selectOption(v21Value);
  await page.waitForFunction(
    () => document.querySelector('.workspace-detail')?.textContent?.includes('Telmisartan') &&
          document.querySelector('.model-provenance-badge')?.textContent?.trim() === '✦ GPT-6.1 Sol · Microsoft Foundry',
    null,
    { timeout: 15000 }
  );
  record('Persisted GPT (P100234 v21) displays "✦ GPT-6.1 Sol · Microsoft Foundry"', 'PASS', 'Exact match');

  // 5. Switch to P100234 v31 (persisted Local ONNX)
  const v31Value = await verSelect.locator('option', { hasText: 'v31' }).first().getAttribute('value');
  assert(v31Value, 'P100234 v31 option missing');
  await verSelect.selectOption(v31Value);
  await page.waitForFunction(
    () => document.querySelector('.results-subtitle')?.textContent?.includes('Version 31') &&
          document.querySelector('.model-provenance-badge')?.textContent?.trim() === 'Local ONNX',
    null,
    { timeout: 15000 }
  );
  record('Persisted Local ONNX (P100234 v31) displays "Local ONNX"', 'PASS', 'Exact match');

  // 6. Switch to P100234 v23 (Failed AZURE_INVALID_EVIDENCE) -> MUST NOT show a completed-model badge
  const v23Value = await verSelect.locator('option', { hasText: 'v23' }).first().getAttribute('value');
  assert(v23Value, 'P100234 v23 option missing');
  await verSelect.selectOption(v23Value);
  await page.waitForSelector('.progress-state.progress-failed', { timeout: 15000 });
  assert((await page.locator('.model-provenance-badge').count()) === 0, 'Failed extraction v23 must NOT show a completed-model badge!');
  record('Failed extraction (P100234 v23) hides completed-model badge', 'PASS', 'BadgeCount=0, Error=AZURE_INVALID_EVIDENCE');

  // 7. Cross-UHID switch + delayed out-of-order response protection (P100234 v33 ONNX delayed 900ms -> immediately open P100237 v21 GPT)
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });

  const delayHandler = async (route) => {
    await new Promise((r) => setTimeout(r, 900));
    await route.continue();
  };
  await page.route('**/api/v1/queue/7b83e7bc-c817-4284-9306-8318b107adbb*', delayHandler);
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v33' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.btn-back', { timeout: 5000 });
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 5000 });
  await page.locator('.queue-row', { hasText: 'ENC-100570 · v21' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.model-provenance-badge', { timeout: 15000 });
  await page.waitForTimeout(1300);

  const p237Badge = (await page.locator('.model-provenance-badge').innerText()).trim();
  assert(p237Badge === '✦ GPT-6.1 Sol · Microsoft Foundry', `Delayed ONNX response corrupted P100237 v21 GPT badge: got "${p237Badge}"`);
  await page.unroute('**/api/v1/queue/7b83e7bc-c817-4284-9306-8318b107adbb*', delayHandler);
  record('Rapid UHID switch & delayed API response never displays stale provenance', 'PASS', `Retained P100237 v21 badge="${p237Badge}"`);

  // 8. Verify browser reload restores exact persisted provenance (open P100237 v21 again after reload)
  await page.reload({ waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 15000 });
  await page.locator('.queue-row', { hasText: 'ENC-100570 · v21' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.model-provenance-badge', { timeout: 15000 });
  const afterReloadBadge = (await page.locator('.model-provenance-badge').innerText()).trim();
  assert(afterReloadBadge === '✦ GPT-6.1 Sol · Microsoft Foundry', `Reload lost persisted GPT provenance: got "${afterReloadBadge}"`);
  record('Page refresh restores persisted model provenance from PostgreSQL', 'PASS', `Badge="${afterReloadBadge}"`);

  // 9. Verify zero inference calls, zero console errors, zero failed API requests
  assert(inferenceCalls.length === 0, `Unexpected inference triggered while viewing results: ${inferenceCalls.join(', ')}`);
  assert(consoleErrors.length === 0, `Console errors: ${consoleErrors.join('; ')}`);
  assert(failedApiRequests.length === 0, `Failed API requests: ${failedApiRequests.join('; ')}`);
  record('Zero inference calls, zero console errors, zero unexpected 4xx/5xx', 'PASS', 'Clean');

  fs.writeFileSync(
    path.join(evidenceDir, 'model-provenance-e2e-results.json'),
    JSON.stringify({ status: 'PASS', results, consoleErrors, failedApiRequests, inferenceCalls }, null, 2)
  );
  console.log('\nALL MODEL PROVENANCE E2E CHECKS PASSED!');
} catch (err) {
  console.error('\nMODEL PROVENANCE E2E FAILED:', err);
  fs.writeFileSync(
    path.join(evidenceDir, 'model-provenance-e2e-results.json'),
    JSON.stringify({ status: 'FAIL', error: String(err), results, consoleErrors, failedApiRequests, inferenceCalls }, null, 2)
  );
  process.exitCode = 1;
} finally {
  await browser.close();
}

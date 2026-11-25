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
  console.log(`[STAGE 13 E2E] [${status}] ${name}: ${details}`);
}
function assert(cond, msg) {
  if (!cond) throw new Error(msg);
}

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const consoleErrors = [];
const failedApiRequests = [];
let queuePollCount = 0;

page.on('pageerror', (err) => consoleErrors.push(`pageerror: ${err.message}`));
page.on('console', (msg) => {
  if (msg.type() === 'error') consoleErrors.push(`console.error: ${msg.text()}`);
});
page.on('response', (resp) => {
  const url = resp.url();
  if (url.includes('/api/v1/queue') && resp.request().method() === 'GET' && !url.match(/\/api\/v1\/queue\/[0-9a-f-]{36}/i)) {
    queuePollCount++;
  }
  if (url.includes('/api/v1/') && resp.status() >= 400) {
    failedApiRequests.push(`${resp.status()} ${resp.request().method()} ${url}`);
  }
});

try {
  // =========================================================================
  // 1. VERIFY TOP 10 INDIVIDUAL EXTRACTION SUBMISSIONS ON LOAD (READ-ONLY)
  // =========================================================================
  await page.goto('http://localhost:4200/', { waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 20000 });

  const rowCount = await page.locator('.queue-table tbody tr').count();
  assert(rowCount === 10, `Expected exactly 10 latest extraction submissions, got ${rowCount}`);

  // Confirm repeated patients appear as separate rows with distinct note versions
  const rowTexts = await page.locator('.queue-table tbody tr').allInnerTexts();
  const p234Rows = rowTexts.filter((t) => t.includes('P100234'));
  const p235Rows = rowTexts.filter((t) => t.includes('P100235'));
  const p237Rows = rowTexts.filter((t) => t.includes('P100237'));
  assert(p234Rows.length >= 4, `Expected >=4 rows for P100234 (v27,v25,v23,v21), got ${p234Rows.length}`);
  assert(p235Rows.length >= 3, `Expected >=3 rows for P100235 (v16,v14,v12), got ${p235Rows.length}`);
  assert(p237Rows.length >= 3, `Expected >=3 rows for P100237 (v21,v19,v17), got ${p237Rows.length}`);

  // Confirm zero unwanted clutter returned
  assert((await page.locator('.filter-pills, .filter-pill, .queue-count, aside, .portal-sidebar, .portal-primary-nav').count()) === 0, 'Unwanted legacy controls reappeared');
  const landingText = await page.locator('body').innerText();
  for (const forbidden of ['Ready · Verification recommended', 'Local ONNX', 'Validation warnings', 'Local ONNX limited-coverage mode']) {
    assert(!landingText.includes(forbidden), `Forbidden clutter text visible: "${forbidden}"`);
  }

  await page.screenshot({ path: path.join(screenshotsDir, '01-top10-queue-entries.png'), fullPage: false });
  record('Test A & B: Top 10 Individual Extraction Submissions Rendered', 'PASS', `Rows=${rowCount} (P100234=${p234Rows.length}, P100235=${p235Rows.length}, P100237=${p237Rows.length})`);

  // =========================================================================
  // 2. SCREENSHOT 2 — REPEATED UHID ENTRIES WITH DIFFERENT NOTE VERSIONS
  // =========================================================================
  await page.fill('input[type="search"]', 'P100234');
  await page.waitForTimeout(200);
  const filtered234 = await page.locator('.queue-table tbody tr').count();
  assert(filtered234 === 4, `Expected 4 P100234 versions in Top 10 (v27,v25,v23,v21), got ${filtered234}`);
  await page.screenshot({ path: path.join(screenshotsDir, '02-repeated-uhid-versions.png'), fullPage: false });
  await page.fill('input[type="search"]', '');
  await page.waitForTimeout(200);
  record('Screenshot 2: Repeated UHID P100234 Versions Shown Separately', 'PASS', `P100234 rows=${filtered234}`);

  // =========================================================================
  // 3. TEST D — SAME-PATIENT VERSION ISOLATION (P100234 v21 -> v25 -> v27)
  // =========================================================================
  // Open P100234 v21 (Case 1: Hypertension — Amlodipine, Atorvastatin, Telmisartan)
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v21' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 15000 });
  const v21Text = await page.locator('.workspace-detail').innerText();
  assert(v21Text.includes('Telmisartan') && v21Text.includes('Amlodipine'), 'P100234 v21 missing Telmisartan/Amlodipine');
  assert(!v21Text.includes('Colchicine') && !v21Text.includes('Pantoprazole'), 'Other version meds leaked into P100234 v21');

  // Return and open P100234 v25 (Case 7: Acid Peptic Disease — Ibuprofen negated, Pantoprazole)
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v25' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 15000 });
  const v25Text = await page.locator('.workspace-detail').innerText();
  assert(v25Text.includes('Pantoprazole') && v25Text.includes('Ibuprofen') && v25Text.includes('Negated'), 'P100234 v25 missing Pantoprazole/Negated Ibuprofen');
  assert(!v25Text.includes('Telmisartan') && !v25Text.includes('Colchicine'), 'Other version meds leaked into P100234 v25');

  // Return and open P100234 v27 (Case 10: Gout Flare — Historical Allopurinol, Colchicine, Etoricoxib)
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v27' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 15000 });
  const v27Text = await page.locator('.workspace-detail').innerText();
  assert(v27Text.includes('Allopurinol') && v27Text.includes('Historical') && v27Text.includes('Colchicine') && v27Text.includes('Etoricoxib'), 'P100234 v27 missing expected Gout extraction');
  assert(!v27Text.includes('Telmisartan') && !v27Text.includes('Pantoprazole'), 'Earlier version meds leaked into P100234 v27');

  await page.screenshot({ path: path.join(screenshotsDir, '03-P100234-v27-source-results.png'), fullPage: false });
  record('Test D: Same-Patient Version Isolation (P100234 v21 -> v25 -> v27)', 'PASS', 'All 3 versions rendered distinct source text and extractions');

  // =========================================================================
  // 4. TEST E — CROSS-PATIENT ISOLATION + DELAYED OUT-OF-ORDER SWITCHING
  // =========================================================================
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  await page.locator('.queue-row', { hasText: 'ENC-100570 · v21' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 15000 });
  const p237v21Text = await page.locator('.workspace-detail').innerText();
  assert(p237v21Text.includes('P100237') && p237v21Text.includes('Clopidogrel') && p237v21Text.includes('Rosuvastatin') && p237v21Text.includes('Isosorbide Mononitrate'), 'P100237 v21 missing cardiology meds');
  assert(!p237v21Text.includes('P100234') && !p237v21Text.includes('Colchicine') && !p237v21Text.includes('Allopurinol'), 'P100234 data leaked into P100237 v21');
  await page.screenshot({ path: path.join(screenshotsDir, '04-P100237-v21-distinct-patient.png'), fullPage: false });

  // Rapid out-of-order delayed response test: delay P100234 v27 by 900ms, immediately switch to P100235 v16
  const delayHandler = async (route) => {
    await new Promise((r) => setTimeout(r, 900));
    await route.continue();
  };
  await page.route('**/api/v1/queue/7b83e7bc-c817-4284-9306-8318b107adbb*', delayHandler);
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v27' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.btn-back', { timeout: 5000 });
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 5000 });
  await page.locator('.queue-row', { hasText: 'ENC-100568 · v16' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 10000 });
  await page.waitForTimeout(1300);
  const afterDelayText = await page.locator('.workspace-detail').innerText();
  assert(afterDelayText.includes('P100235') && afterDelayText.includes('Nitrofurantoin'), 'P100235 v16 not retained after delayed P100234 response');
  assert(!afterDelayText.includes('P100234') && !afterDelayText.includes('Colchicine'), 'Delayed P100234 response leaked into P100235 v16!');
  await page.unroute('**/api/v1/queue/7b83e7bc-c817-4284-9306-8318b107adbb*', delayHandler);
  record('Test E: Cross-Patient Isolation & Out-of-Order Delayed Response Check', 'PASS', 'P100237 v21 & P100235 v16 fully isolated');

  // =========================================================================
  // 5. TEST F — FAILED CASE 4 (P100234 v23: AZURE_INVALID_EVIDENCE)
  // =========================================================================
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v23' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.progress-state.progress-failed', { timeout: 15000 });
  const v23Text = await page.locator('.workspace-detail').innerText();
  assert(v23Text.includes('Discontinue Warfarin 5 mg immediately due to prolonged prothrombin time.'), 'Case 4 v23 source note missing');
  assert(v23Text.includes('Extraction failed') && v23Text.includes('AZURE_INVALID_EVIDENCE'), 'Case 4 v23 missing truthful failure state');
  assert((await page.locator('.medication-table, .investigation-table').count()) === 0, 'Failed Case 4 must not render fabricated medication/investigation tables');
  assert(!v23Text.includes('Colchicine'), 'v27 results leaked into failed v23 view');
  await page.screenshot({ path: path.join(screenshotsDir, '05-P100234-v23-failed-case4.png'), fullPage: false });
  record('Test F: Failed Case 4 (P100234 v23) Truthful Failure State & Zero Fabrication', 'PASS', 'Displayed v23 source note + AZURE_INVALID_EVIDENCE');

  // =========================================================================
  // 6. TEST C — READ-ONLY AUTOMATIC POLLING & BROWSER RELOAD PERSISTENCE
  // =========================================================================
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  const pollsBeforeWait = queuePollCount;
  await page.waitForTimeout(4600);
  assert(queuePollCount > pollsBeforeWait, `Expected automatic 4s polling refresh (${pollsBeforeWait} -> ${queuePollCount})`);
  assert((await page.locator('.queue-table tbody tr').count()) === 10, 'Polling altered row count away from 10');
  await page.screenshot({ path: path.join(screenshotsDir, '06-queue-after-auto-poll.png'), fullPage: false });

  await page.reload({ waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 15000 });
  const afterReloadRows = await page.locator('.queue-table tbody tr').count();
  assert(afterReloadRows === 10, `Reload collapsed queue from 10 rows to ${afterReloadRows}`);
  record('Test C: Automatic Read-Only Polling & Browser Reload Retention', 'PASS', `Polls=${pollsBeforeWait}->${queuePollCount}, ReloadRows=${afterReloadRows}`);

  // =========================================================================
  // 7. RESPONSIVE LAYOUT (768x1024)
  // =========================================================================
  await page.locator('.queue-row', { hasText: 'ENC-100567 · v27' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 10000 });
  await page.setViewportSize({ width: 768, height: 1024 });
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(300);
  const overflowX = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  assert(overflowX <= 4, `Horizontal overflow at 768px: ${overflowX}px`);
  await page.screenshot({ path: path.join(screenshotsDir, '07-responsive-layout.png'), fullPage: false });
  record('Screenshot 7: Responsive Stacked Layout (768x1024)', 'PASS', `HorizontalOverflow=${overflowX}px`);

  assert(consoleErrors.length === 0, `Console/page errors: ${consoleErrors.join('; ')}`);
  assert(failedApiRequests.length === 0, `Unexpected API errors: ${failedApiRequests.join('; ')}`);
  record('Runtime Cleanliness: Zero Console Errors & Zero Unexpected 4xx/5xx', 'PASS', 'Clean');

  fs.writeFileSync(
    path.join(evidenceDir, 'stage13-e2e-results.json'),
    JSON.stringify({ status: 'PASS', results, consoleErrors, failedApiRequests }, null, 2)
  );
  console.log('\nALL STAGE 13 E2E CHECKS PASSED!');
} catch (err) {
  console.error('\nSTAGE 13 E2E FAILED:', err);
  fs.writeFileSync(
    path.join(evidenceDir, 'stage13-e2e-results.json'),
    JSON.stringify({ status: 'FAIL', error: String(err), results, consoleErrors, failedApiRequests }, null, 2)
  );
  process.exitCode = 1;
} finally {
  await browser.close();
}

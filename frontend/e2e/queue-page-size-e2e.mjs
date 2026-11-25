import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const evidenceDir = process.env.APOLLO_E2E_OUTPUT_ROOT || path.join(root, 'docs/stage13-evidence');
const screenshotsDir = path.join(evidenceDir, 'screenshots');
fs.mkdirSync(screenshotsDir, { recursive: true });

const results = [];
function record(name, status, details = '') {
  results.push({ name, status, details, timestamp: new Date().toISOString() });
  console.log(`[PAGE-SIZE E2E] [${status}] ${name}: ${details}`);
}
function assert(cond, msg) {
  if (!cond) throw new Error(msg);
}

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const consoleErrors = [];
const failedApiRequests = [];
const queueRequests = [];
const unexpectedMutations = [];

page.on('pageerror', (err) => consoleErrors.push(`pageerror: ${err.message}`));
page.on('console', (msg) => {
  if (msg.type() === 'error') consoleErrors.push(`console.error: ${msg.text()}`);
});
page.on('request', (req) => {
  const url = req.url();
  if (url.includes('/api/v1/extractions') && req.method() === 'POST') {
    unexpectedMutations.push(`${req.method()} ${url}`);
  }
});
page.on('response', (resp) => {
  const url = resp.url();
  const method = resp.request().method();
  if (
    method === 'GET' &&
    url.includes('/api/v1/queue') &&
    !url.match(/\/api\/v1\/queue\/[0-9a-f-]{36}/i)
  ) {
    const parsed = new URL(url);
    queueRequests.push({
      limit: parsed.searchParams.get('limit'),
      search: parsed.searchParams.get('search'),
      status: resp.status(),
      at: Date.now(),
    });
  }
  if (url.includes('/api/v1/') && resp.status() >= 400) {
    failedApiRequests.push(`${resp.status()} ${method} ${url}`);
  }
});

try {
  // 1. Load queue and verify default Show entries = 10 & horizontal alignment with search
  await page.goto('http://localhost:4200/', { waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 20000 });

  const select = page.locator('select[aria-label="Show entries"]');
  await select.waitFor({ state: 'visible', timeout: 10000 });

  const options = await select.locator('option').allInnerTexts();
  assert(
    JSON.stringify(options.map((s) => s.trim())) === JSON.stringify(['5', '10']),
    `Expected options [5, 10], got ${JSON.stringify(options)}`
  );
  assert((await select.inputValue()) === '10', `Expected default value 10, got ${await select.inputValue()}`);

  const searchBox = await page.locator('.search-wrap input').boundingBox();
  const controlBox = await page.locator('.entries-control').boundingBox();
  assert(searchBox && controlBox, 'Toolbar search or Show entries bounding box missing');
  assert(
    Math.abs(searchBox.y - controlBox.y) < 12 && controlBox.x > searchBox.x + searchBox.width,
    `Show entries control is not aligned horizontally to the right of search (search=${JSON.stringify(searchBox)}, control=${JSON.stringify(controlBox)})`
  );

  const initialRows = await page.locator('.queue-table tbody tr').count();
  assert(initialRows === 10, `Expected 10 rows on initial load, got ${initialRows}`);

  const initialRowTexts = await page.locator('.queue-table tbody tr').allInnerTexts();
  const expectedQueue = await (await page.request.get('http://localhost:4200/api/v1/queue?limit=10')).json();
  const p234Initial = initialRowTexts.filter((t) => t.includes('P100234'));
  assert(p234Initial.length >= 4, `Expected repeated UHID P100234 rows in top 10, got ${p234Initial.length}`);
  assert(
    initialRowTexts[0].includes(expectedQueue.items[0].patientReference) && initialRowTexts[0].includes(`v${expectedQueue.items[0].noteVersion}`),
    `Newest row differs from the current persisted API queue: ${initialRowTexts[0]}`
  );

  await page.screenshot({ path: path.join(screenshotsDir, '08-show-entries-10.png'), fullPage: false });
  record(
    'Default Show entries = 10, toolbar alignment, newest-first & repeated UHIDs',
    'PASS',
    `Rows=${initialRows}, P100234=${p234Initial.length}, FirstRow=${initialRowTexts[0].split('\n')[0]}`
  );

  // 2. Switch dropdown to 5 -> immediate API call GET /api/v1/queue?limit=5 (no reload)
  const navEntriesBefore = await page.evaluate(() => performance.getEntriesByType('navigation').length);
  const resp5Promise = page.waitForResponse(
    (r) => r.request().method() === 'GET' && r.url().includes('/api/v1/queue') && new URL(r.url()).searchParams.get('limit') === '5'
  );
  await select.selectOption('5');
  const resp5 = await resp5Promise;
  assert(resp5.status() === 200, `Expected HTTP 200 for limit=5, got ${resp5.status()}`);
  await page.waitForFunction(() => document.querySelectorAll('.queue-table tbody tr').length === 5, null, { timeout: 5000 });

  const navEntriesAfter = await page.evaluate(() => performance.getEntriesByType('navigation').length);
  assert(navEntriesAfter === navEntriesBefore, 'Changing dropdown caused a browser page reload');

  const fiveRowTexts = await page.locator('.queue-table tbody tr').allInnerTexts();
  assert(fiveRowTexts.length === 5, `Expected 5 rows, got ${fiveRowTexts.length}`);
  // First 5 rows must exactly match the first 5 rows of the top-10 list (newest-first preserved)
  for (let i = 0; i < 5; i++) {
    const expectedMeta = initialRowTexts[i].split('\n').slice(0, 2).join(' ');
    const actualMeta = fiveRowTexts[i].split('\n').slice(0, 2).join(' ');
    assert(actualMeta === expectedMeta, `Row ${i} mismatch: expected "${expectedMeta}", got "${actualMeta}"`);
  }
  const p234InFive = fiveRowTexts.filter((t) => t.includes('P100234'));
  assert(p234InFive.length === expectedQueue.items.slice(0, 5).filter(x => x.patientReference === 'P100234').length, 'Top-five repeated UHID count differs from the API');

  await page.screenshot({ path: path.join(screenshotsDir, '09-show-entries-5.png'), fullPage: false });
  record(
    'Select 5 -> immediate ?limit=5 fetch, 5 newest rows, repeated UHIDs, zero reload',
    'PASS',
    `Rows=5, P100234=${p234InFive.length}`
  );

  // 3. Automatic polling preserves selected page size = 5
  const reqCountBeforePoll = queueRequests.length;
  await page.waitForTimeout(4600);
  const newPolls = queueRequests.slice(reqCountBeforePoll);
  assert(newPolls.length >= 1, `Expected automatic 4s poll while on limit=5, got ${newPolls.length}`);
  assert(newPolls.every((p) => p.limit === '5'), `Polling did not preserve limit=5: ${JSON.stringify(newPolls)}`);
  assert((await page.locator('.queue-table tbody tr').count()) === 5, 'Polling changed row count away from 5');
  assert((await select.inputValue()) === '5', 'Polling reset dropdown away from 5');
  record('Automatic 4s polling preserves limit=5', 'PASS', `PollsObserved=${newPolls.length}, AllLimit5=true`);

  // 4. Open extraction & return -> preserves Show entries = 5 and isolates patient/version
  const selected = expectedQueue.items.slice(0, 5).find(x => x.queueState === 'Ready' && x.extractionId);
  assert(selected, 'No completed extraction available in the first five rows');
  const expectedDetail = await (await page.request.get(`http://localhost:4200/api/v1/queue/${selected.noteId}?version=${selected.noteVersion}&patient=${selected.patientReference}`)).json();
  await page.locator('.queue-row', { hasText: `${selected.encounterReference} · v${selected.noteVersion}` }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 15000 });
  const detailText = await page.locator('.workspace-detail').innerText();
  assert(
    detailText.includes(selected.patientReference) && expectedDetail.extraction.medications.every(m => detailText.includes(m.name)),
    'Opened detail does not display the selected persisted patient and medication entities'
  );
  assert(expectedDetail.noteVersion === selected.noteVersion && expectedDetail.extractionId === selected.extractionId, 'Selected note version or extraction identity changed');

  const backRespPromise = page.waitForResponse(
    (r) => r.request().method() === 'GET' && r.url().includes('/api/v1/queue') && new URL(r.url()).searchParams.get('limit') === '5'
  );
  await page.click('.btn-back');
  await backRespPromise;
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  assert((await select.inputValue()) === '5', `Returning from detail reset Show entries to ${await select.inputValue()}`);
  assert((await page.locator('.queue-table tbody tr').count()) === 5, 'Returning from detail did not preserve 5 rows');
  record('Preserves Show entries = 5 after returning from opened extraction', 'PASS', 'Dropdown=5, Rows=5, Isolation=PASS');

  // 5. Search consistency with API pagination (?search=P100234&limit=5 then &limit=10)
  const search5Promise = page.waitForResponse(
    (r) =>
      r.request().method() === 'GET' &&
      r.url().includes('/api/v1/queue') &&
      new URL(r.url()).searchParams.get('search') === 'P100234' &&
      new URL(r.url()).searchParams.get('limit') === '5'
  );
  await page.fill('input[type="search"]', 'P100234');
  await search5Promise;
  await page.waitForFunction(
    () => {
      const rows = Array.from(document.querySelectorAll('.queue-table tbody tr'));
      return rows.length === 5 && rows.every((r) => r.textContent?.includes('P100234'));
    },
    null,
    { timeout: 5000 }
  );

  // Switch Show entries to 10 while search="P100234" -> returns 10 newest P100234 submissions
  const search10Promise = page.waitForResponse(
    (r) =>
      r.request().method() === 'GET' &&
      r.url().includes('/api/v1/queue') &&
      new URL(r.url()).searchParams.get('search') === 'P100234' &&
      new URL(r.url()).searchParams.get('limit') === '10'
  );
  await select.selectOption('10');
  await search10Promise;
  await page.waitForFunction(
    () => {
      const rows = Array.from(document.querySelectorAll('.queue-table tbody tr'));
      return rows.length === 10 && rows.every((r) => r.textContent?.includes('P100234'));
    },
    null,
    { timeout: 5000 }
  );

  // Clear search -> returns 10 newest overall submissions
  const clear10Promise = page.waitForResponse(
    (r) =>
      r.request().method() === 'GET' &&
      r.url().includes('/api/v1/queue') &&
      !new URL(r.url()).searchParams.has('search') &&
      new URL(r.url()).searchParams.get('limit') === '10'
  );
  await page.fill('input[type="search"]', '');
  await clear10Promise;
  await page.waitForFunction(() => document.querySelectorAll('.queue-table tbody tr').length === 10, null, { timeout: 5000 });
  record('Search consistent with API ?search=...&limit=5 and ?limit=10', 'PASS', 'P100234 search returned 5 then 10 newest rows');

  // 6. Verify no forbidden clutter & zero inference triggers & zero console/API errors
  assert((await page.locator('.filter-pills, .filter-pill, .queue-count, aside, .portal-sidebar').count()) === 0, 'Forbidden UI controls present');
  const bodyText = await page.locator('body').innerText();
  for (const forbidden of ['Local ONNX', 'Validation warnings', 'Doctor Dashboard']) {
    assert(!bodyText.includes(forbidden), `Forbidden text found: ${forbidden}`);
  }
  assert(unexpectedMutations.length === 0, `Unexpected extraction/inference requests fired: ${unexpectedMutations.join(', ')}`);
  assert(consoleErrors.length === 0, `Console errors: ${consoleErrors.join('; ')}`);
  assert(failedApiRequests.length === 0, `Failed API requests: ${failedApiRequests.join('; ')}`);
  record('Zero inference triggers, zero clutter, zero console/API errors', 'PASS', 'Clean');

  fs.writeFileSync(
    path.join(evidenceDir, 'queue-page-size-e2e-results.json'),
    JSON.stringify({ status: 'PASS', results, consoleErrors, failedApiRequests, unexpectedMutations }, null, 2)
  );
  console.log('\nALL SHOW-ENTRIES E2E CHECKS PASSED!');
} catch (err) {
  console.error('\nSHOW-ENTRIES E2E FAILED:', err);
  fs.writeFileSync(
    path.join(evidenceDir, 'queue-page-size-e2e-results.json'),
    JSON.stringify({ status: 'FAIL', error: String(err), results, consoleErrors, failedApiRequests, unexpectedMutations }, null, 2)
  );
  process.exitCode = 1;
} finally {
  await browser.close();
}

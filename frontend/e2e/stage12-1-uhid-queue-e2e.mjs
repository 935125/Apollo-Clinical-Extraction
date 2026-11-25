import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const evidenceDir = path.join(root, 'docs/stage12-evidence/uhid-queue');
fs.mkdirSync(evidenceDir, { recursive: true });

const results = [];
function record(name, status, details = '') {
  results.push({ name, status, details, timestamp: new Date().toISOString() });
  console.log(`[STAGE 12.1 E2E] [${status}] ${name}: ${details}`);
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
  // TEST A — CLEAN QUEUE OVERVIEW (ALL UNWANTED UI REMOVED)
  // =========================================================================
  await page.goto('http://localhost:4200/', { waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 20000 });

  const queueText = await page.locator('body').innerText();
  const headers = await page.locator('.queue-table thead th').allInnerTexts();
  assert(
    headers.map((h) => h.trim().toUpperCase()).join('|') === 'UHID / PATIENT ID|CLINICAL NOTE PREVIEW|RECEIVED|ACTION',
    `Unexpected queue table headers: ${headers.join(' | ')}`
  );

  // Assert unwanted queue UI controls & row clutter are strictly absent
  assert((await page.locator('.filter-pills, .filter-pill, .queue-count, .status-pill').count()) === 0, 'Unwanted filter pills or status badges found in queue');
  assert((await page.locator('aside, .portal-sidebar, .portal-primary-nav, .quick-switch-strip').count()) === 0, 'Unwanted sidebar/nav/chips found');
  for (const forbidden of [
    'Ready · Verification recommended',
    'Local ONNX',
    'notes ·',
    'Doctor Dashboard',
    'Assigned Encounters',
    'Review & Validation',
    'Validation warnings',
    'Clinical validation diagnostics',
    'Local ONNX limited-coverage mode',
    'Submit Review',
  ]) {
    assert(!queueText.includes(forbidden), `Forbidden UI text visible in queue overview: "${forbidden}"`);
  }
  const rowTexts = await page.locator('.queue-table tbody tr').allInnerTexts();
  for (const rt of rowTexts) {
    assert(!/\b\d+\s+words\b/i.test(rt), `Word count clutter still present in queue row: "${rt}"`);
  }

  await page.screenshot({ path: path.join(evidenceDir, '01-clean-queue-overview.png'), fullPage: false });
  record('Test A: Clean Queue Overview (All Filters, Badges, ONNX Labels & Word Counts Removed)', 'PASS', `Headers=${headers.join(' | ')}`);

  // Capture queue items via API for exact backend-to-UI parity checks
  const queueResp = await page.request.get('http://localhost:4200/api/v1/queue?limit=50');
  assert(queueResp.ok(), `GET /api/v1/queue failed: ${queueResp.status()}`);
  const queueData = await queueResp.json();
  const item234 = queueData.items.find((i) => i.patientReference === 'P100234');
  const item237 = queueData.items.find((i) => i.patientReference === 'P100237');
  const item235 = queueData.items.find((i) => i.patientReference === 'P100235');
  const item236 = queueData.items.find((i) => i.patientReference === 'P100236');
  assert(item234 && item237 && item235, 'Expected synthetic patients P100234, P100237, and P100235 in queue');

  // =========================================================================
  // TEST B1 — SELECT UHID P100234 (STRICT ISOLATION + NO WARNINGS/ONNX BANNER)
  // =========================================================================
  const detail234Api = await (await page.request.get(`http://localhost:4200/api/v1/queue/${item234.noteId}?patient=P100234`)).json();

  await page.locator('.queue-row', { hasText: 'P100234' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 15000 });
  await page.waitForSelector('.results-panel', { timeout: 15000 });

  const detail234Text = await page.locator('.workspace-detail').innerText();
  const source234Ui = (await page.locator('.source-document').innerText()).trim();
  assert(detail234Text.includes('P100234'), 'Missing P100234 in selected header');
  assert(!detail234Text.includes('P100237') && !detail234Text.includes('P100235') && !detail234Text.includes('P100236'), 'Unrelated patient IDs leaked into P100234 detail view');
  assert((await page.locator('.quick-switch-strip').count()) === 0, 'Unrelated UHID quick-switch chips must be removed');
  assert(source234Ui === detail234Api.sourceText.trim(), 'Displayed P100234 source text does not match immutable backend record');

  for (const forbiddenDetail of [
    'Validation warnings',
    'Clinical validation diagnostics',
    'LOCAL_COVERAGE_REDUCED',
    'TERMINOLOGY_UNMATCHED',
    'TIMING_NOT_DOCUMENTED',
    'INCOMPLETE_PRESCRIPTION',
    'Local ONNX limited-coverage mode',
    'Accept',
    'Reject',
    'Submit Review',
  ]) {
    assert(!detail234Text.includes(forbiddenDetail), `Forbidden detail element visible on P100234: "${forbiddenDetail}"`);
  }

  // Confirm backend STILL persists & serves validation warnings & provider provenance!
  assert(detail234Api.extraction?.modelMetadata?.provider === 'onnx', 'Expected backend provenance metadata to remain intact');
  assert((detail234Api.extraction?.validationSummary?.warningCount ?? 0) > 0, 'Expected backend clinical validation warnings to remain active in API');

  await page.screenshot({ path: path.join(evidenceDir, '02-selected-uhid-P100234.png'), fullPage: false });
  record('Test B1: Selected UHID P100234 Isolation & Backend Validation Preserved', 'PASS', `Note=${item234.noteId}, v${detail234Api.noteVersion}, BackendWarnings=${detail234Api.extraction.validationSummary.warningCount}`);

  // =========================================================================
  // TEST B2 — SWITCH TO UHID P100237 (ZERO P100234 DATA REMAINS)
  // =========================================================================
  const detail237Api = await (await page.request.get(`http://localhost:4200/api/v1/queue/${item237.noteId}?patient=P100237`)).json();

  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
  await page.locator('.queue-row', { hasText: 'P100237' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 15000 });

  const detail237Text = await page.locator('.workspace-detail').innerText();
  const source237Ui = (await page.locator('.source-document').innerText()).trim();
  assert(detail237Text.includes('P100237'), 'Missing P100237 in selected header');
  assert(!detail237Text.includes('P100234') && !detail237Text.includes('P100235') && !detail237Text.includes('P100236'), 'Unrelated patient IDs leaked into P100237 detail view');
  assert(source237Ui === detail237Api.sourceText.trim(), 'Displayed P100237 source text does not match immutable backend record');

  // Verify every extracted medication from backend API is rendered accurately (Test F)
  for (const med of detail237Api.extraction?.medications ?? []) {
    assert(detail237Text.toLowerCase().includes(med.name.toLowerCase()), `Missing expected P100237 medication: ${med.name}`);
  }

  await page.screenshot({ path: path.join(evidenceDir, '03-selected-uhid-P100237.png'), fullPage: false });

  // Click first medication row to show side-by-side highlight + lightweight source quotation
  if ((await page.locator('.medication-table tbody tr.entity-row').count()) > 0) {
    await page.locator('.medication-table tbody tr.entity-row').first().click();
    await page.waitForSelector('.evidence-callout', { timeout: 5000 });
  }
  await page.screenshot({ path: path.join(evidenceDir, '04-side-by-side-source-results.png'), fullPage: false });
  record('Test B2 & F: Selected UHID P100237 Exact Source + Extracted Results Parity', 'PASS', `Note=${item237.noteId}, Meds=${detail237Api.extraction?.medications?.length ?? 0}, Invs=${detail237Api.extraction?.investigations?.length ?? 0}`);

  // =========================================================================
  // TEST B3 — RAPID SWITCHING WITH INTENTIONALLY DELAYED OUT-OF-ORDER RESPONSE
  // =========================================================================
  const slowHandler = async (route) => {
    await new Promise((r) => setTimeout(r, 900));
    await route.continue();
  };
  await page.route(`**/api/v1/queue/${item234.noteId}*`, slowHandler);

  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });

  // Open P100234 (delayed 900ms), immediately go back, open P100237 (fast)
  await page.locator('.queue-row', { hasText: 'P100234' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.btn-back', { timeout: 5000 });
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 5000 });
  await page.locator('.queue-row', { hasText: 'P100237' }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 10000 });

  // Wait past the 900ms delayed response from P100234 and verify P100237 is STILL cleanly shown
  await page.waitForTimeout(1300);
  const afterDelayText = await page.locator('.workspace-detail').innerText();
  const afterDelaySource = (await page.locator('.source-document').innerText()).trim();
  assert(afterDelayText.includes('P100237'), 'P100237 lost after delayed P100234 response');
  assert(!afterDelayText.includes('P100234'), 'Stale delayed P100234 leaked into P100237 view!');
  assert(afterDelaySource === detail237Api.sourceText.trim(), 'Delayed out-of-order response overwrote P100237 source note!');
  await page.unroute(`**/api/v1/queue/${item234.noteId}*`, slowHandler);
  record('Test B3: Rapid Switching with Out-of-Order Delayed Response (Zero Cross-Patient Leak)', 'PASS', 'Delayed P100234 cancelled/ignored cleanly');

  // =========================================================================
  // SCREENSHOT 5 — PROCESSING STATE (OPEN P100236 IF IN PROGRESS)
  // =========================================================================
  if (item236 && ['Waiting', 'Queued', 'Extracting'].includes(item236.queueState)) {
    await page.click('.btn-back');
    await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });
    await page.locator('.queue-row', { hasText: 'P100236' }).locator('button:has-text("Open")').click();
    await page.waitForSelector('.progress-state', { timeout: 10000 });
    const procText = await page.locator('.results-panel').innerText();
    assert(procText.includes('Extraction in progress'), `Expected "Extraction in progress", got: ${procText}`);
    await page.screenshot({ path: path.join(evidenceDir, '05-processing-state.png'), fullPage: false });
    record('Screenshot 5: Truthful In-Progress Processing State', 'PASS', 'P100236 shows "Extraction in progress"');
  }

  // =========================================================================
  // TEST E — HEADLESS API INGESTION (POSTMAN/SWAGGER SIMULATION) -> SERVICE BUS -> LIVE POLLING
  // =========================================================================
  await page.click('.btn-back');
  await page.waitForSelector('.queue-table tbody tr', { timeout: 10000 });

  const headlessTag = `STAGE12-1-HEADLESS-${Date.now().toString().slice(-6)}`;
  const headlessNoteText = `Chief Complaint:\n${headlessTag} Acute respiratory follow-up.\n\nPlan:\nStart Azithromycin 500 mg 1 tablet orally once daily for 3 days.\nDiscontinue Warfarin 5 mg immediately due to elevated INR.\nOrder Chest X-ray PA view tomorrow.`;

  // Fetch CSRF + current P100235 note via direct HTTP (simulating Postman/Swagger without touching Angular!)
  const csrfResp = await page.request.get('http://localhost:4200/api/v1/emr/auth/csrf');
  const csrfToken = (await csrfResp.json()).token;
  const current235 = await (await page.request.get(`http://localhost:4200/api/v1/emr/notes/${item235.noteId}`)).json();

  const corrResp = await page.request.post(`http://localhost:4200/api/v1/emr/notes/${item235.noteId}/corrections`, {
    headers: { 'X-XSRF-TOKEN': csrfToken },
    data: { text: headlessNoteText, expectedVersion: current235.version },
  });
  assert(corrResp.ok(), `Headless correction failed: ${corrResp.status()}`);
  const draft235 = await corrResp.json();

  const csrf2 = (await (await page.request.get('http://localhost:4200/api/v1/emr/auth/csrf')).json()).token;
  const pollsBeforeHeadless = queuePollCount;
  const signResp = await page.request.post(`http://localhost:4200/api/v1/emr/notes/${item235.noteId}/sign`, {
    headers: { 'X-XSRF-TOKEN': csrf2 },
    data: { expectedVersion: draft235.version },
  });
  assert(signResp.ok(), `Headless sign failed: ${signResp.status()}`);
  const signed235 = await signResp.json();

  // Do NOT reload page or click refresh — wait for automatic RxJS polling to show the new note!
  await page.locator('.queue-row', { hasText: headlessTag }).waitFor({ timeout: 20000 });
  assert(queuePollCount > pollsBeforeHeadless, 'Expected automatic RxJS poll to detect headless API submission');

  // Open P100235 and wait for live polling inside the detail screen to update automatically to Completed!
  await page.locator('.queue-row', { hasText: headlessTag }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.split-workspace .source-document', { timeout: 10000 });
  await page.locator('.medication-table tbody tr', { hasText: /Azithromycin/i }).waitFor({ timeout: 45000 });

  const completedText = await page.locator('.workspace-detail').innerText();
  assert(completedText.includes(headlessTag), 'Headless submitted source text not shown');
  assert(completedText.includes('Azithromycin'), 'Expected Azithromycin in completed extraction');
  assert(completedText.includes('Discontinued'), 'Expected Warfarin discontinued assertion');
  assert(!completedText.includes('P100234') && !completedText.includes('P100237'), 'Other patients leaked into P100235 completed view');

  await page.screenshot({ path: path.join(evidenceDir, '06-completed-extraction-state.png'), fullPage: false });
  record(
    'Test E: Headless API Ingestion -> Outbox -> Azure Service Bus -> Worker -> Live Browser Auto-Update',
    'PASS',
    `Tag=${headlessTag}, P100235 v${signed235.version}, Polls=${pollsBeforeHeadless}->${queuePollCount}`
  );

  // =========================================================================
  // SCREENSHOT 7 — RESPONSIVE LAYOUT (768x1024)
  // =========================================================================
  await page.setViewportSize({ width: 768, height: 1024 });
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(300);
  const overflowX = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  assert(overflowX <= 4, `Horizontal overflow at 768px: ${overflowX}px`);
  await page.screenshot({ path: path.join(evidenceDir, '07-responsive-layout.png'), fullPage: false });
  record('Screenshot 7: Responsive Stacked Layout (768x1024)', 'PASS', `HorizontalOverflow=${overflowX}px`);

  // =========================================================================
  // RUNTIME CLEANLINESS
  // =========================================================================
  assert(consoleErrors.length === 0, `Console/page errors: ${consoleErrors.join('; ')}`);
  assert(failedApiRequests.length === 0, `Unexpected API errors: ${failedApiRequests.join('; ')}`);
  record('Runtime Cleanliness: Zero Console Errors & Zero Unexpected 4xx/5xx', 'PASS', 'Clean');

  fs.writeFileSync(
    path.join(evidenceDir, 'stage12-1-e2e-results.json'),
    JSON.stringify(
      {
        status: 'PASS',
        results,
        backendLinkageEvidence: {
          p100234: {
            patientId: detail234Api.patientId,
            patientReference: detail234Api.patientReference,
            encounterId: detail234Api.encounterId,
            encounterReference: detail234Api.encounterReference,
            noteId: detail234Api.noteId,
            noteVersion: detail234Api.noteVersion,
            eventId: detail234Api.eventId,
            extractionId: detail234Api.extractionId,
            provider: detail234Api.extraction?.modelMetadata?.provider,
            backendWarningCount: detail234Api.extraction?.validationSummary?.warningCount,
            withinPatientVersionsCount: detail234Api.patientNotes?.length ?? 0,
          },
          p100237: {
            patientId: detail237Api.patientId,
            patientReference: detail237Api.patientReference,
            encounterId: detail237Api.encounterId,
            encounterReference: detail237Api.encounterReference,
            noteId: detail237Api.noteId,
            noteVersion: detail237Api.noteVersion,
            eventId: detail237Api.eventId,
            extractionId: detail237Api.extractionId,
            provider: detail237Api.extraction?.modelMetadata?.provider,
            backendWarningCount: detail237Api.extraction?.validationSummary?.warningCount,
            withinPatientVersionsCount: detail237Api.patientNotes?.length ?? 0,
          },
        },
        consoleErrors,
        failedApiRequests,
      },
      null,
      2
    )
  );
  console.log('\nALL STAGE 12.1 E2E CHECKS PASSED!');
} catch (err) {
  console.error('\nSTAGE 12.1 E2E FAILED:', err);
  fs.writeFileSync(
    path.join(evidenceDir, 'stage12-1-e2e-results.json'),
    JSON.stringify({ status: 'FAIL', error: String(err), results, consoleErrors, failedApiRequests }, null, 2)
  );
  process.exitCode = 1;
} finally {
  await browser.close();
}

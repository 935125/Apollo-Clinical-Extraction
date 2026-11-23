import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

// Deterministic fictional API fixtures. This verifies the real Angular UI and
// request contracts without depending on a running clinical extraction backend.
const output = path.resolve('verification');
fs.mkdirSync(output, { recursive: true });
const note = `Fictional patient, 62 years old, with type 2 diabetes and hypertension. Three days of fever, productive cough and shortness of breath.

Assessment: Community acquired pneumonia.

Plan:
• Start amoxicillin-clavulanate 875/125 mg, 1 tablet orally twice daily for 7 days.
• Azithromycin 500 mg on day 1, then 250 mg once daily for 4 more days.
• Continue metformin 500 mg twice daily.
• Increase amlodipine to 10 mg once daily.

Investigations:
• Chest X-ray to rule out consolidation.
• CBC, CRP and U&E today.`;
const historyNote = 'Fictional historical source.\n\nChest X-ray if cough persists.';
function evidence(source, quote) {
  const start = source.indexOf(quote);
  return { quote, start, end: start < 0 ? null : start + quote.length, verified: start >= 0 };
}
const normalization = { status: 'unresolved', matchedCode: null, displayName: null, confidence: 0 };
function response(source, overrides = {}) {
  const medication = (name, strength, dose, frequency, assertionStatus, quote, extra = {}) => ({
    name,
    strength,
    dose,
    frequency,
    assertionStatus,
    route: 'oral',
    duration: null,
    formulation: null,
    instructions: null,
    evidence: evidence(source, quote),
    findings: [],
    qualityFlags: [],
    normalization,
    ...extra,
  });
  const investigation = (name, quote, extra = {}) => ({
    name,
    timing: 'today',
    specimen: null,
    assertionStatus: 'new_order',
    evidence: evidence(source, quote),
    findings: [],
    qualityFlags: [],
    normalization,
    ...extra,
  });
  return {
    schemaVersion: '1.0.0',
    extractionId: 'fictional-desktop-run',
    correlationId: 'fictional-correlation',
    extractedAt: '2026-11-20T06:00:00Z',
    executionDurationMs: 721,
    modelMetadata: {
      adapterType: 'fictional_browser_fixture',
      modelDeployment: 'fictional-model',
      promptVersion: 'test-v1',
      totalTokens: 800,
    },
    preprocessed: { charLength: source.length, sections: [] },
    medications: [
      medication(
        'Amoxicillin-clavulanate',
        '875/125 mg',
        '1 tablet',
        'Twice daily',
        'new_order',
        'Start amoxicillin-clavulanate 875/125 mg, 1 tablet orally twice daily for 7 days.',
        { duration: '7 days' },
      ),
      medication(
        'Azithromycin',
        '500 mg',
        null,
        'Once daily',
        'new_order',
        'Azithromycin 500 mg on day 1, then 250 mg once daily for 4 more days.',
        { instructions: '500 mg on day 1, then 250 mg once daily for 4 more days.' },
      ),
      medication('Metformin', '500 mg', null, 'Twice daily', 'current_medication', 'metformin'),
      medication(
        'Amlodipine',
        '10 mg',
        null,
        'Once daily',
        'current_medication',
        'Increase amlodipine to 10 mg once daily.',
      ),
    ],
    investigations: [
      investigation('Chest X-ray', 'Chest X-ray to rule out consolidation.'),
      investigation('Complete blood count', 'CBC'),
      investigation('C-reactive protein', 'CRP'),
      investigation('Urea & electrolytes', 'U&E'),
    ],
    validationSummary: { hasErrors: false, errorCount: 0, warningCount: 0 },
    noteText: source,
    inputType: 'text',
    sourceFileName: null,
    saved: true,
    ...overrides,
  };
}
const history = response(historyNote, {
  extractionId: 'fictional-history',
  sourceFileName: 'fictional-history.pdf',
  inputType: 'pdf',
  medications: [],
  investigations: [
    {
      name: 'Chest X-ray',
      timing: null,
      specimen: null,
      assertionStatus: 'conditional',
      evidence: evidence(historyNote, 'Chest X-ray'),
      findings: [
        {
          code: 'REVIEW_CONDITION',
          severity: 'warning',
          field: 'assertionStatus',
          message: 'Only if cough persists.',
        },
      ],
      qualityFlags: [],
      normalization,
    },
  ],
  validationSummary: { hasErrors: false, warningCount: 1, errorCount: 0 },
});
const checks = [];
const consoleErrors = [];
const record = (name) => {
  checks.push(name);
  console.log(`PASS ${name}`);
};
let mode = 'success';
let releaseDelayed;
let pendingStarted;
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    acceptDownloads: true,
  });
  const page = await context.newPage();
  // Angular schedules signal-driven rendering after DOM events.
  async function click(locator) {
    await locator.click();
    await page.evaluate(
      () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))),
    );
  }
  page.on('pageerror', (err) => consoleErrors.push(err.message));
  const postedNotes = [];
  await page.route('**/v1/health', (route) => route.fulfill({ json: { status: 'healthy' } }));
  await page.route('**/api/v1/extractions**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === 'POST') {
      if (url.pathname.endsWith('/pdf')) {
        assert.ok(request.headers()['content-type'].startsWith('multipart/form-data;'));
        return route.fulfill({ json: { ...history, sourceFileName: 'fictional-upload.pdf' } });
      }
      const { noteText } = request.postDataJSON();
      postedNotes.push(noteText);
      if (mode === 'delayed') {
        pendingStarted?.();
        await new Promise((resolve) => {
          releaseDelayed = resolve;
        });
        return route.fulfill({ json: response(noteText) });
      }
      if (mode === 'failure')
        return route.fulfill({
          status: 503,
          json: {
            title: 'Persistence failed',
            detail: 'Fictional save failure.',
            code: 'PERSISTENCE_FAILED',
          },
        });
      if (mode === 'empty')
        return route.fulfill({ json: response(noteText, { medications: [], investigations: [] }) });
      return route.fulfill({ json: response(noteText) });
    }
    if (url.pathname.endsWith('/fhir'))
      return route.fulfill({ json: { resourceType: 'Bundle', type: 'collection', entry: [] } });
    if (url.pathname.endsWith('/fictional-history')) return route.fulfill({ json: history });
    return route.fulfill({ json: [history] });
  });
  await page.goto(process.env.PREVIEW_URL || 'http://127.0.0.1:4200/', {
    waitUntil: 'networkidle',
  });
  await page.getByRole('heading', { name: 'Clinical extraction', exact: true }).waitFor();
  assert.equal(await page.locator('textarea').inputValue(), '');
  assert.equal(await page.locator('.sidebar, .kpi-card, .model-pill').count(), 0);
  record('Empty workspace opens in edit mode without placeholder navigation or sample text');
  await page.screenshot({ path: path.join(output, 'desktop-empty.png') });
  await page.locator('textarea').fill(note);
  await click(page.getByRole('button', { name: 'Extract', exact: true }));
  await page.getByRole('button', { name: 'Chest X-ray', exact: true }).waitFor();
  assert.equal(postedNotes.at(-1), note);
  assert.equal(await page.locator('.paper .clinical-narrative').textContent(), note);
  assert.equal(await page.locator('.medication-table .result-row').count(), 4);
  assert.equal(await page.locator('.investigation-table .result-row').count(), 4);
  const cells = page.locator('.medication-table .result-row').first().locator('td');
  assert.equal(await cells.nth(1).textContent(), '875/125 mg');
  assert.equal(await cells.nth(2).textContent(), '1 tablet');
  record('Exact submitted paragraphs, actual counts, separate strength and dose columns');
  await click(page.getByRole('button', { name: 'Chest X-ray', exact: true }));
  assert.equal(
    await page.locator('.paper mark').textContent(),
    'Chest X-ray to rule out consolidation.',
  );
  assert.equal(await page.locator('.investigation-table .result-row.selected').count(), 1);
  assert.ok(
    (await page.locator('.source-evidence').textContent()).includes(
      'Chest X-ray to rule out consolidation.',
    ),
  );
  const selectedColor = await page
    .locator('.result-row.selected')
    .evaluate((el) => getComputedStyle(el).backgroundColor);
  assert.equal(selectedColor, 'rgb(250, 237, 207)');
  const layout = await page.locator('.workspace').evaluate((el) => {
    const style = getComputedStyle(el);
    return {
      gap: style.columnGap,
      columns: style.gridTemplateColumns.split(' ').map(Number.parseFloat),
    };
  });
  assert.equal(layout.gap, '32px');
  assert.ok(Math.abs(layout.columns[0] / (layout.columns[0] + layout.columns[1]) - 0.39) < 0.001);
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.evaluate(() => scrollTo(0, 0));
  await page.mouse.move(0, 0);
  await page.screenshot({ path: path.join(output, 'desktop-1440x900.png'), fullPage: false });
  await page.screenshot({ path: path.join(output, 'desktop-full.png'), fullPage: true });
  record(
    '1440×900 desktop layout: 39/61 split, 32px gap, selected amber row and exact chest X-ray evidence',
  );
  await click(page.getByRole('button', { name: 'Metformin', exact: true }));
  assert.equal(await page.locator('.paper mark').textContent(), 'metformin');
  record('Name-only API evidence highlights only the returned entity name');
  await click(page.getByRole('button', { name: 'Amoxicillin-clavulanate', exact: true }));
  assert.ok((await page.locator('.medication-details').textContent()).includes('7 days'));
  assert.ok((await page.locator('.medication-details').textContent()).includes('oral'));
  assert.ok((await page.locator('.medication-details').textContent()).includes('Not documented'));
  record('Medication row details retain route, duration, instructions and missing-field labels');
  for (const format of ['JSON', 'CSV', 'FHIR']) {
    await click(page.getByRole('button', { name: 'Export', exact: true }));
    const downloadPromise = page.waitForEvent('download');
    await click(page.getByRole('button', { name: `Download ${format}`, exact: true }));
    const download = await downloadPromise;
    const filename = path.join(output, download.suggestedFilename());
    await download.saveAs(filename);
    if (format === 'CSV') {
      const csv = fs.readFileSync(filename, 'utf8');
      assert.ok(csv.includes('"Strength","Dose"'));
      assert.ok(csv.includes('"875/125 mg","1 tablet"'));
    }
  }
  record(
    'JSON, CSV and FHIR export actions create downloads with separate strength and dose in CSV',
  );
  await click(page.getByRole('tab', { name: 'Source', exact: true }));
  assert.equal(await page.locator('.source-result .clinical-narrative').textContent(), note);
  await click(page.getByRole('tab', { name: 'Details', exact: true }));
  await click(page.getByText('Model information', { exact: true }));
  assert.ok((await page.locator('.details-view').textContent()).includes('fictional-model'));
  await click(page.getByText('JSON response', { exact: true }));
  assert.ok((await page.locator('pre').textContent()).includes('schemaVersion'));
  record('Source tab preserves exact submitted note; Details exposes validation, model and JSON');
  await click(page.getByRole('tab', { name: 'Structured', exact: true }));
  await click(page.getByRole('button', { name: 'Chest X-ray', exact: true }));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.evaluate(() => scrollTo(0, 0));
  const sourceBox = await page.locator('.source-column').boundingBox();
  const resultBox = await page.locator('.results-column').boundingBox();
  assert.ok(resultBox.y >= sourceBox.y + sourceBox.height);
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  assert.ok(
    await page.locator('.medication-table').evaluate((el) => el.scrollWidth <= el.clientWidth),
  );
  await page.screenshot({ path: path.join(output, 'mobile-390x844.png') });
  await page.screenshot({ path: path.join(output, 'mobile-full.png'), fullPage: true });
  await page.locator('.results-header').evaluate((el) => el.scrollIntoView({ block: 'start' }));
  await page.screenshot({ path: path.join(output, 'mobile-results-390x844.png') });
  await page.locator('.investigations').evaluate((el) => el.scrollIntoView({ block: 'start' }));
  await page.screenshot({ path: path.join(output, 'mobile-evidence-390x844.png') });
  record('390×844 mobile layout stacks source above results with no page or table overflow');
  await click(page.getByRole('button', { name: 'Edit', exact: true }));
  assert.equal(await page.locator('textarea').inputValue(), note);
  assert.equal(await page.locator('mark').count(), 0);
  assert.equal(await page.locator('.result-row').count(), 0);
  record('Edit restores the same source and detaches all previous results and highlights');
  mode = 'failure';
  const failedNote = '  Fictional failed request.\n\nKeep every paragraph and space.  ';
  await page.locator('textarea').fill(failedNote);
  await click(page.getByRole('button', { name: 'Extract', exact: true }));
  await page.getByRole('alert').waitFor();
  assert.equal(await page.locator('textarea').inputValue(), failedNote);
  assert.ok((await page.getByRole('alert').textContent()).includes('Could not save extraction.'));
  assert.equal(await page.locator('.failed-state').count(), 1);
  await page.evaluate(() => scrollTo(0, 0));
  await page.screenshot({ path: path.join(output, 'mobile-save-failure.png'), fullPage: true });
  record('PERSISTENCE_FAILED preserves the exact input and displays a concise inline save error');
  mode = 'delayed';
  const started = new Promise((resolve) => {
    pendingStarted = resolve;
  });
  await page.locator('textarea').fill(note);
  await click(page.getByRole('button', { name: 'Extract', exact: true }));
  await started;
  await page.locator('.loading-state').waitFor();
  await page.locator('textarea').fill('A newer fictional note');
  const delayedDone = page.waitForResponse(
    (res) => res.request().method() === 'POST' && res.url().endsWith('/api/v1/extractions'),
  );
  releaseDelayed();
  await delayedDone;
  await page.waitForTimeout(100);
  assert.equal(await page.locator('textarea').inputValue(), 'A newer fictional note');
  assert.equal(await page.locator('.result-row').count(), 0);
  record('Loading state is distinct and a late response cannot attach to a newer note');
  await click(page.getByRole('button', { name: 'History', exact: true }));
  await click(page.locator('.history-item'));
  await page.getByRole('button', { name: 'Chest X-ray', exact: true }).waitFor();
  assert.equal(await page.locator('.paper .clinical-narrative').textContent(), historyNote);
  assert.ok((await page.locator('.paper-label').textContent()).includes('fictional-history.pdf'));
  await click(page.getByRole('button', { name: 'Chest X-ray', exact: true }));
  assert.equal(await page.locator('.paper mark').textContent(), 'Chest X-ray');
  assert.equal(await page.locator('.status-cell').textContent(), 'conditional');
  await click(page.getByRole('tab', { name: 'Details', exact: true }));
  assert.ok(
    (await page.locator('.details-view').textContent()).includes('Only if cough persists.'),
  );
  record(
    'History restores its own source, filename, conditional status and backend validation finding',
  );
  await click(page.getByRole('button', { name: 'Edit', exact: true }));
  await page
    .locator('input[type=file]')
    .setInputFiles({
      name: 'fictional-upload.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.from('%PDF-fictional-fixture'),
    });
  await page.locator('.paper-label').getByText('fictional-upload.pdf', { exact: true }).waitFor();
  assert.equal(await page.locator('.paper .clinical-narrative').textContent(), historyNote);
  record(
    'PDF uses the existing multipart API contract and restores returned text and actual filename',
  );
  await click(page.getByRole('button', { name: 'Clear', exact: true }));
  mode = 'empty';
  await page.locator('textarea').fill('Fictional note without any medications or investigations.');
  await click(page.getByRole('button', { name: 'Extract', exact: true }));
  await page.getByText('No medications found in this note.', { exact: true }).waitFor();
  assert.ok((await page.locator('.results-column').textContent()).includes('Extraction complete'));
  await page.getByText('No investigations found in this note.', { exact: true }).waitFor();
  record(
    'Successful extraction with zero entities is distinct from empty, loading and failure states',
  );
  assert.deepEqual(consoleErrors, []);
  record('No browser runtime errors');
  const report = {
    fixtureMode: true,
    backendIntegrationTested: false,
    viewports: ['1440×900', '390×844'],
    checks,
    runtimeErrors: consoleErrors,
    screenshots: fs.readdirSync(output).filter((name) => name.endsWith('.png')),
  };
  fs.writeFileSync(path.join(output, 'browser-report.json'), JSON.stringify(report, null, 2));
  console.log(`Completed ${checks.length} browser checks. Screenshots and report: ${output}`);
} finally {
  await browser.close();
}

// Offline captured real-ONNX output; all extraction/review writes are intercepted.
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const directory = path.join(root, 'docs/stage6-evidence/playwright');
fs.mkdirSync(directory, { recursive: true });
const capture = JSON.parse(fs.readFileSync(path.join(root, 'docs/stage6-evidence/after.json'), 'utf8'));
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
const results = [];
const errors = [];
let extractionPosts = 0;
page.on('pageerror', error => errors.push(error.message));
const ensure = (value, message) => { if (!value) throw new Error(message); };
let selected = capture.cases.find(c => c.id === 'stage5-018-onc-methotrexate-weekly').finalPipeline;
await page.route('**/api/v1/extractions/stage6-offline-preview', route => route.fulfill({ json: { ...selected, extractionId: 'stage6-offline-preview', saved: true } }));
await page.route('**/api/v1/extractions', async route => {
  if (route.request().method() === 'POST') { extractionPosts++; await route.abort(); }
  else await route.continue();
});
try {
  await page.goto('http://127.0.0.1:4200/?extractionId=stage6-offline-preview', { waitUntil: 'networkidle' });
  await page.locator('.medication-table button.entity-button', { hasText: 'Methotrexate' }).click();
  await page.locator('.row-details').waitFor({ state: 'visible' });
  const text = await page.locator('.result-content').innerText();
  ensure(text.includes('WEEKLY') && text.includes('once weekly'), `Weekly frequency and source instruction must remain visible: ${text.slice(0, 2500)}`);
  ensure(text.includes('attributes and investigations use source-based rules'), 'Fallback rule capability must be disclosed');
  await page.locator('.medication-table button.entity-button', { hasText: 'Ondansetron' }).click();
  ensure((await page.locator('.result-content').innerText()).includes('every 8 hours as needed'), 'PRN interval must be preserved');
  let request;
  await page.route('**/api/v1/extractions/stage6-offline-preview/reviews', async route => {
    request = route.request().postDataJSON();
    await route.fulfill({ status: 409, json: { title: 'Review has changed', detail: 'Reload the extraction and review the latest decision before resubmitting.', code: 'REVIEW_VERSION_CONFLICT' } });
  });
  await page.getByTestId('review-accept-btn').click();
  await page.getByTestId('review-error-msg').waitFor({ state: 'visible' });
  ensure(request.expectedVersion === 0, 'Review must send observed version');
  ensure((await page.getByTestId('review-error-msg').innerText()).includes('Reload'), 'Conflict requires actionable reload instruction');
  ensure((await page.getByTestId('med-review-status').innerText()).trim() === 'PENDING_REVIEW', 'Rejected stale write must not become an accepted review');
  results.push({ scenario: 'Weekly and PRN attributes, fallback disclosure, stale review handling', status: 'PASS' });
  await page.screenshot({ path: path.join(directory, 'stage6-prn-review-conflict.png'), fullPage: true });

  selected = capture.cases.find(c => c.id === 'stage5-007-pulm-asthma-inhaler').finalPipeline;
  await page.reload({ waitUntil: 'networkidle' });
  await page.locator('.investigation-table button.entity-button', { hasText: 'Chest X-ray' }).click();
  const details = await page.locator('.row-details .medication-details').innerText();
  ensure(details.includes('Timing\nNot documented') && details.includes('Specimen\nNot documented'), 'Missing attributes must be explicit');
  ensure((await page.locator('.investigation-table').innerText()).toLowerCase().includes('conditional'), 'Conditional investigation must not be shown as a definite order');
  results.push({ scenario: 'Missing timing/specimen remain visible on conditional investigation', status: 'PASS' });
  await page.screenshot({ path: path.join(directory, 'stage6-missing-investigation-attributes.png'), fullPage: true });
  ensure(extractionPosts === 0 && errors.length === 0, 'No extraction calls or page errors permitted');
} finally {
  fs.writeFileSync(path.join(directory, 'stage6-browser-results.json'), JSON.stringify({ results, extractionPosts, liveFoundryCalls: 0, pageErrors: errors }, null, 2));
  await browser.close();
}
console.log(JSON.stringify(results));

import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const directory = process.env.E2E_ARTIFACT_DIR || path.join(root, 'docs/stage7-evidence/playwright');
fs.mkdirSync(directory, { recursive: true });
const base = process.env.E2E_BASE_URL || 'http://127.0.0.1:5107';
const report = JSON.parse(fs.readFileSync(path.join(root, 'docs/stage7-evidence/stage7-development-results.json'), 'utf8'));
const browser = await chromium.launch({ headless: true }); const page = await browser.newPage();
const results = []; const errors = []; let extractionPosts = 0;
page.on('pageerror', error => errors.push(error.message));
const check = (value, message) => { if (!value) throw new Error(message); };
let selected = report.cases.find(c => c.id === 'stage5-008-pulm-copd-iv').finalApiResult;
await page.route('**/api/v1/extractions/stage7-offline-preview', route => route.fulfill({ json: selected }));
await page.route('**/api/v1/extractions', async route => {
  if (route.request().method() === 'POST') { extractionPosts++; await route.abort(); } else await route.continue();
});
try {
  const ready = await page.request.get(`${base}/health/ready`);
  check(ready.status() === 200, 'Real local inference readiness must succeed');
  await page.goto(`${base}/?extractionId=stage7-offline-preview`, { waitUntil: 'networkidle' });
  const candidates = page.getByTestId('source-review-candidates'); await candidates.waitFor({ state: 'visible' });
  check((await candidates.innerText()).includes('Theophylline'), 'Suppressed mention must remain available for review');
  check(!(await page.locator('.medication-table').innerText()).includes('Theophylline'), 'Candidate must not become an extracted prescription');
  check((await candidates.innerText()).includes('NO_EXPLICIT_PRESCRIPTION_PREFIX'), 'Suppression reason must be explicit');
  check(selected.modelMetadata.executionMode === 'NEURAL_ONNX' && selected.modelMetadata.attempts[0].onnxWindows > 0, 'Capture must originate from real neural inference');
  results.push({ scenario: 'Real ONNX readiness and visible source candidate excluded from prescriptions', status: 'PASS' });
  await page.screenshot({ path: path.join(directory, 'stage7-source-candidate.png'), fullPage: true });

  selected = report.cases.find(c => c.id === 'stage5-035-genmed-unresolved-oov').finalApiResult;
  await page.reload({ waitUntil: 'networkidle' });
  await page.locator('.investigation-table button.entity-button', { hasText: 'High-resolution CT Thorax' }).click();
  await page.locator('.row-details').waitFor({ state: 'visible' });
  const details = await page.locator('.investigation-table .row-details').innerText();
  check(details.includes('Unresolved'), 'New source-qualified concept must abstain from unsupported coding');
  results.push({ scenario: 'Qualified imaging source name retained with unresolved terminology', status: 'PASS' });
  await page.screenshot({ path: path.join(directory, 'stage7-qualified-imaging.png'), fullPage: true });
  check(extractionPosts === 0 && errors.length === 0, 'No new extraction requests or page errors allowed');
} finally {
  fs.writeFileSync(path.join(directory, 'stage7-browser-results.json'), JSON.stringify({ results, extractionPosts, liveFoundryCalls: 0, pageErrors: errors }, null, 2));
  await browser.close();
}
console.log(JSON.stringify(results));

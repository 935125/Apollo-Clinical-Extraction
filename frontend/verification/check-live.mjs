import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const runtimeErrors = [];
  page.on('pageerror', error => runtimeErrors.push(error.message));
  await page.goto('http://127.0.0.1:4200/', { waitUntil: 'networkidle' });
  assert.equal(await page.locator('.connection-note').count(), 0);
  const source = 'Fictional verification note.\n\nPlan: Start Metformin 500 mg PO twice daily. Order Chest X-ray.';
  await page.locator('textarea').fill(source);
  const extractionResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && response.url().endsWith('/api/v1/extractions'));
  await page.getByRole('button', { name: 'Extract', exact: true }).click();
  const response = await extractionResponse;
  assert.equal(response.status(), 200);
  const result = await response.json();
  assert.equal(result.saved, true);
  await page.getByRole('button', { name: 'Chest X-ray', exact: true }).waitFor();
  assert.equal(await page.locator('.paper .clinical-narrative').textContent(), source);
  const chest = result.investigations.find(entity => entity.name === 'Chest X-ray');
  assert.ok(chest);
  await page.getByRole('button', { name: 'Chest X-ray', exact: true }).click();
  await page.waitForFunction(quote => document.querySelector('.paper mark')?.textContent === quote, chest.evidence.quote);
  assert.equal(await page.locator('[role="alert"]').count(), 0);
  const savedResponse = await page.request.get(`http://127.0.0.1:4200/api/v1/extractions/${result.extractionId}`);
  assert.equal(savedResponse.status(), 200);
  const saved = await savedResponse.json();
  assert.equal(saved.noteText, source);
  assert.deepEqual(runtimeErrors, []);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: 'verification/live-connection-restored.png', fullPage: true });
  const report = {
    fixtureMode: false,
    backendIntegrationTested: true,
    httpStatus: response.status(),
    saved: result.saved,
    medicationCount: result.medications.length,
    investigationCount: result.investigations.length,
    exactSourceRestored: saved.noteText === source,
    selectedEvidence: chest.evidence.quote,
    runtimeErrors,
  };
  fs.writeFileSync('verification/live-report.json', JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
} finally {
  await browser.close();
}

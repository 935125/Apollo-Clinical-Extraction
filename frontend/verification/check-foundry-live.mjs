import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import fs from 'node:fs';

// No intercepted responses: Angular -> ASP.NET Core -> configured provider -> PostgreSQL.
const note = 'Continue metformin 500 mg tablets, two tablets by mouth twice daily for 7 days. Stop amlodipine. Order a CBC today. Consider a chest X-ray only if cough persists. Penicillin is listed as an allergy.';
const records = [];
const checks = [];
const errors = [];
const pass = name => { checks.push(name); console.log('PASS ' + name); };
function pdf(text) {
  const escaped = text.replaceAll('\\', '\\\\').replaceAll('(', '\\(').replaceAll(')', '\\)');
  const content = `BT /F1 10 Tf 32 720 Td (${escaped}) Tj ET`;
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>', '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1400 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>', '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', `<< /Length ${Buffer.byteLength(content)} >>\nstream\n${content}\nendstream`];
  let result = '%PDF-1.4\n'; const offsets = [0];
  objects.forEach((object, index) => { offsets.push(Buffer.byteLength(result)); result += `${index + 1} 0 obj\n${object}\nendobj\n`; });
  const xref = Buffer.byteLength(result);
  result += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  result += offsets.slice(1).map(offset => String(offset).padStart(10, '0') + ' 00000 n \n').join('');
  result += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`;
  return Buffer.from(result);
}
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('http://127.0.0.1:4200/', { waitUntil: 'networkidle' });
  assert.equal(await page.locator('.connection-note').count(), 0);
  pass('API is reachable through Angular');
  for (const input of ['text', 'pdf']) {
    const pending = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith(input === 'text' ? '/api/v1/extractions' : '/api/v1/extractions/pdf'));
    if (input === 'text') {
      await page.locator('textarea').fill(note);
      await page.getByRole('button', { name: 'Extract', exact: true }).click();
    } else {
      await page.locator('input[type=file]').setInputFiles({ name: 'synthetic-foundry-verification.pdf', mimeType: 'application/pdf', buffer: pdf(note) });
    }
    const httpResponse = await pending;
    assert.equal(httpResponse.status(), 200);
    const result = await httpResponse.json();
    assert.equal(result.saved, true);
    assert.equal(result.inputType, input);
    assert.equal(result.medications.length, 3);
    assert.equal(result.investigations.length, 2);
    const metadata = result.modelMetadata;
    assert.ok(['foundry', 'onnx'].includes(metadata.provider));
    assert.equal(metadata.inferenceExecuted, true);
    if (metadata.provider === 'onnx') {
      assert.ok(metadata.fallbackReason);
      assert.equal(metadata.attempts.filter(attempt => attempt.provider === 'onnx').length, 1);
      assert.ok(metadata.attempts.find(attempt => attempt.provider === 'onnx').onnxWindows > 0);
      assert.equal(metadata.completionTokens, null);
    } else {
      assert.equal(metadata.attempts.filter(attempt => attempt.provider === 'onnx').length, 0);
      assert.ok(metadata.model.startsWith('gpt-6.1-sol'));
    }
    const metformin = result.medications.find(entity => entity.name.toLowerCase() === 'metformin');
    assert.equal(metformin.strength, '500 mg');
    assert.ok(/^(?:two|2)(?: tablets)?$/i.test(metformin.dose));
    assert.ok(metformin.doseUnit.toLowerCase().includes('tablet'));
    assert.ok(metformin.duration.includes('7 days'));
    assert.equal(result.medications.filter(entity => entity.action === 'stop').length, 1);
    assert.equal(result.medications.find(entity => entity.action === 'stop').name.toLowerCase(), 'amlodipine');
    assert.equal(result.medications.find(entity => entity.name.toLowerCase() === 'penicillin').assertionStatus, 'allergy');
    assert.equal(result.investigations.find(entity => entity.name.toLowerCase() === 'cbc').assertionStatus, 'new_order');
    assert.equal(result.investigations.find(entity => entity.name.toLowerCase() === 'chest x-ray').assertionStatus, 'conditional');
    await page.getByRole('button', { name: /^metformin$/i }).waitFor();
    await page.waitForFunction(source => document.querySelector('.paper .clinical-narrative')?.textContent === source, result.noteText);
    for (const entity of [...result.medications, ...result.investigations]) {
      assert.equal(result.noteText.slice(entity.evidence.start, entity.evidence.end), entity.evidence.quote);
      await page.getByRole('button', { name: entity.name, exact: true }).click();
      await page.waitForFunction(quote => document.querySelector('.paper mark')?.textContent === quote, entity.evidence.quote);
    }
    const savedResponse = await page.request.get(`http://127.0.0.1:4200/api/v1/extractions/${result.extractionId}`);
    assert.equal(savedResponse.status(), 200);
    const saved = await savedResponse.json();
    assert.equal(saved.noteText, result.noteText);
    assert.deepEqual(saved.modelMetadata, metadata);
    assert.equal(saved.medications.length, 3);
    assert.equal(saved.investigations.length, 2);
    records.push({ inputType: input, extractionId: result.extractionId, modelMetadata: metadata, medicationCount: 3, investigationCount: 2, exactEvidenceVerified: true, sourceRestored: true });
    pass(`${input}: real persisted extraction, scoped dose/action/allergy, conditional investigation and every evidence highlight`);
    await page.getByRole('tab', { name: 'Details', exact: true }).click();
    await page.getByText('Model information', { exact: true }).click();
    await page.locator('.provider-attempt').first().waitFor();
    assert.equal(await page.locator('.provider-attempt').count(), metadata.attempts.length);
    if (metadata.provider === 'onnx') assert.ok((await page.locator('.provider-summary').textContent()).includes('local ONNX'));
    await page.evaluate(() => scrollTo(0, 0));
    await page.mouse.move(0, 0);
    await page.screenshot({ path: `verification/foundry-live-${input}-details.png`, fullPage: true });
    await page.getByRole('tab', { name: 'Structured', exact: true }).click();
  }
  await page.getByRole('button', { name: /^chest x-ray$/i }).click();
  await page.waitForFunction(() => !!document.querySelector('.investigation-table .selected'));
  await page.evaluate(() => scrollTo(0, 0));
  await page.screenshot({ path: 'verification/foundry-live-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: 'verification/foundry-live-mobile.png', fullPage: true });
  pass('Actual provider and attempt details render at desktop and mobile widths');
  await page.getByRole('button', { name: 'History', exact: true }).click();
  const historyItem = page.locator('.history-item').filter({ hasText: 'synthetic-foundry-verification.pdf' }).first();
  await historyItem.waitFor();
  assert.ok((await historyItem.textContent()).includes(records[1].modelMetadata.provider === 'onnx' ? 'local ONNX' : 'Microsoft Foundry'));
  await historyItem.click();
  await page.getByRole('button', { name: /^metformin$/i }).waitFor();
  assert.equal(await page.locator('.paper .clinical-narrative').textContent(), note);
  pass('History restores the saved provider and the PDF run source');
  assert.deepEqual(errors, []);
  pass('No browser runtime errors');
  const report = { fixtureMode: false, backendIntegrationTested: true, realFoundryVerified: records.some(record => record.modelMetadata.provider === 'foundry'), records, checks, runtimeErrors: errors };
  fs.writeFileSync('verification/foundry-live-report.json', JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ realFoundryVerified: report.realFoundryVerified, providers: records.map(record => ({ input: record.inputType, provider: record.modelMetadata.provider, reason: record.modelMetadata.fallbackReason, attempts: record.modelMetadata.attempts })), checks: checks.length }, null, 2));
} finally {
  await browser.close();
}

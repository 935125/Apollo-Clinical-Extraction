import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const directory = process.env.E2E_ARTIFACT_DIR || path.join(root, 'docs/stage8-evidence/playwright');
fs.mkdirSync(directory, { recursive: true });
const base = process.env.E2E_BASE_URL || 'http://127.0.0.1:5108';
const savedFinal = JSON.parse(
  fs.readFileSync(path.join(root, 'backend/artifacts/live-final-verification/foundry-final-extraction.json'), 'utf8'),
);

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
const results = [];
const errors = [];
let extractionPosts = 0;

page.on('pageerror', (error) => errors.push(error.message));
page.on('console', (msg) => {
  if (msg.type() === 'error') errors.push(msg.text());
});

const check = (value, message) => {
  if (!value) throw new Error(`STAGE8 E2E ASSERTION FAILED: ${message}`);
};

let mockedPayload = null;
await page.route('**/api/v1/extractions/stage8-routing-preview', (route) =>
  route.fulfill({ json: mockedPayload }),
);
await page.route('**/api/v1/extractions', async (route) => {
  if (route.request().method() === 'POST') {
    extractionPosts++;
    await route.abort();
  } else {
    await route.continue();
  }
});

try {
  // 1. Verify /v1/routing diagnostics endpoint on running backend
  const routingResp = await page.request.get(`${base}/v1/routing`);
  check(routingResp.status() === 200, `Expected /v1/routing 200 OK, got ${routingResp.status()}`);
  const routingJson = await routingResp.json();
  check(routingJson.extractionInferenceExecuted === false, '/v1/routing must never execute inference');
  check(routingJson.configurationReady === true, `/v1/routing must be ready on isolated fixture host: ${JSON.stringify(routingJson)}`);

  // 2. Verify Primary Microsoft Foundry GPT-6.1 Sol provenance banner & routing trace
  mockedPayload = {
    ...savedFinal,
    extractionId: 'stage8-routing-preview',
    saved: true,
    modelMetadata: {
      ...savedFinal.modelMetadata,
      provider: 'foundry',
      model: 'gpt-6.1-sol-2026-09-29',
      azureDeployment: 'gpt-6.1-sol',
      requestedPrimaryProvider: 'MicrosoftFoundry',
      finalExecutedProvider: 'MicrosoftFoundry',
      routingMode: 'primary',
      primaryInvocationStatus: 'accepted',
      fallbackTriggered: false,
      extractionStatus: 'completed_requires_review',
      validationResult: 'source_validated_requires_review',
    },
  };
  await page.goto(`${base}/?extractionId=stage8-routing-preview`, { waitUntil: 'networkidle' });
  const primaryLabel = await page.locator('.provider-summary').first().innerText();
  check(
    primaryLabel.includes('Microsoft Foundry — gpt-6.1-sol-2026-09-29 (GPT-6.1 Sol)'),
    `Unexpected primary Foundry label: ${primaryLabel}`,
  );
  check(!primaryLabel.includes('?'), `Mojibake question mark found in primary label: ${primaryLabel}`);
  const primaryTrace = await page.getByTestId('routing-provenance').innerText();
  check(
    primaryTrace.includes('Requested: MicrosoftFoundry · Executed: MicrosoftFoundry') &&
      primaryTrace.includes('source_validated_requires_review · Clinical review required'),
    `Unexpected primary routing trace: ${primaryTrace}`,
  );
  check(!primaryTrace.includes('?'), `Mojibake question mark found in routing trace: ${primaryTrace}`);
  await page.screenshot({ path: path.join(directory, 'stage8-primary-foundry-provenance.png'), fullPage: true });
  results.push({ scenario: 'Primary Microsoft Foundry GPT-6.1 Sol routing provenance & clean typography', status: 'PASS' });

  // 3. Verify Eligible Fallback provenance (MicrosoftFoundry -> LocalOnnx on AZURE_RATE_LIMITED)
  mockedPayload = {
    ...savedFinal,
    extractionId: 'stage8-routing-preview',
    saved: true,
    modelMetadata: {
      ...savedFinal.modelMetadata,
      provider: 'onnx',
      model: 'bc5cdr_int8',
      adapterType: 'onnx_neurosymbolic_int8',
      executionMode: 'NEURAL_ONNX',
      fallbackReason: 'AZURE_RATE_LIMITED',
      fallbackTriggered: true,
      routingMode: 'fallback',
      requestedPrimaryProvider: 'MicrosoftFoundry',
      finalExecutedProvider: 'LocalOnnx',
      primaryInvocationStatus: 'rejected',
      extractionStatus: 'completed_requires_review',
      validationResult: 'source_validated_requires_review',
    },
  };
  await page.reload({ waitUntil: 'networkidle' });
  const fallbackLabel = await page.locator('.provider-summary').first().innerText();
  check(
    fallbackLabel.includes('Local ONNX — Fallback after Microsoft Foundry failure · Microsoft Foundry rate limit'),
    `Unexpected fallback label: ${fallbackLabel}`,
  );
  const fallbackTrace = await page.getByTestId('routing-provenance').innerText();
  check(
    fallbackTrace.includes('Requested: MicrosoftFoundry · Executed: LocalOnnx'),
    `Unexpected fallback routing trace: ${fallbackTrace}`,
  );
  await page.screenshot({ path: path.join(directory, 'stage8-fallback-onnx-provenance.png'), fullPage: true });
  results.push({ scenario: 'Eligible transient fallback routing provenance (MicrosoftFoundry -> LocalOnnx)', status: 'PASS' });

  // 4. Verify Explicit Authorized Offline provenance (LocalOnnx without Foundry fallback claim)
  mockedPayload = {
    ...savedFinal,
    extractionId: 'stage8-routing-preview',
    saved: true,
    modelMetadata: {
      ...savedFinal.modelMetadata,
      provider: 'onnx',
      model: 'bc5cdr_int8',
      adapterType: 'onnx_neurosymbolic_int8',
      executionMode: 'NEURAL_ONNX',
      fallbackReason: null,
      fallbackTriggered: false,
      routingMode: 'explicit_offline',
      requestedPrimaryProvider: 'LocalOnnx',
      finalExecutedProvider: 'LocalOnnx',
      primaryInvocationStatus: 'not_attempted_offline',
      extractionStatus: 'completed_requires_review',
      validationResult: 'source_validated_requires_review',
    },
  };
  await page.reload({ waitUntil: 'networkidle' });
  const offlineLabel = await page.locator('.provider-summary').first().innerText();
  check(offlineLabel === 'Local ONNX — Offline Mode', `Unexpected offline label: ${offlineLabel}`);
  const offlineTrace = await page.getByTestId('routing-provenance').innerText();
  check(
    offlineTrace.includes('Requested: LocalOnnx · Executed: LocalOnnx'),
    `Unexpected offline routing trace: ${offlineTrace}`,
  );
  await page.screenshot({ path: path.join(directory, 'stage8-explicit-offline-provenance.png'), fullPage: true });
  results.push({ scenario: 'Explicit authorized offline routing provenance distinct from fallback', status: 'PASS' });

  check(extractionPosts === 0 && errors.length === 0, `Errors or unexpected POSTs: posts=${extractionPosts}, errors=${errors.join('; ')}`);
} finally {
  fs.writeFileSync(
    path.join(directory, 'stage8-browser-results.json'),
    JSON.stringify({ executedAt: new Date().toISOString(), results, extractionPosts, liveFoundryCalls: 0, pageErrors: errors }, null, 2),
  );
  await browser.close();
}
console.log(JSON.stringify(results));

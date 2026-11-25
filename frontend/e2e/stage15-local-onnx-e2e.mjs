import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const evidence = path.join(root, 'docs/stage15-evidence/playwright');
fs.mkdirSync(evidence, { recursive: true });
const base = 'http://127.0.0.1:5015';
const routing = await (await fetch(`${base}/v1/routing`)).json();
if (routing.requestedPrimaryProvider !== 'LocalOnnx' || routing.localPipelineVersion !== 'CandidateV2') {
  throw new Error('Stage 15 browser writes require the isolated CandidateV2 LocalOnnx instance on port 5015');
}
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ baseURL: base, viewport: { width: 1440, height: 1000 } });
const page = await context.newPage();
const errors = [];
const consoleMessages = [];
const failedResponses = [];
page.on('pageerror', e => errors.push(e.message));
page.on('console', m => consoleMessages.push(`${m.type()}: ${m.text()}`));
page.on('response', r => { if (r.status() >= 400) failedResponses.push(`${r.status()} ${r.url()}`); });
const checks = [];
function assert(value, message) { if (!value) throw new Error(message); checks.push(message); }
let persisted;
try {
  await page.goto('/', { waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 20000 });
  const get = async url => {
    const response = await page.request.get(url);
    if (!response.ok()) throw new Error(`GET ${url}: ${response.status()}`);
    return response.json();
  };
  const post = async (url, data) => {
    const csrf = (await get('/api/v1/emr/auth/csrf')).token;
    const response = await page.request.post(url, { data, headers: { 'X-XSRF-TOKEN': csrf } });
    if (!response.ok()) throw new Error(`POST ${url}: ${response.status()} ${await response.text()}`);
    return response.json();
  };
  const encounters = await get('/api/v1/emr/encounters');
  const encounter = encounters.find(e => e.patientReference === 'P100234');
  const fixtureLabel = `Stage 15 isolated browser fixture ${randomUUID()}`;
  const source = fixtureLabel + '\nAllergy History:\nAspirin (anaphylaxis).\nChemotherapy Regimen:\nCytarabine 100 mg/m2 continuous IV infusion (Days 1-7) + Daunorubicin 60 mg/m2 IV push (Days 1-3).\nDischarge Medications:\nAcyclovir 400 mg PO twice daily.\nFollow-Up Plan:\nReport on 01/12/2026 at 09:00 AM for CBC and renal function panel.';
  const draft = await post(`/api/v1/emr/encounters/${encounter.id}/notes`, { text: source });
  const signed = await post(`/api/v1/emr/notes/${draft.id}/sign`, { expectedVersion: draft.version });
  for (let attempt = 0; attempt < 25; attempt++) {
    persisted = await get(`/api/v1/queue/${signed.id}?version=${signed.version}&patient=P100234`);
    if (persisted.extraction) break;
    if (persisted.queueState === 'Failed') throw new Error(persisted.error);
    await page.waitForTimeout(500);
  }
  assert(persisted?.extraction, 'Asynchronous real ONNX extraction persisted in isolated PostgreSQL');
  const result = persisted.extraction;
  assert(result.modelMetadata.provider === 'onnx' && result.modelMetadata.localPipelineVersion === 'CandidateV2', 'Actual provider and pipeline version persisted');
  assert(result.medications.length === 4 && result.investigations.length === 2, 'Persisted entity counts match the synthetic source');
  assert(result.medications.find(m => m.name === 'Aspirin').assertionStatus === 'allergy', 'Allergy remains a non-active mention');
  assert(result.medications.find(m => m.name === 'Cytarabine').dose === '100 mg/m2', 'BSA-relative dose preserved without calculating an absolute dose');
  assert(result.investigations.every(i => i.provenance.clinicalContext.assertion === 'scheduled'), 'Follow-up tests preserve scheduled assertions');
  for (const entity of [...result.medications, ...result.investigations]) {
    const e = entity.evidence;
    assert(source.slice(e.start, e.end) === e.quote, `Exact persisted evidence for ${entity.name}`);
  }
  await page.reload({ waitUntil: 'networkidle' });
  await page.locator('.queue-row', { hasText: `${encounter.reference} · v${signed.version}` }).filter({ hasText: fixtureLabel }).locator('button:has-text("Open")').click();
  await page.waitForSelector('.medication-table tbody tr', { timeout: 15000 });
  assert(await page.locator('.medication-table tbody tr').count() === result.medications.length, 'Angular medication rows come from the selected persisted extraction');
  assert(await page.locator('.investigation-table tbody tr').count() === result.investigations.length, 'Angular investigation rows come from the selected persisted extraction');
  assert((await page.locator('.model-provenance-badge').innerText()).trim() === 'Local ONNX', 'Compact badge identifies the actual local provider');
  assert((await page.locator('.medication-table').innerText()).includes('100 mg/m2'), 'Angular displays the documented chemotherapy dose');
  assert((await page.locator('.investigation-table').innerText()).includes('Scheduled'), 'Angular displays persisted scheduling details');
  const body = await page.locator('body').innerText();
  assert(!body.includes('Validation warnings') && !body.includes('Doctor Dashboard'), 'Queue retains the existing compact clinical interface');
  assert(errors.length === 0, 'No browser runtime errors');
  await page.screenshot({ path: path.join(evidence, 'stage15-real-persisted-result.png'), fullPage: true });
  fs.writeFileSync(path.join(evidence, 'stage15-local-onnx-results.json'), JSON.stringify({ status: 'PASS', checks, errors,
    database: 'cx_audit_test', messaging: 'Simulated', liveFoundryCalls: 0, noteId: signed.id, version: signed.version,
    extractionId: persisted.extractionId, persisted: result }, null, 2));
  console.log(`PASS: ${checks.length} real ONNX/PostgreSQL/Angular checks; simulated messaging; zero Foundry calls.`);
} catch (error) {
  await page.screenshot({ path: path.join(evidence, 'stage15-debug-page.png'), fullPage: true });
  fs.writeFileSync(path.join(evidence, 'stage15-local-onnx-results.json'), JSON.stringify({ status: 'FAIL', checks, errors, consoleMessages, failedResponses, body: (await page.locator('body').innerText()).slice(0,2000), error: String(error) }, null, 2));
  console.error(error);
  process.exitCode = 1;
} finally { await browser.close(); }

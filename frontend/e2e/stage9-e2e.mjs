import { chromium } from 'playwright';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const outputDir = process.env.STAGE9_E2E_OUTPUT_DIR || path.join(root, 'docs/stage9-evidence/playwright');
const screenshotsDir = path.join(outputDir, 'screenshots');
fs.mkdirSync(screenshotsDir, { recursive: true });

const staticDir = path.join(root, 'frontend/dist/cx-review-ui/browser');
const mimeTypes = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.txt': 'text/plain; charset=utf-8'
};

const server = http.createServer((req, res) => {
  const urlPath = req.url.split('?')[0];
  let filePath = path.join(staticDir, urlPath);
  if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
    const candidate = path.join(filePath, 'index.html');
    if (fs.existsSync(candidate)) filePath = candidate;
    else filePath = path.join(staticDir, 'index.html');
  }
  const ext = path.extname(filePath).toLowerCase();
  const contentType = mimeTypes[ext] || 'application/octet-stream';
  res.writeHead(200, { 'Content-Type': contentType });
  fs.createReadStream(filePath).pipe(res);
});

await new Promise((resolve) => server.listen(5108, '127.0.0.1', resolve));

const base = process.env.E2E_BASE_URL || 'http://127.0.0.1:5108';
const results = [];
const errors = [];

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });

page.on('pageerror', (err) => errors.push(err.message));
page.on('console', (msg) => {
  if (msg.type() === 'error') errors.push(msg.text());
});

const check = (condition, message) => {
  if (!condition) throw new Error(`STAGE9 E2E ASSERTION FAILED: ${message}`);
};

try {
  // Mock EMR backend APIs for isolated browser verification
  let currentDoctor = null;
  let csrfToken = 'mock-csrf-token-12345';
  let mockEncounters = [
    {
      id: 'e100567-0000-0000-0000-000000000001',
      reference: 'ENC-100567',
      patientReference: 'P100234',
      encounterDate: '2026-01-28T11:24:00Z',
      context: 'General medicine · Follow-up consultation',
      noteId: 'n100874-0000-0000-0000-000000000001',
      version: 1,
      noteStatus: 'Signed',
      extractionStatus: 'Completed',
      extractionId: 'ext-86985b4aaa88'
    },
    {
      id: 'e100568-0000-0000-0000-000000000002',
      reference: 'ENC-100568',
      patientReference: 'P100235',
      encounterDate: '2026-01-28T10:15:00Z',
      context: 'General medicine · Outpatient consultation',
      noteId: 'n100875-0000-0000-0000-000000000002',
      version: 1,
      noteStatus: 'Draft',
      extractionStatus: null,
      extractionId: null
    },
    {
      id: 'e100569-0000-0000-0000-000000000003',
      reference: 'ENC-100569',
      patientReference: 'P100236',
      encounterDate: '2026-01-27T16:30:00Z',
      context: 'Internal medicine · Review consultation',
      noteId: 'n100876-0000-0000-0000-000000000003',
      version: 1,
      noteStatus: 'Signed',
      extractionStatus: 'Processing',
      extractionId: null
    },
    {
      id: 'e100570-0000-0000-0000-000000000004',
      reference: 'ENC-100570',
      patientReference: 'P100237',
      encounterDate: '2026-01-27T14:00:00Z',
      context: 'Cardiology · Routine consultation',
      noteId: null,
      version: null,
      noteStatus: null,
      extractionStatus: null,
      extractionId: null
    }
  ];

  let mockNote = {
    id: 'n100874-0000-0000-0000-000000000001',
    encounterId: 'e100567-0000-0000-0000-000000000001',
    patientReference: 'P100234',
    encounterReference: 'ENC-100567',
    encounterDate: '2026-01-28T11:24:00Z',
    context: 'General medicine · Follow-up consultation',
    text: 'Follow-up for hypertension.\nContinue amlodipine 5 mg orally once daily for 7 days.\nOrder complete blood count tomorrow.\nReview in 1 week.',
    version: 1,
    status: 'Draft',
    updatedAt: '2026-01-28T11:24:00Z',
    signedAt: null,
    extractionStatus: null,
    extractionId: null,
    error: null
  };

  const mockExtraction = {
    extractionId: 'ext-86985b4aaa88',
    correlationId: 'live-final-86985b4aaa88',
    saved: true,
    documentLabel: 'Outpatient Clinical Note (P100234)',
    noteText: 'Follow-up for hypertension.\nContinue amlodipine 5 mg orally once daily for 7 days.\nOrder complete blood count tomorrow.\nReview in 1 week.',
    medications: [
      {
        name: 'Amlodipine',
        status: 'continued',
        dose: '5 mg',
        route: 'oral',
        frequency: 'once daily',
        duration: '7 days',
        code: '17767',
        codeSystem: 'RxNorm',
        sourceEvidence: 'Continue amlodipine 5 mg orally once daily for 7 days.',
        evidenceSpans: [{ start: 30, end: 84, text: 'Continue amlodipine 5 mg orally once daily for 7 days.' }]
      }
    ],
    investigations: [
      {
        name: 'Complete Blood Count',
        status: 'ordered',
        timing: 'tomorrow',
        code: '58410-2',
        codeSystem: 'LOINC',
        sourceEvidence: 'Order complete blood count tomorrow.',
        evidenceSpans: [{ start: 85, end: 122, text: 'Order complete blood count tomorrow.' }]
      }
    ],
    modelMetadata: {
      provider: 'foundry',
      model: 'gpt-6.1-sol-2026-09-29',
      azureDeployment: 'gpt-6.1-sol',
      requestedPrimaryProvider: 'MicrosoftFoundry',
      finalExecutedProvider: 'MicrosoftFoundry',
      fallbackTriggered: false,
      routingMode: 'primary_foundry',
      promptTokens: 125,
      completionTokens: 85,
      totalTokens: 210,
      latencyMs: 1450,
      timestamp: '2026-01-28T11:25:00Z'
    },
    clinicalSafetyAlerts: [],
    validationSummary: {
      hasErrors: false,
      errorCount: 0,
      warningCount: 0,
      dimensions: []
    },
    entityReviews: []
  };

  await page.route('**/api/v1/emr/**', async (route) => {
    const url = route.request().url();
    const method = route.request().method();

    if (url.includes('/auth/csrf')) {
      return route.fulfill({ json: { token: csrfToken } });
    }
    if (url.includes('/auth/me')) {
      if (currentDoctor) return route.fulfill({ json: currentDoctor });
      return route.fulfill({ status: 401, json: { code: 'UNAUTHORIZED' } });
    }
    if (url.includes('/auth/login') && method === 'POST') {
      const body = route.request().postDataJSON();
      if (body.login && body.password) {
        currentDoctor = {
          id: '11111111-1111-1111-1111-111111111111',
          login: body.login,
          displayName: 'Dr. K. Krishnan',
          role: 'Doctor'
        };
        return route.fulfill({ json: currentDoctor });
      }
      return route.fulfill({ status: 401, json: { code: 'LOGIN_REJECTED' } });
    }
    if (url.includes('/auth/logout') && method === 'POST') {
      currentDoctor = null;
      return route.fulfill({ status: 204 });
    }
    if (url.includes('/encounters') && !url.includes('/notes')) {
      const idMatch = url.match(/\/encounters\/([0-9a-f-]+)$/i);
      if (idMatch) {
        const found = mockEncounters.find(e => e.id === idMatch[1]);
        return route.fulfill({ json: found || mockEncounters[0] });
      }
      return route.fulfill({ json: mockEncounters });
    }
    if (url.includes('/notes') && method === 'GET') {
      return route.fulfill({ json: mockNote });
    }
    if (url.includes('/notes') && method === 'PUT') {
      const body = route.request().postDataJSON();
      mockNote.text = body.text || mockNote.text;
      mockNote.version = (body.expectedVersion || 1) + 1;
      return route.fulfill({ json: mockNote });
    }
    if (url.includes('/sign') && method === 'POST') {
      mockNote.status = 'Signed';
      mockNote.signedAt = '2026-01-28T11:24:00Z';
      mockNote.version = mockNote.version + 1;
      mockNote.extractionStatus = 'Queued';
      return route.fulfill({ json: mockNote });
    }
    if (url.includes('/extraction') && method === 'GET') {
      return route.fulfill({ json: mockExtraction });
    }

    return route.continue();
  });

  await page.route('**/api/v1/extractions/**', async (route) => {
    return route.fulfill({ json: mockExtraction });
  });

  // SCREEN 1: Doctor Login
  console.log('Testing Screen 1: Doctor Login...');
  await page.goto(`${base}/login`);
  await page.waitForSelector('.identity-panel');
  await page.waitForSelector('.login-form');

  const h1Text = (await page.locator('h1').textContent() || '').replace(/\s+/g, ' ').trim();
  check(h1Text === 'Your clinical workspace.' || h1Text === 'Your clinicalworkspace.', 'Login left panel heading matches');
  check(await page.locator('.login-form h2').textContent() === 'Welcome back', 'Login form heading matches');

  await page.fill('#doctor-login', 'doctor.krishnan@apollo.local');
  await page.fill('#doctor-password', 'ValidDoctorPass123!');
  await page.screenshot({ path: path.join(screenshotsDir, '01-doctor-login.png'), fullPage: true });
  results.push({ screen: '1 - Doctor Login', status: 'PASS', path: '01-doctor-login.png' });

  // Click Sign in
  await page.click('button.sign-in');

  // SCREEN 2: Doctor Dashboard
  console.log('Testing Screen 2: Doctor Dashboard...');
  await page.waitForURL('**/emr');
  await page.waitForSelector('.dashboard-page');
  await page.waitForSelector('.clinical-table');

  check(await page.locator('h1').textContent() === 'Doctor Dashboard', 'Dashboard heading matches');
  check(await page.locator('.profile-name').textContent() === 'Dr. K. Krishnan', 'Doctor profile matches in header');
  check(await page.locator('.avatar').textContent() === 'DK', 'Doctor initials match');

  const rows = await page.locator('.clinical-table tbody tr').count();
  check(rows >= 3, 'Encounter table displays at least 3 assigned encounters');

  await page.screenshot({ path: path.join(screenshotsDir, '02-doctor-dashboard.png'), fullPage: true });
  results.push({ screen: '2 - Doctor Dashboard', status: 'PASS', path: '02-doctor-dashboard.png' });

  // SCREEN 3: Clinical Note Editor
  console.log('Testing Screen 3: Clinical Note Editor...');
  await page.goto(`${base}/emr/notes/${mockNote.id}/edit`);
  await page.waitForSelector('.note-editor-frame');

  check(await page.locator('h2#clinical-note-label').textContent() === 'Clinical Note', 'Editor heading matches');
  check(await page.locator('#clinical-note-text').count() === 1, 'Textarea is present');

  await page.fill('#clinical-note-text', 'Follow-up for hypertension.\nContinue amlodipine 5 mg orally once daily for 7 days.\nOrder complete blood count tomorrow.\nReview in 1 week.');

  // Save Draft
  await page.click('button:has-text("Save Draft")');
  await page.waitForSelector('.emr-alert.success');

  await page.screenshot({ path: path.join(screenshotsDir, '03-clinical-note-editor.png'), fullPage: true });
  results.push({ screen: '3 - Clinical Note Editor', status: 'PASS', path: '03-clinical-note-editor.png' });

  // Click Sign & Submit
  await page.click('button.sign-button');
  await page.waitForSelector('dialog[open]');
  await page.click('dialog[open] button.primary');

  // SCREEN 4: Extraction Status
  console.log('Testing Screen 4: Extraction Status...');
  await page.waitForURL('**/status');
  await page.waitForSelector('.status-progression');

  check(await page.locator('h1').textContent() === 'Clinical note submitted', 'Status heading matches');
  const steps = await page.locator('.progress-step').count();
  check(steps === 5, 'Status progression displays 5 distinct steps');

  await page.screenshot({ path: path.join(screenshotsDir, '04-extraction-status.png'), fullPage: true });
  results.push({ screen: '4 - Extraction Status', status: 'PASS', path: '04-extraction-status.png' });

  // SCREEN 5: Extraction Results & Review
  console.log('Testing Screen 5: Extraction Results & Review...');
  mockNote.extractionId = 'ext-86985b4aaa88';
  mockNote.extractionStatus = 'Completed';
  await page.goto(`${base}/extraction/result?extractionId=ext-86985b4aaa88`);
  await page.waitForSelector('.results-column');

  check(await page.locator('.provider-summary[role="status"]').textContent() === 'Microsoft Foundry — gpt-6.1-sol-2026-09-29 (GPT-6.1 Sol)', 'Model provenance matches');

  await page.screenshot({ path: path.join(screenshotsDir, '05-extraction-results.png'), fullPage: true });
  results.push({ screen: '5 - Extraction Results & Review', status: 'PASS', path: '05-extraction-results.png' });

  const summary = {
    executedAt: new Date().toISOString(),
    results,
    pageErrors: errors,
    screenshots: [
      '01-doctor-login.png',
      '02-doctor-dashboard.png',
      '03-clinical-note-editor.png',
      '04-extraction-status.png',
      '05-extraction-results.png'
    ]
  };

  fs.writeFileSync(path.join(outputDir, 'stage9-browser-results.json'), JSON.stringify(summary, null, 2), 'utf8');
  console.log('ALL_5_STAGE9_SCENARIOS_PASSED');
} finally {
  await browser.close();
  server.close();
}

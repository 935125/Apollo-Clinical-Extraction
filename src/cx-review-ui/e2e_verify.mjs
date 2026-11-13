import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

async function runBrowserVerification() {
  console.log('--- STARTING PLAYWRIGHT LIVE BROWSER VERIFICATION ---');

  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();

  const networkRequests = [];
  const consoleMessages = [];

  // 1. Intercept network traffic
  page.on('request', request => {
    const url = request.url();
    const method = request.method();
    const headers = request.headers();
    networkRequests.push({
      url,
      method,
      hasAuthHeader: Boolean(headers['authorization'] || headers['api-key']),
      targetPort: new URL(url).port || (url.startsWith('https') ? '443' : '80')
    });
  });

  // 2. Intercept console logs
  page.on('console', msg => {
    consoleMessages.push({
      type: msg.type(),
      text: msg.text()
    });
  });

  // 3. Navigate to Angular application
  console.log('Navigating to http://localhost:4200/ ...');
  await page.goto('http://localhost:4200/', { waitUntil: 'domcontentloaded', timeout: 30000 });

  // 4. Verify title and page header
  const title = await page.title();
  const headerText = await page.locator('.app-title').textContent();
  console.log(`Page title: "${title}", Header: "${headerText.trim()}"`);

  // 5. Enter fictional clinical note
  const sampleNote = `Patient: 62-year-old male with persistent cough and exertional dyspnea.
Assessment: Chronic Obstructive Pulmonary Disease with acute mild exacerbation.
Plan:
1. Prescribe Salbutamol 100mcg inhaler 2 puffs QDS PRN.
2. Prescribe Prednisolone 40mg PO daily x 5 days.
3. Patient allergic to Penicillin (anaphylaxis).
4. Order Chest X-Ray PA view and Pulmonary Function Test.`;

  const textarea = page.locator('textarea.clinical-textarea');
  await textarea.fill('');
  await textarea.fill(sampleNote);
  console.log('Filled clinical narrative in textarea.');

  // 6. Click Extract Clinical Information
  console.log('Clicking "Extract Clinical Information" button...');
  const extractButton = page.locator('button.btn-primary');
  await extractButton.click();

  // 7. Wait for results container to render
  console.log('Waiting for extraction response from ASP.NET Core -> Python -> Foundry gpt-4.1-mini...');
  await page.waitForSelector('.results-top-bar', { timeout: 35000 });
  console.log('Results top bar rendered!');

  // 8. Extract rendered telemetry and metadata
  const headerBadges = await page.locator('.header-badges').textContent();
  const metricsBar = await page.locator('.metrics-bar').textContent();

  // Extract medication cards
  const medCards = await page.locator('.entity-card').evaluateAll(cards => {
    return cards.map(c => {
      const name = c.querySelector('.entity-name')?.textContent?.trim() || '';
      const strength = c.querySelector('.entity-strength')?.textContent?.trim() || '';
      const badge = c.querySelector('.assertion-badge')?.textContent?.trim() || '';
      const code = c.querySelector('.norm-badge')?.textContent?.trim() || '';
      const quote = c.querySelector('.evidence-box blockquote')?.textContent?.trim() || '';
      return { name, strength, badge, code, quote };
    });
  });

  // Switch to Investigations Tab to view investigation cards
  console.log('Switching to Investigations tab...');
  const invTab = page.locator('button.tab-btn:has-text("Investigations")');
  await invTab.click();
  await page.waitForTimeout(500);

  const invCards = await page.locator('.entity-card').evaluateAll(cards => {
    return cards.map(c => {
      const name = c.querySelector('.entity-name')?.textContent?.trim() || '';
      const badge = c.querySelector('.assertion-badge')?.textContent?.trim() || '';
      const code = c.querySelector('.norm-badge')?.textContent?.trim() || '';
      const quote = c.querySelector('.evidence-box blockquote')?.textContent?.trim() || '';
      return { name, badge, code, quote };
    });
  });

  // Switch back to Medications Tab
  const medTab = page.locator('button.tab-btn:has-text("Medications")');
  await medTab.click();
  await page.waitForTimeout(500);

  // 9. Click on first medication card to verify bidirectional highlight
  console.log('Clicking first medication card to trigger evidence highlight...');
  const firstCard = page.locator('.entity-card').first();
  await firstCard.click();
  await page.waitForTimeout(500);

  // Switch to Evidence Highlight View in narrative panel
  const highlightToggle = page.locator('.chip-toggle:has-text("Evidence Highlight View")');
  await highlightToggle.click();
  await page.waitForTimeout(500);

  // Check highlighted mark text
  const highlightedSpans = await page.locator('mark.evidence-highlight').allTextContents();
  console.log('Highlighted evidence mark spans:', highlightedSpans);

  // 10. Capture screenshot
  const screenshotPath = 'docs/audit/browser_verification_live.png';
  fs.mkdirSync(path.dirname(screenshotPath), { recursive: true });
  await page.screenshot({ path: screenshotPath, fullPage: true });
  console.log(`Screenshot saved to ${screenshotPath}`);

  // 11. Analyze network security and architecture
  const apiRequests = networkRequests.filter(r => r.url.includes('/api/v1/extractions'));
  const pythonDirectCalls = networkRequests.filter(r => r.targetPort === '5005');
  const foundryDirectCalls = networkRequests.filter(r => r.url.includes('openai.azure.com') || r.url.includes('ai.azure.com'));
  const requestsWithAuth = networkRequests.filter(r => r.hasAuthHeader);

  const verificationResult = {
    timestamp: new Date().toISOString(),
    pageTitle: title,
    headerText: headerText.trim(),
    architecture: {
      browserOrigin: 'http://localhost:4200/',
      apiGatewayTarget: apiRequests.map(r => r.url),
      callsAspNetCoreDirectly: apiRequests.length > 0,
      callsPythonDirectly: pythonDirectCalls.length > 0,
      callsFoundryDirectly: foundryDirectCalls.length > 0,
      secretsInBrowserRequests: requestsWithAuth.length > 0
    },
    telemetry: {
      headerBadges: headerBadges.replace(/\s+/g, ' ').trim(),
      metricsBar: metricsBar.replace(/\s+/g, ' ').trim()
    },
    renderedMedications: medCards,
    renderedInvestigations: invCards,
    highlightVerification: {
      selectedHighlightedText: highlightedSpans
    },
    consoleLogsCount: consoleMessages.length
  };

  const jsonReportPath = 'docs/audit/browser_verification_result.json';
  fs.writeFileSync(jsonReportPath, JSON.stringify(verificationResult, null, 2), 'utf-8');
  console.log(`Report written to ${jsonReportPath}`);

  console.log('\n--- BROWSER VERIFICATION SUMMARY ---');
  console.log('Calls ASP.NET Core on port 5000:', verificationResult.architecture.callsAspNetCoreDirectly);
  console.log('Direct calls to Python (5005):', verificationResult.architecture.callsPythonDirectly);
  console.log('Direct calls to Foundry:', verificationResult.architecture.callsFoundryDirectly);
  console.log('Secrets in browser requests:', verificationResult.architecture.secretsInBrowserRequests);
  console.log('Header Badges:', verificationResult.telemetry.headerBadges);
  console.log('Metrics Bar:', verificationResult.telemetry.metricsBar);
  console.log('Medication Cards:', verificationResult.renderedMedications);
  console.log('Investigation Cards:', verificationResult.renderedInvestigations);
  console.log('Evidence Highlights:', verificationResult.highlightVerification.selectedHighlightedText);

  await browser.close();
  return verificationResult;
}

runBrowserVerification().catch(err => {
  console.error('Browser verification failed:', err);
  process.exit(1);
});

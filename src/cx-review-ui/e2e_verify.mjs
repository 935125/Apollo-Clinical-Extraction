import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

async function runBrowserVerification() {
  console.log('--- STARTING PLAYWRIGHT LIVE BROWSER VERIFICATION (APOLLO HOSPITALS STUDIO) ---');

  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--window-size=1600,900']
  });
  const context = await browser.newContext({
    viewport: { width: 1600, height: 900 }
  });
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
  await page.waitForTimeout(1000);

  // 4. Verify title and page header
  const title = await page.title();
  const headerText = await page.locator('.app-title').textContent();
  console.log(`Page title: "${title}", Header: "${headerText.trim()}"`);

  // 5. Check sidebar navigation
  const sidebarNavItems = await page.locator('.sidebar-nav .nav-item').allTextContents();
  console.log('Sidebar nav items:', sidebarNavItems.map(s => s.trim()));

  // 6. Enter clinical note
  const sampleNote = `62-year-old male with type 2 diabetes and hypertension presents with 3 days of fever, productive cough and shortness of breath.

Assessment: Community acquired pneumonia.

Plan:
• Start amoxicillin-clavulanate 875/125 mg orally twice daily for 7 days.
• Add azithromycin 500 mg on day 1, then 250 mg once daily for 4 more days.
• Continue metformin 500 mg twice daily.
• Increase amlodipine to 10 mg once daily.

Investigations:
• Chest X-ray to rule out consolidation.
• CBC, CRP and U&E today.`;

  const textarea = page.locator('textarea.clinical-textarea');
  await textarea.fill('');
  await textarea.fill(sampleNote);
  console.log('Filled clinical narrative in textarea.');

  // 7. Click Extract Medications & Investigations
  console.log('Clicking "Extract Medications & Investigations" button...');
  const extractButton = page.locator('button.btn-extract-main');
  const responsePromise = page.waitForResponse(resp => resp.url().includes('/api/v1/extractions') && resp.status() === 200, { timeout: 45000 });
  await extractButton.click();

  // 8. Wait for actual extraction response from ASP.NET Core -> Python -> Foundry gpt-4.1-mini
  console.log('Waiting for extraction response from ASP.NET Core -> Python -> Foundry gpt-4.1-mini...');
  const response = await responsePromise;
  const responseBody = await response.json();
  console.log(`Live response received! Total tokens: ${responseBody.modelMetadata?.totalTokens}, duration: ${responseBody.executionDurationMs}ms`);
  await page.waitForTimeout(1000);

  // 9. Inspect KPI cards
  const kpiCards = await page.locator('.kpi-card').evaluateAll(cards => {
    return cards.map(c => {
      const num = c.querySelector('.kpi-number')?.textContent?.trim() || '';
      const label = c.querySelector('.kpi-label')?.textContent?.trim() || '';
      return `${label}: ${num}`;
    });
  });
  console.log('Rendered KPI Metrics:', kpiCards);

  // 10. Extract rendered table rows for medications
  const medRows = await page.locator('.entity-section:has-text("Medications") tbody tr').evaluateAll(rows => {
    return rows.map(r => {
      const name = r.querySelector('.td-name')?.textContent?.trim() || '';
      const intent = r.querySelector('.intent-pill')?.textContent?.trim() || '';
      const evidence = r.querySelector('.td-evidence')?.textContent?.trim() || '';
      const match = r.querySelector('.match-exact, .match-unresolved')?.textContent?.trim() || '';
      return { name, intent, evidence, match };
    });
  });
  console.log(`Rendered ${medRows.length} Medications in table:`, medRows);

  // 11. Extract rendered table rows for investigations
  const invRows = await page.locator('.entity-section:has-text("Investigations") tbody tr').evaluateAll(rows => {
    return rows.map(r => {
      const name = r.querySelector('.td-name')?.textContent?.trim() || '';
      const intent = r.querySelector('.intent-pill')?.textContent?.trim() || '';
      const evidence = r.querySelector('.td-evidence')?.textContent?.trim() || '';
      const match = r.querySelector('.match-exact, .match-unresolved')?.textContent?.trim() || '';
      return { name, intent, evidence, match };
    });
  });
  console.log(`Rendered ${invRows.length} Investigations in table:`, invRows);

  // 12. Click on first medication row to inspect evidence highlight
  console.log('Clicking first medication row...');
  const firstRow = page.locator('.entity-section:has-text("Medications") tbody tr').first();
  await firstRow.click();
  await page.waitForTimeout(300);

  // 13. Switch to Highlighted Note tab
  console.log('Switching to "Highlighted Note" tab...');
  const highlightTab = page.locator('button.subtab-btn:has-text("Highlighted Note")');
  await highlightTab.click();
  await page.waitForTimeout(500);

  const highlightedSpans = await page.locator('mark.evidence-highlight').allTextContents();
  console.log('Highlighted evidence mark spans:', highlightedSpans);

  // Switch back to Structured Results tab
  const structuredTab = page.locator('button.subtab-btn:has-text("Structured Results")');
  await structuredTab.click();
  await page.waitForTimeout(500);

  // 14. Capture full page screenshot
  const screenshotPath = 'docs/audit/browser_verification_live.png';
  fs.mkdirSync(path.dirname(screenshotPath), { recursive: true });
  await page.screenshot({ path: screenshotPath, fullPage: true });
  console.log(`Screenshot saved to ${screenshotPath}`);

  // 15. Analyze network security and architecture
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
      kpiCards,
      modelBadge: await page.locator('.model-pill').textContent()
    },
    renderedMedications: medRows,
    renderedInvestigations: invRows,
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
  console.log('Model pill:', verificationResult.telemetry.modelBadge.replace(/\s+/g, ' ').trim());
  console.log('KPI Cards:', verificationResult.telemetry.kpiCards);
  console.log('Evidence Highlights:', verificationResult.highlightVerification.selectedHighlightedText);

  await browser.close();
  return verificationResult;
}

runBrowserVerification().catch(err => {
  console.error('Browser verification failed:', err);
  process.exit(1);
});

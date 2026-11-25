import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const evidenceDir = path.join(root, 'docs/stage13-evidence');
const screenshotsDir = path.join(evidenceDir, 'screenshots');
fs.mkdirSync(screenshotsDir, { recursive: true });

const SYNTHETIC_AML_NOTE = `HOSPITAL: Apollo Cancer Centre, Teynampet, Chennai
PATIENT NAME: Rajeshwari Sundaram | UHID: APL-CHN-8849201 | AGE/SEX: 48 / Female
ADMISSION DATE: 02/11/2026 | DISCHARGE DATE: 24/11/2026
ATTENDING ONCOLOGIST: Dr. Karthik Subramanian (Reg. No: TNMC-74912)
EMERGENCY CONTACT: Venkatraman Sundaram (Spouse) - +91-98401-55219

PRIMARY DIAGNOSIS:
Acute Myeloid Leukemia (AML) with NPM1 mutation and FLT3-ITD (low allelic ratio 0.22), ELN 2022 Favorable-to-Intermediate Risk.

HOSPITAL COURSE:
Mrs. Rajeshwari Sundaram was admitted for standard "7+3" induction chemotherapy plus FLT3 inhibition. Baseline bone marrow aspirate showed 68% myeloblasts (CD34+, CD117+, MPO+). Cytogenetics: 46,XX[20] (normal karyotype).
- Chemotherapy Administered: Cytarabine 100 mg/m2 continuous IV infusion (Days 1-7) + Daunorubicin 60 mg/m2 IV push (Days 1-3) + Midostaurin 50 mg PO twice daily with food (Days 8-21).
- Complications: Developed febrile neutropenia on Day 11 (ANC 40/uL, Tmax 39.1 C). Blood cultures grew ESBL-producing Escherichia coli; successfully treated with IV Meropenem 1 g q8h for 10 days. Experienced Grade 2 oral mucositis managed with benzydamine mouthwash and IV hydration.
- Count Recovery (Day 22): ANC recovered to 1,450/uL; unsupported Platelets 92,000/uL; Hemoglobin 9.6 g/dL. Day 21 bone marrow biopsy confirmed morphologic complete remission (CR) with 2% blasts.

DISCHARGE MEDICATIONS:
1. Posaconazole delayed-release tablets 300 mg PO once daily (antifungal prophylaxis).
2. Acyclovir 400 mg PO twice daily (antiviral prophylaxis).
3. Levofloxacin 500 mg PO once daily until ANC > 1,500/uL on two consecutive checks.
4. Ondansetron 8 mg PO SOS for nausea.

FOLLOW-UP INSTRUCTIONS:
Report to Day Care Oncology Wing on 01/12/2026 at 09:00 AM for CBC, renal/liver function panel, and Cycle 1 High-Dose Cytarabine (HiDAC) consolidation planning. Seek immediate ER care at 1066 if temperature >= 38.0 C, chills, or mucosal bleeding occurs.`;

function assert(cond, msg) {
  if (!cond) throw new Error(msg);
}

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const consoleErrors = [];
page.on('pageerror', (err) => consoleErrors.push(`pageerror: ${err.message}`));
page.on('console', (msg) => {
  if (msg.type() === 'error') consoleErrors.push(`console.error: ${msg.text()}`);
});

try {
  await page.goto('http://localhost:4200/', { waitUntil: 'networkidle' });
  await page.waitForSelector('.queue-table tbody tr', { timeout: 20000 });

  // Open Submit Test Note drawer in Angular
  await page.click('button:has-text("Submit Test Note")');
  await page.waitForSelector('.intake-drawer textarea', { timeout: 10000 });

  // Select P100234 synthetic encounter
  const encSelect = page.locator('.intake-drawer select');
  const p234Val = await encSelect.locator('option', { hasText: 'P100234' }).first().getAttribute('value');
  assert(p234Val, 'Synthetic encounter P100234 not found in Submit Test Note dropdown');
  await encSelect.selectOption(p234Val);

  // Fill realistic synthetic AML discharge note with fictional headers/names/UHID/phone
  await page.fill('.intake-drawer textarea', SYNTHETIC_AML_NOTE);
  await page.click('.intake-drawer button:has-text("Submit Note")');

  // Wait for success banner confirming submission
  await page.waitForSelector('.banner-ok', { timeout: 15000 });
  const bannerText = await page.locator('.banner-ok').innerText();
  const verMatch = bannerText.match(/v(\d+)/);
  assert(verMatch, `Could not parse submitted version from banner: ${bannerText}`);
  const submittedVer = verMatch[1];
  console.log(`[SYNTHETIC SUBMIT E2E] Submitted P100234 v${submittedVer}: ${bannerText}`);

  // Wait for newly submitted version row to appear and open it
  const targetRow = page.locator('.queue-row', { hasText: `ENC-100567 · v${submittedVer}` });
  await targetRow.waitFor({ state: 'visible', timeout: 15000 });
  await targetRow.locator('button:has-text("Open")').click();

  // Wait for Azure Service Bus -> EmrConsumer -> LocalOnnx extraction to complete (medication table renders)
  await page.waitForSelector('.medication-table tbody tr', { timeout: 30000 });
  const detailText = await page.locator('.workspace-detail').innerText();

  assert(!detailText.includes('SOURCE_REQUIRES_PRIVACY_REVIEW'), 'Authorized synthetic note was unexpectedly rejected by privacy gate!');
  assert(!detailText.includes('Extraction failed'), 'Authorized synthetic note failed extraction!');
  assert(detailText.includes('PATIENT NAME: Rajeshwari Sundaram | UHID: APL-CHN-8849201'), 'Original synthetic header was altered or missing in Angular source panel!');
  assert(detailText.includes('Dr. Karthik Subramanian') && detailText.includes('+91-98401-55219'), 'Fictional clinician/contact lines missing from Angular source panel!');
  assert(detailText.includes('Posaconazole') && detailText.includes('Ondansetron'), 'Expected medications (Posaconazole, Ondansetron) missing from extracted table!');
  assert(detailText.includes('CBC'), 'Expected investigation (CBC) missing from extracted table!');

  // Verify exact verbatim equality between saved source document and submitted synthetic note
  const displayedSource = (await page.locator('.source-document').innerText()).replace(/\r\n/g, '\n').trim();
  assert(displayedSource === SYNTHETIC_AML_NOTE.replace(/\r\n/g, '\n').trim(), 'Displayed Angular source text differed from saved synthetic note!');

  await page.screenshot({ path: path.join(screenshotsDir, '10-synthetic-aml-note-accepted.png'), fullPage: false });
  assert(consoleErrors.length === 0, `Console errors: ${consoleErrors.join('; ')}`);

  console.log(`[SYNTHETIC SUBMIT E2E] PASS: P100234 v${submittedVer} extracted via Azure Service Bus + LocalOnnx with exact original synthetic source preserved!`);
} finally {
  await browser.close();
}

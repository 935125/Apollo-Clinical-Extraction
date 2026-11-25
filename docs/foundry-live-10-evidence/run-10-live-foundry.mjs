import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const baseUrl = 'http://127.0.0.1:5000';
const outDir = path.dirname(fileURLToPath(import.meta.url));
fs.mkdirSync(outDir, { recursive: true });

// 10 diverse synthetic outpatient clinical notes (zero real PHI)
const CASES = [
  {
    id: 1,
    patientRef: 'P100234',
    title: 'Hypertension & Dyslipidaemia Follow-up',
    text: `Chief Complaint:
Follow-up for essential hypertension and dyslipidaemia.

Current Medications:
Continue Amlodipine 5 mg 1 tablet orally once daily for 30 days.
Continue Atorvastatin 10 mg 1 tablet orally at bedtime for 30 days.

Plan:
Start Telmisartan 40 mg half tablet orally once daily in the morning for 14 days.
Order Complete Blood Count automated and Serum Creatinine tomorrow morning.`,
  },
  {
    id: 2,
    patientRef: 'P100235',
    title: 'Community Acquired Pneumonia',
    text: `Chief Complaint:
4-day history of productive cough, fever, and mild dyspnoea.

Assessment:
Community acquired pneumonia, outpatient management.

Plan:
Start Amoxicillin-Clavulanate 625 mg 1 tablet orally three times daily for 7 days.
Add Azithromycin 500 mg 1 tablet orally once daily for 3 days.
Take Paracetamol 650 mg 1 tablet orally as needed for fever above 38.5 C.
Order Chest X-ray PA view today and Complete Blood Count tomorrow.`,
  },
  {
    id: 3,
    patientRef: 'P100237',
    title: 'Type 2 Diabetes Escalation & Aspirin Allergy',
    text: `Chief Complaint:
Routine diabetes follow-up with elevated fasting glucose.

Allergies:
Aspirin causes severe bronchospasm and facial swelling.

Current Medications:
Continue Metformin 500 mg 1 tablet orally twice daily with meals.

Plan:
Add Sitagliptin 100 mg 1 tablet orally once daily for 30 days.
Order HbA1c and Lipid Profile next week.
Order Serum Creatinine if systolic blood pressure exceeds 160 mmHg.`,
  },
  {
    id: 4,
    patientRef: 'P100234',
    title: 'Anticoagulation Safety & Discontinuation',
    text: `Chief Complaint:
Easy bruising and elevated home INR reading.

Medications:
Discontinue Warfarin 5 mg immediately due to prolonged prothrombin time.
Continue Bisoprolol 2.5 mg 1 tablet orally once daily.

Investigations:
Previous INR was 4.8 yesterday.
Order repeat Prothrombin Time and INR today.
Order Complete Blood Count automated today.`,
  },
  {
    id: 5,
    patientRef: 'P100235',
    title: 'Asthma Exacerbation & Inhaler Therapy',
    text: `Chief Complaint:
Nocturnal wheezing and chest tightness for 5 days.

Plan:
Start Budesonide-Formoterol 200/6 mcg 2 puffs inhaled twice daily for 30 days.
Use Salbutamol 100 mcg 2 puffs inhaled as needed for shortness of breath.
Start Montelukast 10 mg 1 tablet orally at night for 14 days.
Order Spirometry and Chest X-ray if symptoms persist after 1 week.`,
  },
  {
    id: 6,
    patientRef: 'P100237',
    title: 'Hypothyroidism & Iron Deficiency Anaemia',
    text: `Chief Complaint:
Fatigue, cold intolerance, and pallor for 6 weeks.

Current Medications:
Continue Levothyroxine 50 mcg 1 tablet orally once daily before breakfast.

Plan:
Start Ferrous Sulfate 200 mg 1 tablet orally once daily after lunch for 60 days.
Order Thyroid Stimulating Hormone (TSH), Serum Ferritin, and Complete Blood Count automated in 6 weeks.`,
  },
  {
    id: 7,
    patientRef: 'P100234',
    title: 'Acid Peptic Disease & Negated NSAID',
    text: `Chief Complaint:
Epigastric burning pain worse after spicy meals for 2 weeks.

Medications:
Ibuprofen was discussed but not prescribed due to gastritis risk.
Start Pantoprazole 40 mg 1 tablet orally once daily before breakfast for 14 days.
Take Sucralfate 1 g 10 mL orally three times daily before meals for 7 days.

Investigations:
Order Stool H. pylori antigen test and Liver Function Tests this week.`,
  },
  {
    id: 8,
    patientRef: 'P100235',
    title: 'Acute Uncomplicated Urinary Tract Infection',
    text: `Chief Complaint:
Burning micturition and urinary frequency for 2 days without flank pain.

Plan:
Start Nitrofurantoin 100 mg 1 capsule orally twice daily for 5 days.
Take Potassium Citrate 15 mL orally twice daily diluted in water for 5 days.
Order Urine Routine Examination and Urine Culture and Sensitivity prior to first antibiotic dose.`,
  },
  {
    id: 9,
    patientRef: 'P100237',
    title: 'Stable Angina & Cardiology Workup',
    text: `Chief Complaint:
Exertional chest tightness relieved by rest over the past 3 weeks.

Current Medications:
Continue Clopidogrel 75 mg 1 tablet orally once daily.
Continue Rosuvastatin 20 mg 1 tablet orally at night.

Plan:
Add Isosorbide Mononitrate 30 mg 1 tablet orally once daily in the morning for 30 days.
Order 12-lead ECG today, Echocardiogram, and Fasting Lipid Profile.`,
  },
  {
    id: 10,
    patientRef: 'P100234',
    title: 'Gout Flare & Historical Allopurinol',
    text: `Chief Complaint:
Acute painful swelling of the right first metatarsophalangeal joint since last night.

Past Medical History:
Patient took Allopurinol 100 mg daily in 2024, ceased last year.

Plan:
Start Colchicine 0.5 mg 1 tablet orally twice daily for 5 days.
Start Etoricoxib 90 mg 1 tablet orally once daily after food for 5 days.
Order Serum Uric Acid, Serum Creatinine, and C-Reactive Protein tomorrow.`,
  },
];

// Simple cookie jar for Node fetch
const cookies = new Map();
function saveCookies(resp) {
  const setCookies = typeof resp.headers.getSetCookie === 'function'
    ? resp.headers.getSetCookie()
    : (resp.headers.get('set-cookie') ? [resp.headers.get('set-cookie')] : []);
  for (const raw of setCookies) {
    const pair = raw.split(';')[0];
    const eq = pair.indexOf('=');
    if (eq > 0) cookies.set(pair.slice(0, eq).trim(), pair.slice(eq + 1).trim());
  }
}
function cookieHeader() {
  return Array.from(cookies.entries()).map(([k, v]) => `${k}=${v}`).join('; ');
}
async function api(method, urlPath, body = null, csrf = null) {
  const headers = {};
  const ck = cookieHeader();
  if (ck) headers['Cookie'] = ck;
  if (csrf) headers['X-XSRF-TOKEN'] = csrf;
  if (body !== null) headers['Content-Type'] = 'application/json';
  const resp = await fetch(`${baseUrl}${urlPath}`, {
    method,
    headers,
    body: body !== null ? JSON.stringify(body) : undefined,
  });
  saveCookies(resp);
  const text = await resp.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* non-json */ }
  return { status: resp.status, ok: resp.ok, json, text };
}

async function getCsrf() {
  const r = await api('GET', '/api/v1/emr/auth/csrf');
  if (!r.ok || !r.json?.token) throw new Error(`CSRF fetch failed: ${r.status}`);
  return r.json.token;
}

async function run() {
  // 1. Verify backend routing is genuinely MicrosoftFoundry with no silent fallback
  const routing = await api('GET', '/v1/routing');
  if (!routing.ok) throw new Error(`Routing check failed: ${routing.status}`);
  console.log('[FOUNDRY 10-RUN] Active Routing:', JSON.stringify(routing.json));
  if (routing.json.requestedPrimaryProvider !== 'MicrosoftFoundry') {
    throw new Error(`Expected requestedPrimaryProvider=MicrosoftFoundry, got ${routing.json.requestedPrimaryProvider}`);
  }
  if (!routing.json.configurationReady) {
    throw new Error(`Foundry configuration not ready: ${routing.json.configurationError}`);
  }

  // 2. Establish authorized loopback session
  const auth = await api('POST', '/api/v1/emr/auth/dev-session', {});
  if (!auth.ok) throw new Error(`Dev session failed: ${auth.status} ${auth.text}`);
  console.log(`[FOUNDRY 10-RUN] Authenticated as ${auth.json.displayName} (${auth.json.role})`);

  const encResp = await api('GET', '/api/v1/emr/encounters');
  if (!encResp.ok) throw new Error(`Encounters fetch failed: ${encResp.status}`);
  const encMap = new Map(encResp.json.map((e) => [e.patientReference, e]));

  const outFile = path.join(outDir, 'live-foundry-10-results.json');
  const prior = fs.existsSync(outFile) ? JSON.parse(fs.readFileSync(outFile, 'utf8')) : null;
  const records = prior?.cases ? [...prior.cases] : [];
  const completedIds = new Set(records.map((r) => r.caseId));
  let totalPromptTokens = records.reduce((s, r) => s + (r.promptTokens || 0), 0);
  let totalCompletionTokens = records.reduce((s, r) => s + (r.completionTokens || 0), 0);
  let totalTokensSum = records.reduce((s, r) => s + (r.totalTokens || 0), 0);
  let totalCostUsd = records.reduce((s, r) => s + (r.estimatedCostUsd || 0), 0);

  for (const c of CASES) {
    if (completedIds.has(c.id)) {
      console.log(`[CASE ${c.id}/10] Already executed in this 10-call batch (${records.find((r) => r.caseId === c.id)?.queueState}); skipping to preserve exact 10-call cap.`);
      continue;
    }
    const enc = encMap.get(c.patientRef);
    if (!enc) throw new Error(`Encounter not found for ${c.patientRef}`);

    // Fetch latest note version for this patient
    const noteResp = await api('GET', `/api/v1/emr/notes/${enc.noteId}`);
    if (!noteResp.ok) throw new Error(`Note fetch failed for ${c.patientRef}: ${noteResp.status}`);
    const currentNote = noteResp.json;

    const runTag = `FOUNDRY-LIVE-${String(c.id).padStart(2, '0')}`;
    const fullText = `${c.text}\n\n[Synthetic Case: ${runTag} — ${c.title}]`;

    const csrf1 = await getCsrf();
    let draftResp;
    if (currentNote.status === 'Draft') {
      draftResp = await api('PUT', `/api/v1/emr/notes/${currentNote.id}`, {
        text: fullText,
        expectedVersion: currentNote.version,
      }, csrf1);
    } else {
      draftResp = await api('POST', `/api/v1/emr/notes/${currentNote.id}/corrections`, {
        text: fullText,
        expectedVersion: currentNote.version,
      }, csrf1);
    }
    if (!draftResp.ok) throw new Error(`Draft create failed on case #${c.id}: ${draftResp.status} ${draftResp.text}`);
    const draft = draftResp.json;

    const csrf2 = await getCsrf();
    const submittedAt = Date.now();
    const signResp = await api('POST', `/api/v1/emr/notes/${draft.id}/sign`, {
      expectedVersion: draft.version,
    }, csrf2);
    if (!signResp.ok) throw new Error(`Sign failed on case #${c.id}: ${signResp.status} ${signResp.text}`);
    const signed = signResp.json;

    console.log(`\n[CASE ${c.id}/10] Signed ${c.patientRef} (${enc.reference} v${signed.version}) — "${c.title}" -> waiting for Azure Service Bus + GPT-6.1 Sol...`);

    // Poll bounded (max 90s for cloud LLM via Service Bus)
    let detail = null;
    for (let attempt = 1; attempt <= 45; attempt++) {
      await new Promise((r) => setTimeout(r, 2000));
      const q = await api('GET', `/api/v1/queue/${draft.id}?version=${signed.version}&patient=${c.patientRef}`);
      if (!q.ok) throw new Error(`Queue poll failed on case #${c.id}: ${q.status}`);
      detail = q.json;
      if (detail.queueState === 'Ready' || detail.queueState === 'Failed') break;
    }

    const wallMs = Date.now() - submittedAt;
    const ext = detail?.extraction;
    const meta = ext?.modelMetadata;

    const summary = {
      caseId: c.id,
      title: c.title,
      patientReference: c.patientRef,
      encounterReference: enc.reference,
      noteId: draft.id,
      noteVersion: signed.version,
      eventId: detail?.eventId ?? null,
      extractionId: detail?.extractionId ?? null,
      correlationId: ext?.correlationId ?? null,
      queueState: detail?.queueState ?? 'Timeout',
      error: detail?.error ?? null,
      provider: meta?.provider ?? null,
      model: meta?.model ?? meta?.modelDeployment ?? null,
      azureDeployment: meta?.azureDeployment ?? null,
      fallbackTriggered: meta?.fallbackTriggered ?? null,
      fallbackReason: meta?.fallbackReason ?? null,
      promptTokens: meta?.promptTokens ?? null,
      completionTokens: meta?.completionTokens ?? null,
      totalTokens: meta?.totalTokens ?? null,
      estimatedCostUsd: meta?.estimatedCostUsd ?? null,
      inferenceDurationMs: ext?.executionDurationMs ?? null,
      endToEndWallMs: wallMs,
      medicationCount: ext?.medications?.length ?? 0,
      medications: (ext?.medications ?? []).map((m) => ({
        name: m.name,
        strength: m.strength,
        dose: m.dose,
        route: m.route,
        frequency: m.frequency,
        duration: m.duration,
        assertionStatus: m.assertionStatus,
        rxnorm: m.normalization?.matchedCode ?? null,
      })),
      investigationCount: ext?.investigations?.length ?? 0,
      investigations: (ext?.investigations ?? []).map((i) => ({
        name: i.name,
        assertionStatus: i.assertionStatus,
        timing: i.timing,
        loinc: i.normalization?.matchedCode ?? null,
        candidateLoinc: i.normalization?.candidateCode ?? null,
      })),
      warningCount: ext?.validationSummary?.warningCount ?? null,
      errorCount: ext?.validationSummary?.errorCount ?? null,
    };

    records.push(summary);
    if (summary.promptTokens) totalPromptTokens += summary.promptTokens;
    if (summary.completionTokens) totalCompletionTokens += summary.completionTokens;
    if (summary.totalTokens) totalTokensSum += summary.totalTokens;
    if (summary.estimatedCostUsd) totalCostUsd += summary.estimatedCostUsd;

    console.log(
      `[CASE ${c.id}/10] ${summary.queueState} | provider=${summary.provider} | model=${summary.model} | tokens=${summary.totalTokens} | cost=$${summary.estimatedCostUsd?.toFixed?.(5) ?? 'N/A'} | inference=${Math.round(summary.inferenceDurationMs ?? 0)}ms | e2e=${wallMs}ms | meds=${summary.medicationCount} | invs=${summary.investigationCount}`
    );

    if (summary.queueState !== 'Ready' && summary.error && !['AZURE_INVALID_EVIDENCE', 'AZURE_TRUNCATED', 'AZURE_UNUSABLE_JSON'].includes(summary.error)) {
      console.error(`[CASE ${c.id}/10] Stopping early — provider/infrastructure error:`, summary.error);
      break;
    }
  }

  const report = {
    executedAtUtc: new Date().toISOString(),
    routing: routing.json,
    completedCount: records.filter((r) => r.queueState === 'Ready' && r.provider === 'foundry').length,
    attemptedCount: records.length,
    totals: {
      promptTokens: totalPromptTokens,
      completionTokens: totalCompletionTokens,
      totalTokens: totalTokensSum,
      estimatedCostUsd: Number(totalCostUsd.toFixed(6)),
    },
    cases: records,
  };

  fs.writeFileSync(outFile, JSON.stringify(report, null, 2));
  console.log(`\nSaved full 10-case live Foundry report to ${outFile}`);
  console.log(`TOTALS: attempted=${report.attemptedCount}/10, completedFoundry=${report.completedCount}/10, totalTokens=${totalTokensSum}`);
}

run().catch((err) => {
  console.error('LIVE FOUNDRY 10-RUN ERROR:', err);
  process.exitCode = 1;
});

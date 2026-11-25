import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ClinicalQueueComponent } from './clinical-queue';
import { QueueEntryDetail, QueueItem, QueueListResponse } from './queue-api';
import { ClinicalExtractionResponse } from '../models/extraction.model';

function makeExtraction(
  extractionId: string,
  noteText: string,
  medName: string,
  strength: string,
  dose: string | null,
  assertion: string,
  invName: string,
  loincCode: string | null,
  candidateCode: string | null = null
): ClinicalExtractionResponse {
  return {
    schemaVersion: '1.0.0',
    extractionId,
    correlationId: `corr-${extractionId}`,
    extractedAt: '2026-11-25T05:00:00Z',
    executionDurationMs: 120,
    modelMetadata: {
      provider: 'onnx',
      model: 'bc5cdr-int8',
      adapterType: 'onnx_bc5cdr',
      modelDeployment: 'bc5cdr-int8',
      promptVersion: 'v1',
      routingMode: 'explicit_offline',
      fallbackTriggered: false,
      fallbackReason: null,
    },
    preprocessed: { charLength: noteText.length, sections: [] },
    medications: [
      {
        name: medName,
        strength,
        dose,
        route: 'PO',
        frequency: 'OD',
        duration: '30 days',
        formulation: 'tablet',
        instructions: null,
        assertionStatus: assertion,
        evidence: { quote: noteText, verified: true, start: 0, end: noteText.length },
        qualityFlags: ['LOCAL_COVERAGE_REDUCED'],
        findings: [
          {
            code: 'LOCAL_COVERAGE_REDUCED',
            severity: 'warning',
            field: 'name',
            message: 'Backend validation remains active.',
          },
        ],
        normalization: {
          status: 'matched',
          matchedCode: '17767',
          displayName: medName,
          confidence: 1,
          semanticStatus: 'exact',
          terminologySource: 'RxNorm',
        },
      },
    ],
    investigations: [
      {
        name: invName,
        timing: 'tomorrow',
        specimen: null,
        assertionStatus: 'new_order',
        evidence: { quote: invName, verified: true, start: null, end: null },
        qualityFlags: ['TERMINOLOGY_UNMATCHED'],
        findings: [
          {
            code: 'TERMINOLOGY_UNMATCHED',
            severity: 'warning',
            field: 'name',
            message: 'Backend validation remains active.',
          },
        ],
        normalization: {
          status: loincCode ? 'matched' : 'uncertain',
          matchedCode: loincCode,
          displayName: invName,
          confidence: 0.9,
          semanticStatus: loincCode ? 'exact' : 'narrower',
          candidateCode,
          terminologySource: 'LOINC',
        },
      },
    ],
    validationSummary: { hasErrors: false, errorCount: 0, warningCount: 2 },
    safetyAlerts: [],
    noteText,
    inputType: 'text',
    saved: true,
  };
}

const THREE_PATIENTS_QUEUE: QueueItem[] = [
  {
    patientId: 'p-id-a',
    encounterId: 'enc-a',
    noteId: '11111111-1111-1111-1111-111111111111',
    noteVersion: 3,
    patientReference: 'P100234',
    encounterReference: 'ENC-100567',
    encounterDate: '2026-11-24T00:00:00Z',
    receivedAt: '2026-11-24T13:11:00Z',
    sourcePreview: 'Patient A note: Continue Amlodipine 5 mg orally once daily.',
    wordCount: 14,
    noteStatus: 'Signed',
    queueState: 'Ready',
    verificationRecommended: true,
    rawProcessingStatus: 'Needs Review',
    extractionId: 'ext-a-v3',
    provider: 'onnx',
    model: 'bc5cdr-int8',
    fallbackTriggered: false,
    warningCount: 2,
    errorCount: 0,
    error: null,
  },
  {
    patientId: 'p-id-b',
    encounterId: 'enc-b',
    noteId: '22222222-2222-2222-2222-222222222222',
    noteVersion: 5,
    patientReference: 'P100237',
    encounterReference: 'ENC-100570',
    encounterDate: '2026-11-24T00:00:00Z',
    receivedAt: '2026-11-24T13:05:00Z',
    sourcePreview: 'Patient B note: Start Metformin 500 mg half tablet twice daily.',
    wordCount: 16,
    noteStatus: 'Signed',
    queueState: 'Ready',
    verificationRecommended: true,
    rawProcessingStatus: 'Needs Review',
    extractionId: 'ext-b-v5',
    provider: 'onnx',
    model: 'bc5cdr-int8',
    fallbackTriggered: false,
    warningCount: 2,
    errorCount: 0,
    error: null,
  },
  {
    patientId: 'p-id-c',
    encounterId: 'enc-c',
    noteId: '33333333-3333-3333-3333-333333333333',
    noteVersion: 2,
    patientReference: 'P100235',
    encounterReference: 'ENC-100568',
    encounterDate: '2026-11-24T00:00:00Z',
    receivedAt: '2026-11-24T12:45:00Z',
    sourcePreview: 'Patient C note: Discontinue Warfarin 5 mg immediately.',
    wordCount: 12,
    noteStatus: 'Signed',
    queueState: 'Extracting',
    verificationRecommended: false,
    rawProcessingStatus: 'Processing',
    extractionId: null,
    provider: null,
    model: null,
    fallbackTriggered: false,
    warningCount: null,
    errorCount: null,
    error: null,
  },
];

const DETAIL_A: QueueEntryDetail = {
  patientId: 'p-id-a',
  noteId: THREE_PATIENTS_QUEUE[0].noteId,
  encounterId: 'enc-a',
  noteVersion: 3,
  patientReference: 'P100234',
  encounterReference: 'ENC-100567',
  encounterDate: '2026-11-24T00:00:00Z',
  context: 'General medicine',
  noteStatus: 'Signed',
  sourceText: 'Patient A source: Continue Amlodipine 5 mg 1 tablet orally once daily. Order Serum Electrolytes.',
  sourceHash: 'a'.repeat(64),
  signedAt: '2026-11-24T13:11:00Z',
  updatedAt: '2026-11-24T13:11:00Z',
  queueState: 'Ready',
  verificationRecommended: true,
  rawProcessingStatus: 'Needs Review',
  extractionId: 'ext-a-v3',
  error: null,
  extraction: makeExtraction(
    'ext-a-v3',
    'Patient A source: Continue Amlodipine 5 mg 1 tablet orally once daily. Order Serum Electrolytes.',
    'Amlodipine',
    '5 mg',
    '1 tablet',
    'continued',
    'Serum Electrolytes',
    '24326-1'
  ),
  patientNotes: [
    {
      noteId: THREE_PATIENTS_QUEUE[0].noteId,
      encounterId: 'enc-a',
      encounterReference: 'ENC-100567',
      noteVersion: 3,
      receivedAt: '2026-11-24T13:11:00Z',
      noteStatus: 'Signed',
      queueState: 'Ready',
      extractionId: 'ext-a-v3',
    },
    {
      noteId: THREE_PATIENTS_QUEUE[0].noteId,
      encounterId: 'enc-a',
      encounterReference: 'ENC-100567',
      noteVersion: 1,
      receivedAt: '2026-11-20T10:00:00Z',
      noteStatus: 'Signed',
      queueState: 'Ready',
      extractionId: 'ext-a-v1',
    },
  ],
};

const DETAIL_B: QueueEntryDetail = {
  patientId: 'p-id-b',
  noteId: THREE_PATIENTS_QUEUE[1].noteId,
  encounterId: 'enc-b',
  noteVersion: 5,
  patientReference: 'P100237',
  encounterReference: 'ENC-100570',
  encounterDate: '2026-11-24T00:00:00Z',
  context: 'Endocrinology',
  noteStatus: 'Signed',
  sourceText: 'Patient B source: Start Metformin 500 mg half tablet orally twice daily. Order Complete Blood Count.',
  sourceHash: 'b'.repeat(64),
  signedAt: '2026-11-24T13:05:00Z',
  updatedAt: '2026-11-24T13:05:00Z',
  queueState: 'Ready',
  verificationRecommended: true,
  rawProcessingStatus: 'Needs Review',
  extractionId: 'ext-b-v5',
  error: null,
  extraction: makeExtraction(
    'ext-b-v5',
    'Patient B source: Start Metformin 500 mg half tablet orally twice daily. Order Complete Blood Count.',
    'Metformin',
    '500 mg',
    'half tablet',
    'new_order',
    'Complete Blood Count',
    null,
    '58410-2'
  ),
  patientNotes: [
    {
      noteId: THREE_PATIENTS_QUEUE[1].noteId,
      encounterId: 'enc-b',
      encounterReference: 'ENC-100570',
      noteVersion: 5,
      receivedAt: '2026-11-24T13:05:00Z',
      noteStatus: 'Signed',
      queueState: 'Ready',
      extractionId: 'ext-b-v5',
    },
  ],
};

const DETAIL_C_PROCESSING: QueueEntryDetail = {
  patientId: 'p-id-c',
  noteId: THREE_PATIENTS_QUEUE[2].noteId,
  encounterId: 'enc-c',
  noteVersion: 2,
  patientReference: 'P100235',
  encounterReference: 'ENC-100568',
  encounterDate: '2026-11-24T00:00:00Z',
  context: 'Cardiology',
  noteStatus: 'Signed',
  sourceText: 'Patient C source: Discontinue Warfarin 5 mg immediately due to elevated INR.',
  sourceHash: 'c'.repeat(64),
  signedAt: '2026-11-24T12:45:00Z',
  updatedAt: '2026-11-24T12:45:00Z',
  queueState: 'Extracting',
  verificationRecommended: false,
  rawProcessingStatus: 'Processing',
  extractionId: null,
  error: null,
  extraction: null,
  patientNotes: [
    {
      noteId: THREE_PATIENTS_QUEUE[2].noteId,
      encounterId: 'enc-c',
      encounterReference: 'ENC-100568',
      noteVersion: 2,
      receivedAt: '2026-11-24T12:45:00Z',
      noteStatus: 'Signed',
      queueState: 'Extracting',
      extractionId: null,
    },
  ],
};

describe('Stage 12.1 UHID-Scoped Live Queue & Patient Isolation', () => {
  let httpMock: HttpTestingController;
  const activeFixtures: { destroy: () => void }[] = [];
  const flushMicrotasks = () => new Promise((resolve) => setTimeout(resolve, 0));

  beforeEach(async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [ClinicalQueueComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    while (activeFixtures.length) {
      activeFixtures.pop()?.destroy();
    }
    try {
      httpMock.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  async function bootstrapQueue(items = THREE_PATIENTS_QUEUE) {
    const fixture = TestBed.createComponent(ClinicalQueueComponent);
    activeFixtures.push(fixture);
    fixture.detectChanges();

    httpMock.expectOne('/api/v1/emr/auth/dev-session').flush({
      id: 'doc-1',
      login: 'doctor.krishnan@apollo.local',
      displayName: 'Dr. K. Krishnan',
      role: 'Doctor',
    });
    await flushMicrotasks();
    fixture.detectChanges();

    const resp: QueueListResponse = {
      items,
      total: items.length,
      serverTimeUtc: '2026-11-25T05:00:00Z',
      requestedPrimaryProvider: 'MicrosoftFoundry',
      fallbackProvider: 'LocalOnnx',
    };
    httpMock.expectOne((r) => r.url === '/api/v1/queue').flush(resp);
    fixture.detectChanges();

    return {
      fixture,
      comp: fixture.componentInstance,
      el: fixture.nativeElement as HTMLElement,
    };
  }

  it('Test A: Queue overview removes all filter boxes, status badges, ONNX labels, word counts, and legacy portal chrome', async () => {
    const { el } = await bootstrapQueue();
    const text = el.textContent ?? '';

    expect(text).toContain('P100234');
    expect(text).toContain('P100237');
    expect(text).toContain('P100235');

    // Removed queue controls & row clutter MUST NOT exist
    expect(el.querySelector('.filter-pills, .filter-pill, .queue-count')).toBeNull();
    expect(text).not.toContain('Ready · Verification recommended');
    expect(text).not.toContain('Local ONNX');
    expect(text).not.toContain('14 words');
    expect(text).not.toContain('16 words');
    expect(text).not.toContain('notes ·');
    expect(text).not.toContain('Doctor Dashboard');
    expect(text).not.toContain('Assigned Encounters');
    expect(text).not.toContain('Review & Validation');
    expect(text).not.toContain('Validation warnings');
    expect(text).not.toContain('Local ONNX limited-coverage mode');
    expect(el.querySelector('aside, .portal-sidebar, .portal-primary-nav')).toBeNull();
  });

  it('Test B: Selecting P100234 then P100237 then P100235 completely isolates each patient and cancels out-of-order delayed responses', async () => {
    const { fixture, comp, el } = await bootstrapQueue();

    // 1. Open Patient A (P100234)
    comp.openEntry(THREE_PATIENTS_QUEUE[0]);
    httpMock
      .expectOne(
        (r) =>
          r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[0].noteId}` &&
          r.params.get('patient') === 'P100234'
      )
      .flush(DETAIL_A);
    fixture.detectChanges();

    let text = el.textContent ?? '';
    expect(text).toContain('P100234');
    expect(text).toContain('Amlodipine');
    expect(text).toContain('Serum Electrolytes');
    // Unrelated patients MUST NOT appear anywhere inside P100234's detail workspace
    expect(text).not.toContain('P100237');
    expect(text).not.toContain('P100235');
    expect(text).not.toContain('Metformin');
    expect(el.querySelector('.quick-switch-strip')).toBeNull();
    // Validation Warnings panel and ONNX limited-coverage paragraph MUST NOT exist
    expect(text).not.toContain('LOCAL_COVERAGE_REDUCED');
    expect(text).not.toContain('TERMINOLOGY_UNMATCHED');
    expect(text).not.toContain('Clinical validation diagnostics');
    expect(text).not.toContain('Local ONNX limited-coverage mode');

    // 2. Rapidly alternate A -> B -> C while A and B requests remain in flight (switchMap must cancel superseded requests!)
    comp.openEntry(THREE_PATIENTS_QUEUE[0]);
    fixture.detectChanges();
    // Immediately cleared!
    expect(comp.selectedEntry()).toBeNull();
    expect(el.textContent).not.toContain('Amlodipine');

    const reqA = httpMock.expectOne(
      (r) => r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[0].noteId}`
    );

    // Switch immediately to Patient B BEFORE Patient A responds
    comp.openEntry(THREE_PATIENTS_QUEUE[1]);
    fixture.detectChanges();
    expect(reqA.cancelled).toBe(true); // switchMap cancelled Patient A's HTTP call!

    const reqB = httpMock.expectOne(
      (r) =>
        r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[1].noteId}` &&
        r.params.get('patient') === 'P100237'
    );
    reqB.flush(DETAIL_B);
    fixture.detectChanges();

    text = el.textContent ?? '';
    expect(text).toContain('P100237');
    expect(text).toContain('Metformin');
    expect(text).toContain('half tablet');
    expect(text).toContain('Candidate LOINC 58410-2 (withheld)');
    expect(text).not.toContain('P100234');
    expect(text).not.toContain('Amlodipine');
    expect(text).not.toContain('Serum Electrolytes');

    // 3. Switch to Patient C (in-progress extraction)
    comp.openEntry(THREE_PATIENTS_QUEUE[2]);
    fixture.detectChanges();
    expect(comp.selectedEntry()).toBeNull();

    httpMock
      .expectOne(
        (r) =>
          r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[2].noteId}` &&
          r.params.get('patient') === 'P100235'
      )
      .flush(DETAIL_C_PROCESSING);
    fixture.detectChanges();

    text = el.textContent ?? '';
    expect(text).toContain('P100235');
    expect(text).toContain('Extraction in progress');
    expect(text).not.toContain('P100237');
    expect(text).not.toContain('Metformin');
  });

  it('Test C: Within-patient version selector loads ONLY that patient and never mixes extraction versions', async () => {
    const { fixture, comp, el } = await bootstrapQueue();

    comp.openEntry(THREE_PATIENTS_QUEUE[0]);
    httpMock
      .expectOne((r) => r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[0].noteId}`)
      .flush(DETAIL_A);
    fixture.detectChanges();

    // Dropdown has ONLY P100234's 2 versions
    const options = Array.from(el.querySelectorAll('.within-patient-selector option')).map(
      (o) => o.textContent?.trim() ?? ''
    );
    expect(options.length).toBe(2);
    expect(options.every((o) => o.includes('ENC-100567'))).toBe(true);

    // Switch to Version 1 for P100234
    comp.onWithinPatientNoteChange(`${THREE_PATIENTS_QUEUE[0].noteId}:1`);
    fixture.detectChanges();
    expect(comp.selectedEntry()).toBeNull();

    const v1Detail: QueueEntryDetail = {
      ...DETAIL_A,
      noteVersion: 1,
      extractionId: 'ext-a-v1',
      sourceText: 'Patient A version 1 source: Take Paracetamol 650 mg orally as needed.',
      extraction: makeExtraction(
        'ext-a-v1',
        'Patient A version 1 source: Take Paracetamol 650 mg orally as needed.',
        'Paracetamol',
        '650 mg',
        '1 tablet',
        'new_order',
        'Chest X-ray',
        null
      ),
    };

    httpMock
      .expectOne(
        (r) =>
          r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[0].noteId}` &&
          r.params.get('version') === '1' &&
          r.params.get('patient') === 'P100234'
      )
      .flush(v1Detail);
    fixture.detectChanges();

    const text = el.textContent ?? '';
    expect(text).toContain('Paracetamol');
    expect(text).toContain('Chest X-ray');
    expect(text).not.toContain('Amlodipine');
    expect(text).not.toContain('Serum Electrolytes');
  });

  it('Stage 13: renders 10 individual submissions including repeated UHIDs and failed entries without collapsing by patient', async () => {
    const tenItems: QueueItem[] = Array.from({ length: 10 }, (_, idx) => {
      const ver = 27 - idx * 2;
      const isFailed = idx === 6;
      const patient = idx % 3 === 0 ? 'P100234' : idx % 3 === 1 ? 'P100237' : 'P100235';
      const enc = patient === 'P100234' ? 'ENC-100567' : patient === 'P100237' ? 'ENC-100570' : 'ENC-100568';
      const noteId = patient === 'P100234' ? THREE_PATIENTS_QUEUE[0].noteId : patient === 'P100237' ? THREE_PATIENTS_QUEUE[1].noteId : THREE_PATIENTS_QUEUE[2].noteId;
      return {
        ...THREE_PATIENTS_QUEUE[0],
        eventId: `evt-${idx + 1}`,
        noteId,
        noteVersion: ver,
        patientReference: patient,
        encounterReference: enc,
        sourcePreview: `Submission #${10 - idx} for ${patient} v${ver}`,
        queueState: isFailed ? 'Failed' : 'Ready',
        extractionId: isFailed ? null : `ext-${idx + 1}`,
        error: isFailed ? 'AZURE_INVALID_EVIDENCE' : null,
      };
    });

    const { el } = await bootstrapQueue(tenItems);
    const rows = el.querySelectorAll('.queue-table tbody tr');
    expect(rows.length).toBe(10);
    const text = el.textContent ?? '';
    expect(text).toContain('ENC-100567 · v27');
    expect(text).toContain('ENC-100567 · v21');
    expect(el.querySelectorAll('.failed-job-dot').length).toBe(1);
  });

  it('Show entries dropdown defaults to 10, switches cleanly between 5 and 10, preserves repeated UHIDs & newest-first order, and retains choice after opening an entry', async () => {
    const tenChronological: QueueItem[] = Array.from({ length: 10 }, (_, idx) => {
      const patient = idx % 3 === 0 ? 'P100234' : idx % 3 === 1 ? 'P100237' : 'P100235';
      const enc = patient === 'P100234' ? 'ENC-100567' : patient === 'P100237' ? 'ENC-100570' : 'ENC-100568';
      const noteId =
        patient === 'P100234'
          ? THREE_PATIENTS_QUEUE[0].noteId
          : patient === 'P100237'
            ? THREE_PATIENTS_QUEUE[1].noteId
            : THREE_PATIENTS_QUEUE[2].noteId;
      const version = 30 - idx;
      const minutes = String(50 - idx).padStart(2, '0');
      return {
        ...THREE_PATIENTS_QUEUE[0],
        eventId: `evt-page-${idx + 1}`,
        noteId,
        noteVersion: version,
        patientReference: patient,
        encounterReference: enc,
        receivedAt: `2026-11-25T11:${minutes}:00Z`,
        sourcePreview: `Chronological note #${10 - idx} (${patient} v${version})`,
        queueState: 'Ready',
        extractionId: `ext-page-${idx + 1}`,
      };
    });

    const fixture = TestBed.createComponent(ClinicalQueueComponent);
    activeFixtures.push(fixture);
    fixture.detectChanges();

    httpMock.expectOne('/api/v1/emr/auth/dev-session').flush({
      id: 'doc-1',
      login: 'doctor.krishnan@apollo.local',
      displayName: 'Dr. K. Krishnan',
      role: 'Doctor',
    });
    await flushMicrotasks();
    fixture.detectChanges();

    const initialReq = httpMock.expectOne(
      (r) => r.url === '/api/v1/queue' && r.params.get('limit') === '10' && !r.params.has('search')
    );
    initialReq.flush({
      items: tenChronological,
      total: 28,
      serverTimeUtc: '2026-11-25T06:00:00Z',
      requestedPrimaryProvider: 'LocalOnnx',
      fallbackProvider: 'LocalOnnx',
    } satisfies QueueListResponse);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const comp = fixture.componentInstance;

    const select = el.querySelector('select.entries-select') as HTMLSelectElement;
    expect(select).not.toBeNull();
    expect(el.querySelector('.entries-label')?.textContent?.trim()).toBe('Show entries');
    expect(Array.from(select.options).map((o) => o.value)).toEqual(['5', '10']);
    expect(select.value).toBe('10');
    expect(el.querySelectorAll('.queue-table tbody tr').length).toBe(10);

    // Switch to 5 via DOM event -> immediately calls GET /api/v1/queue?limit=5
    select.value = '5';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const req5 = httpMock.expectOne(
      (r) => r.url === '/api/v1/queue' && r.params.get('limit') === '5' && !r.params.has('search')
    );
    req5.flush({
      items: tenChronological.slice(0, 5),
      total: 28,
      serverTimeUtc: '2026-11-25T06:00:05Z',
      requestedPrimaryProvider: 'LocalOnnx',
      fallbackProvider: 'LocalOnnx',
    } satisfies QueueListResponse);
    fixture.detectChanges();

    const fiveRows = Array.from(el.querySelectorAll('.queue-table tbody tr')).map(
      (r) => r.textContent ?? ''
    );
    expect(fiveRows.length).toBe(5);
    // Repeated UHID P100234 (v30 and v27) appear separately in newest-first order
    expect(fiveRows[0]).toContain('P100234');
    expect(fiveRows[0]).toContain('v30');
    expect(fiveRows[3]).toContain('P100234');
    expect(fiveRows[3]).toContain('v27');

    // Open an entry, verify detail isolation, then return to queue -> must keep Show entries = 5
    comp.openEntry(tenChronological[0]);
    httpMock
      .expectOne(
        (r) =>
          r.url === `/api/v1/queue/${tenChronological[0].noteId}` &&
          r.params.get('version') === '30' &&
          r.params.get('patient') === 'P100234'
      )
      .flush({ ...DETAIL_A, noteVersion: 30 });
    fixture.detectChanges();
    expect(el.textContent).toContain('Amlodipine');

    comp.backToQueue();
    fixture.detectChanges();

    const backReq = httpMock.expectOne(
      (r) => r.url === '/api/v1/queue' && r.params.get('limit') === '5'
    );
    backReq.flush({
      items: tenChronological.slice(0, 5),
      total: 28,
      serverTimeUtc: '2026-11-25T06:00:10Z',
      requestedPrimaryProvider: 'LocalOnnx',
      fallbackProvider: 'LocalOnnx',
    } satisfies QueueListResponse);
    fixture.detectChanges();

    const restoredSelect = el.querySelector('select.entries-select') as HTMLSelectElement;
    expect(restoredSelect.value).toBe('5');
    expect(el.querySelectorAll('.queue-table tbody tr').length).toBe(5);

    // Switch back to 10 -> immediately calls GET /api/v1/queue?limit=10
    restoredSelect.value = '10';
    restoredSelect.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const req10 = httpMock.expectOne(
      (r) => r.url === '/api/v1/queue' && r.params.get('limit') === '10'
    );
    req10.flush({
      items: tenChronological,
      total: 28,
      serverTimeUtc: '2026-11-25T06:00:15Z',
      requestedPrimaryProvider: 'LocalOnnx',
      fallbackProvider: 'LocalOnnx',
    } satisfies QueueListResponse);
    fixture.detectChanges();

    expect(el.querySelectorAll('.queue-table tbody tr').length).toBe(10);
  });

  it('Automatic polling preserves chosen page size (5) and search forwards both search & limit to API', async () => {
    vi.useFakeTimers();
    try {
      const fixture = TestBed.createComponent(ClinicalQueueComponent);
      activeFixtures.push(fixture);
      fixture.detectChanges();

      httpMock.expectOne('/api/v1/emr/auth/dev-session').flush({
        id: 'doc-1',
        login: 'doctor.krishnan@apollo.local',
        displayName: 'Dr. K. Krishnan',
        role: 'Doctor',
      });
      await Promise.resolve();
      await Promise.resolve();
      fixture.detectChanges();

      httpMock
        .expectOne((r) => r.url === '/api/v1/queue' && r.params.get('limit') === '10')
        .flush({
          items: THREE_PATIENTS_QUEUE,
          total: 3,
          serverTimeUtc: '2026-11-25T06:00:00Z',
          requestedPrimaryProvider: 'LocalOnnx',
          fallbackProvider: 'LocalOnnx',
        } satisfies QueueListResponse);
      fixture.detectChanges();

      const comp = fixture.componentInstance;
      comp.onPageSizeChange(5);
      httpMock
        .expectOne((r) => r.url === '/api/v1/queue' && r.params.get('limit') === '5')
        .flush({
          items: THREE_PATIENTS_QUEUE.slice(0, 2),
          total: 3,
          serverTimeUtc: '2026-11-25T06:00:01Z',
          requestedPrimaryProvider: 'LocalOnnx',
          fallbackProvider: 'LocalOnnx',
        } satisfies QueueListResponse);
      fixture.detectChanges();

      // Advance 4000ms -> automatic poll must still request limit=5
      vi.advanceTimersByTime(4000);
      const pollReq = httpMock.expectOne(
        (r) => r.url === '/api/v1/queue' && r.params.get('limit') === '5' && !r.params.has('search')
      );
      pollReq.flush({
        items: THREE_PATIENTS_QUEUE.slice(0, 2),
        total: 3,
        serverTimeUtc: '2026-11-25T06:00:05Z',
        requestedPrimaryProvider: 'LocalOnnx',
        fallbackProvider: 'LocalOnnx',
      } satisfies QueueListResponse);
      fixture.detectChanges();
      expect(comp.pageSize()).toBe(5);

      // Search with page size 5 -> forwards search=P100234 & limit=5 after 250ms debounce
      comp.onSearchChange('P100234');
      vi.advanceTimersByTime(260);
      const searchReq = httpMock.expectOne(
        (r) =>
          r.url === '/api/v1/queue' &&
          r.params.get('limit') === '5' &&
          r.params.get('search') === 'P100234'
      );
      searchReq.flush({
        items: [THREE_PATIENTS_QUEUE[0]],
        total: 1,
        serverTimeUtc: '2026-11-25T06:00:06Z',
        requestedPrimaryProvider: 'LocalOnnx',
        fallbackProvider: 'LocalOnnx',
      } satisfies QueueListResponse);
      fixture.detectChanges();

      // Subsequent automatic poll preserves BOTH search=P100234 and limit=5
      vi.advanceTimersByTime(4000);
      const pollWithSearch = httpMock.expectOne(
        (r) =>
          r.url === '/api/v1/queue' &&
          r.params.get('limit') === '5' &&
          r.params.get('search') === 'P100234'
      );
      pollWithSearch.flush({
        items: [THREE_PATIENTS_QUEUE[0]],
        total: 1,
        serverTimeUtc: '2026-11-25T06:00:10Z',
        requestedPrimaryProvider: 'LocalOnnx',
        fallbackProvider: 'LocalOnnx',
      } satisfies QueueListResponse);
      fixture.detectChanges();
      expect(comp.pageSize()).toBe(5);
      expect(comp.visibleQueueItems().length).toBe(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('Model provenance indicator accurately distinguishes GPT Foundry, Local ONNX primary, Local ONNX fallback, missing provenance, pending, failed, and switches cleanly across versions & UHIDs', async () => {
    const { fixture, comp, el } = await bootstrapQueue();

    // 1. GPT-6.1 Sol · Microsoft Foundry completed extraction
    const gptDetail: QueueEntryDetail = {
      ...DETAIL_A,
      noteVersion: 27,
      extractionId: 'ext-gpt-v27',
      extraction: {
        ...DETAIL_A.extraction!,
        extractionId: 'ext-gpt-v27',
        modelMetadata: {
          provider: 'foundry',
          finalExecutedProvider: 'MicrosoftFoundry',
          requestedPrimaryProvider: 'MicrosoftFoundry',
          adapterType: 'microsoft_foundry',
          model: 'gpt-6.1-sol-2026-09-29',
          modelDeployment: 'gpt-6.1-sol',
          azureDeployment: 'gpt-6.1-sol',
          promptVersion: 'clinical-v3',
          routingMode: 'primary',
          fallbackTriggered: false,
          fallbackReason: null,
        },
      },
    };

    comp.openEntry({ ...THREE_PATIENTS_QUEUE[0], noteVersion: 27, extractionId: 'ext-gpt-v27' });
    httpMock
      .expectOne(
        (r) =>
          r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[0].noteId}` &&
          r.params.get('version') === '27'
      )
      .flush(gptDetail);
    fixture.detectChanges();

    let badge = el.querySelector('.model-provenance-badge');
    expect(badge?.textContent?.trim()).toBe('✦ GPT-6.1 Sol · Microsoft Foundry');
    expect(badge?.getAttribute('data-model-provenance')).toBe('foundry');

    // 2. Switch version on same UHID -> Local ONNX primary (explicit_offline, fallbackTriggered=false)
    const onnxPrimaryDetail: QueueEntryDetail = {
      ...DETAIL_A,
      noteVersion: 33,
      extractionId: 'ext-onnx-v33',
      extraction: {
        ...DETAIL_A.extraction!,
        extractionId: 'ext-onnx-v33',
        modelMetadata: {
          provider: 'onnx',
          finalExecutedProvider: 'LocalOnnx',
          requestedPrimaryProvider: 'LocalOnnx',
          adapterType: 'onnx_neurosymbolic_int8',
          model: 'bc5cdr_int8',
          modelDeployment: 'bc5cdr_int8',
          promptVersion: 'v2.1.0',
          routingMode: 'explicit_offline',
          fallbackTriggered: false,
          fallbackReason: null,
        },
      },
    };

    comp.openEntry({ ...THREE_PATIENTS_QUEUE[0], noteVersion: 33, extractionId: 'ext-onnx-v33' });
    fixture.detectChanges();
    expect(el.querySelector('.model-provenance-badge')).toBeNull(); // Cleared immediately on switch!

    httpMock
      .expectOne(
        (r) =>
          r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[0].noteId}` &&
          r.params.get('version') === '33'
      )
      .flush(onnxPrimaryDetail);
    fixture.detectChanges();

    badge = el.querySelector('.model-provenance-badge');
    expect(badge?.textContent?.trim()).toBe('Local ONNX');
    expect(badge?.getAttribute('data-model-provenance')).toBe('onnx-primary');

    // 3. Switch to different UHID -> Local ONNX · Fallback (explicit fallbackTriggered=true)
    const onnxFallbackDetail: QueueEntryDetail = {
      ...DETAIL_B,
      noteVersion: 5,
      extractionId: 'ext-onnx-fb',
      extraction: {
        ...DETAIL_B.extraction!,
        extractionId: 'ext-onnx-fb',
        modelMetadata: {
          provider: 'onnx',
          finalExecutedProvider: 'LocalOnnx',
          requestedPrimaryProvider: 'MicrosoftFoundry',
          adapterType: 'onnx_neurosymbolic_int8',
          model: 'bc5cdr_int8',
          modelDeployment: 'bc5cdr_int8',
          promptVersion: 'v2.1.0',
          routingMode: 'fallback',
          fallbackTriggered: true,
          fallbackReason: 'AZURE_TIMEOUT',
        },
      },
    };

    comp.openEntry(THREE_PATIENTS_QUEUE[1]);
    httpMock
      .expectOne(
        (r) =>
          r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[1].noteId}` &&
          r.params.get('patient') === 'P100237'
      )
      .flush(onnxFallbackDetail);
    fixture.detectChanges();

    badge = el.querySelector('.model-provenance-badge');
    expect(badge?.textContent?.trim()).toBe('Local ONNX · Fallback');
    expect(badge?.getAttribute('data-model-provenance')).toBe('onnx-fallback');

    // 4. Completed extraction with missing provenance -> "Model not recorded"
    const missingProvDetail: QueueEntryDetail = {
      ...DETAIL_B,
      noteVersion: 6,
      extractionId: 'ext-missing-prov',
      extraction: {
        ...DETAIL_B.extraction!,
        extractionId: 'ext-missing-prov',
        modelMetadata: {
          adapterType: '',
          modelDeployment: '',
          promptVersion: '',
          provider: null,
          finalExecutedProvider: null,
          fallbackTriggered: null,
        },
      },
    };
    comp.openEntry({ ...THREE_PATIENTS_QUEUE[1], noteVersion: 6, extractionId: 'ext-missing-prov' });
    httpMock
      .expectOne((r) => r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[1].noteId}` && r.params.get('version') === '6')
      .flush(missingProvDetail);
    fixture.detectChanges();

    badge = el.querySelector('.model-provenance-badge');
    expect(badge?.textContent?.trim()).toBe('Model not recorded');
    expect(badge?.getAttribute('data-model-provenance')).toBe('unknown');

    // 5. Pending extraction -> NO completed-model badge
    comp.openEntry(THREE_PATIENTS_QUEUE[2]);
    httpMock
      .expectOne((r) => r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[2].noteId}`)
      .flush(DETAIL_C_PROCESSING);
    fixture.detectChanges();
    expect(el.querySelector('.model-provenance-badge')).toBeNull();
    expect(el.textContent).toContain('Extraction in progress');

    // 6. Failed extraction -> NO completed-model badge
    const failedDetail: QueueEntryDetail = {
      ...DETAIL_A,
      noteVersion: 23,
      queueState: 'Failed',
      extractionId: null,
      extraction: null,
      error: 'AZURE_INVALID_EVIDENCE',
    };
    comp.openEntry({ ...THREE_PATIENTS_QUEUE[0], noteVersion: 23, queueState: 'Failed', extractionId: null });
    httpMock
      .expectOne((r) => r.url === `/api/v1/queue/${THREE_PATIENTS_QUEUE[0].noteId}` && r.params.get('version') === '23')
      .flush(failedDetail);
    fixture.detectChanges();
    expect(el.querySelector('.model-provenance-badge')).toBeNull();
    expect(el.textContent).toContain('AZURE_INVALID_EVIDENCE');
  });

  it('fails closed when dev-session is disabled (403) and exposes zero clinical records', async () => {
    const fixture = TestBed.createComponent(ClinicalQueueComponent);
    activeFixtures.push(fixture);
    fixture.detectChanges();

    httpMock
      .expectOne('/api/v1/emr/auth/dev-session')
      .flush(
        { code: 'DEV_SESSION_DISABLED', detail: 'Disabled outside Development loopback.' },
        { status: 403, statusText: 'Forbidden' }
      );
    await flushMicrotasks();

    httpMock.expectOne('/api/v1/emr/auth/me').flush({}, { status: 401, statusText: 'Unauthorized' });
    await flushMicrotasks();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Authorized session required');
    expect(fixture.componentInstance.queueItems().length).toBe(0);
  });
});

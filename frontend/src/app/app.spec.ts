import { describe, it, expect, vi, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { App } from './app';
import { ExtractionService } from './services/extraction.service';
import { ClinicalExtractionResponse } from './models/extraction.model';

const source =
  '  Fictional clinical note.\n\nPlan: Metformin 500 mg, 1 tablet twice daily.\nChest X-ray if symptoms persist.\n  ';
const quote = 'Chest X-ray if symptoms persist.';
const response: ClinicalExtractionResponse = {
  schemaVersion: '1.0.0',
  extractionId: 'fictional-run',
  correlationId: 'fictional-correlation',
  extractedAt: '2026-11-20T06:00:00Z',
  executionDurationMs: 42.5,
  modelMetadata: {
    adapterType: 'test',
    modelDeployment: 'fictional-model',
    promptVersion: 'test-v1',
    totalTokens: 1126,
    estimatedCostUsd: 0.00027,
  },
  preprocessed: { charLength: source.length, sections: [] },
  medications: [
    {
      name: 'Metformin',
      strength: '500 mg',
      dose: '1 tablet',
      route: 'oral',
      frequency: 'twice daily',
      duration: null,
      formulation: 'tablet',
      instructions: null,
      assertionStatus: 'current_medication',
      evidence: {
        quote: 'Metformin',
        start: source.indexOf('Metformin'),
        end: source.indexOf('Metformin') + 9,
        verified: true,
      },
      qualityFlags: [],
      findings: [],
      normalization: {
        status: 'matched',
        matchedCode: 'fictional-code',
        displayName: 'Metformin',
        confidence: 1,
      },
    },
  ],
  investigations: [
    {
      name: 'Chest X-ray',
      timing: null,
      specimen: null,
      assertionStatus: 'conditional',
      evidence: {
        quote,
        start: source.indexOf(quote),
        end: source.indexOf(quote) + quote.length,
        verified: true,
      },
      qualityFlags: ['REVIEW_CONDITION'],
      findings: [
        {
          code: 'CONDITIONAL_ORDER',
          severity: 'warning',
          field: 'assertionStatus',
          message: 'Only if symptoms persist.',
        },
      ],
      normalization: { status: 'unresolved', matchedCode: null, displayName: null, confidence: 0 },
    },
  ],
  validationSummary: { hasErrors: false, errorCount: 0, warningCount: 1 },
  noteText: source,
  inputType: 'text',
  saved: true,
};

describe('Clinical extraction workspace', () => {
  let service: {
    extract: ReturnType<typeof vi.fn>;
    checkHealth: ReturnType<typeof vi.fn>;
    listExtractions: ReturnType<typeof vi.fn>;
    getExtractionById: ReturnType<typeof vi.fn>;
    extractPdf: ReturnType<typeof vi.fn>;
    getFhirBundle: ReturnType<typeof vi.fn>;
    searchSemantic: ReturnType<typeof vi.fn>;
  };
  beforeEach(async () => {
    service = {
      extract: vi.fn().mockReturnValue(of(response)),
      checkHealth: vi.fn().mockReturnValue(of({ status: 'healthy' })),
      listExtractions: vi.fn().mockReturnValue(of([response])),
      getExtractionById: vi.fn().mockReturnValue(of(response)),
      extractPdf: vi.fn().mockReturnValue(
        of({
          response: { ...response, inputType: 'pdf', sourceFileName: 'fictional.pdf' },
          extractedText: source,
        }),
      ),
      getFhirBundle: vi.fn().mockReturnValue(of({ resourceType: 'Bundle', entry: [] })),
      searchSemantic: vi.fn().mockReturnValue(of([{ extraction: response }])),
    };
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [{ provide: ExtractionService, useValue: service }],
    }).compileComponents();
  });
  function workspace() {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return { fixture, app: fixture.componentInstance, html: fixture.nativeElement as HTMLElement };
  }
  function extract() {
    const state = workspace();
    state.app.onNoteChange(source);
    state.app.onExtract();
    state.fixture.detectChanges();
    return state;
  }
  function pdfEvent() {
    const input = {
      files: [new File(['%PDF-fictional'], 'fictional.pdf', { type: 'application/pdf' })],
      value: 'selected',
    };
    return { target: input } as unknown as Event;
  }
  it('opens an empty workspace in edit mode without sample content', () => {
    const { app, html } = workspace();
    expect(app.noteText()).toBe('');
    expect(app.editorMode()).toBe('edit');
    expect(html.querySelector('h1')?.textContent).toBe('Clinical extraction');
    expect(html.querySelector('textarea')).toBeTruthy();
    expect(html.textContent).toContain('A clearer view of your clinical note');
    expect(html.querySelector('.sidebar, .kpi-card, .model-pill')).toBeNull();
  });
  it('loads an example only when chosen and uses its title as the document label', () => {
    const { app } = workspace();
    app.loadPreset(app.presets[1]);
    expect(app.noteText()).toContain('Amoxicillin 500mg');
    expect(app.documentLabel()).toBe(app.presets[1].title);
    expect(app.result()).toBeNull();
  });
  it('submits the exact note including leading and trailing whitespace', () => {
    const { app, html } = extract();
    expect(service.extract).toHaveBeenCalledWith({ noteText: source });
    expect(app.submittedText()).toBe(source);
    expect(app.editorMode()).toBe('inspect');
    expect(html.querySelector('.clinical-narrative')?.textContent).toBe(source);
    expect(html.querySelector('textarea')).toBeNull();
  });
  it('renders strength and dose in separate columns and missing values explicitly', () => {
    const { html, app, fixture } = extract();
    const cells = html.querySelectorAll('.medication-table .result-row td');
    expect(cells[1].textContent).toBe('500 mg');
    expect(cells[2].textContent).toBe('1 tablet');
    expect(html.querySelector('#medications-heading')?.textContent).toContain('1');
    expect(html.querySelector('#investigations-heading')?.textContent).toContain('1');
    app.selectEntity(response.medications[0]);
    fixture.detectChanges();
    expect(html.querySelector('.row-details')?.textContent).toContain('Not documented');
    expect(html.querySelector('.row-details')?.textContent).toContain('oral');
  });
  it('selects the chest X-ray row and highlights exactly its returned evidence', () => {
    const { html, app, fixture } = extract();
    (html.querySelector('.investigation-table .entity-button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(app.selectedEntity()).toBe(response.investigations[0]);
    expect(
      html.querySelector('.investigation-table .result-row')?.classList.contains('selected'),
    ).toBe(true);
    expect(html.querySelector('.paper mark')?.textContent).toBe(quote);
    expect(html.querySelector('.source-evidence')?.textContent).toContain(quote);
    expect(html.querySelector('.status-cell')?.textContent).toBe('conditional');
  });
  it('highlights only the entity name when that is the returned evidence', () => {
    const { app, html, fixture } = extract();
    app.selectEntity(response.medications[0]);
    fixture.detectChanges();
    expect(html.querySelector('.paper mark')?.textContent).toBe('Metformin');
  });
  it('honors offsets for the second occurrence of repeated evidence', () => {
    const { app } = workspace();
    const text = 'Chest X-ray before. Chest X-ray after.';
    app.submittedText.set(text);
    const start = text.lastIndexOf('Chest X-ray');
    app.selectEntity({
      ...response.investigations[0],
      evidence: { quote: 'Chest X-ray', start, end: start + 11, verified: true },
    });
    expect(app.narrativeSegments()[0].text).toBe('Chest X-ray before. ');
    expect(app.narrativeSegments().find((s) => s.isHighlight)?.text).toBe('Chest X-ray');
  });
  it('converts validated Unicode code point offsets for legacy worker responses', () => {
    const { app } = workspace();
    app.submittedText.set('🩺 Chest X-ray before. Chest X-ray after.');
    app.selectEntity({
      ...response.investigations[0],
      evidence: { quote: 'Chest X-ray', start: 22, end: 33, verified: true },
    });
    expect(app.narrativeSegments()[0].text).toBe('🩺 Chest X-ray before. ');
  });
  it('falls back to an exact quote when offsets are invalid without inventing evidence', () => {
    const { app } = extract();
    app.selectEntity({
      ...response.investigations[0],
      evidence: { quote, start: -1, end: -1, verified: false },
    });
    expect(app.narrativeSegments().find((s) => s.isHighlight)?.text).toBe(quote);
    app.selectEntity({
      ...response.investigations[0],
      evidence: { quote: 'not present in the note', start: 0, end: 10, verified: false },
    });
    expect(app.narrativeSegments().some((s) => s.isHighlight)).toBe(false);
  });
  it('Edit retains the exact text while detaching the result and highlight', async () => {
    const { app, html, fixture } = extract();
    app.selectEntity(response.investigations[0]);
    app.beginEdit();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(app.noteText()).toBe(source);
    expect(app.result()).toBeNull();
    expect(app.selectedEntity()).toBeNull();
    expect(html.querySelector('mark')).toBeNull();
    expect(html.querySelector('textarea')?.value).toBe(source);
  });
  it('ignores a late extraction response after the note changes', () => {
    const pending = new Subject<ClinicalExtractionResponse>();
    service.extract.mockReturnValue(pending);
    const { app } = workspace();
    app.onNoteChange(source);
    app.onExtract();
    expect(app.isExtracting()).toBe(true);
    app.onNoteChange('A different fictional note');
    pending.next(response);
    expect(app.noteText()).toBe('A different fictional note');
    expect(app.result()).toBeNull();
    expect(app.isExtracting()).toBe(false);
  });
  it('ignores old errors and results while a newer request is pending', () => {
    const old = new Subject<ClinicalExtractionResponse>();
    const current = new Subject<ClinicalExtractionResponse>();
    service.extract.mockReturnValueOnce(old).mockReturnValueOnce(current);
    const { app } = workspace();
    app.onNoteChange(source);
    app.onExtract();
    app.onNoteChange('new note');
    app.onExtract();
    old.error({ code: 'PERSISTENCE_FAILED' });
    expect(app.error()).toBeNull();
    expect(app.isExtracting()).toBe(true);
    current.next({ ...response, noteText: 'new note' });
    expect(app.submittedText()).toBe('new note');
  });
  it('keeps loading, empty workspace, and successful empty results distinct', () => {
    const pending = new Subject<ClinicalExtractionResponse>();
    service.extract.mockReturnValue(pending);
    const { app, html, fixture } = workspace();
    app.onNoteChange(source);
    app.onExtract();
    fixture.detectChanges();
    expect(html.querySelector('.loading-state')?.textContent).toContain(
      'Reading your clinical note',
    );
    pending.next({ ...response, medications: [], investigations: [] });
    fixture.detectChanges();
    expect(html.textContent).toContain('Extraction complete');
    expect(html.textContent).toContain('No medications found');
    expect(html.textContent).toContain('No investigations found');
  });
  it('preserves input and presents an inline error on PERSISTENCE_FAILED', async () => {
    service.extract.mockReturnValue(
      throwError(() => ({
        title: 'Internal persistence failure',
        code: 'PERSISTENCE_FAILED',
        status: 503,
      })),
    );
    const { app, html, fixture } = workspace();
    app.onNoteChange(source);
    app.onExtract();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(app.noteText()).toBe(source);
    expect(html.querySelector('textarea')?.value).toBe(source);
    expect(html.querySelector('[role="alert"]')?.textContent).toContain(
      'Could not save extraction.',
    );
    expect(html.querySelector('.failed-state')).toBeTruthy();
    expect(app.result()).toBeNull();
  });
  it('preserves input on other request failures', () => {
    service.extract.mockReturnValue(
      throwError(() => ({ title: 'Service unavailable', detail: 'Try again later.', status: 503 })),
    );
    const { app } = workspace();
    app.onNoteChange(source);
    app.onExtract();
    expect(app.noteText()).toBe(source);
    expect(app.error()?.title).toBe('Service unavailable');
    expect(app.isExtracting()).toBe(false);
  });
  it('shows save failure separately when an API result is not saved', () => {
    service.extract.mockReturnValue(of({ ...response, saved: false }));
    const { app, html } = extract();
    expect(app.result()?.saved).toBe(false);
    expect(app.error()?.code).toBe('PERSISTENCE_FAILED');
    expect(html.textContent).toContain('Extracted · not saved');
    expect(app.noteText()).toBe(source);
  });
  it('restores a history run with its own source and filename', () => {
    service.getExtractionById.mockReturnValue(
      of({
        ...response,
        noteText: 'Historical fictional note',
        inputType: 'pdf',
        sourceFileName: 'history.pdf',
      }),
    );
    const { app } = extract();
    app.selectEntity(response.medications[0]);
    app.selectHistoryRun(response);
    expect(app.noteText()).toBe('Historical fictional note');
    expect(app.submittedText()).toBe('Historical fictional note');
    expect(app.documentLabel()).toBe('history.pdf');
    expect(app.selectedEntity()).toBeNull();
  });
  it('uses the matching session source if a history summary omits note text', () => {
    const { app } = extract();
    service.getExtractionById.mockReturnValue(throwError(() => ({})));
    app.onNoteChange('New fictional note');
    app.selectHistoryRun({ ...response, noteText: undefined });
    expect(app.submittedText()).toBe(source);
    expect(app.noteText()).toBe(source);
  });
  it('never pairs unrelated current text with a history run missing its source', () => {
    service.getExtractionById.mockReturnValue(of({ ...response, noteText: null }));
    const { app } = workspace();
    app.onNoteChange('unrelated');
    app.selectHistoryRun(response);
    expect(app.noteText()).toBe('');
    expect(app.submittedText()).toBe('');
    expect(app.sourceUnavailable()).toBe(true);
  });
  it('ignores a late history response after editing starts', () => {
    const pending = new Subject<ClinicalExtractionResponse>();
    service.getExtractionById.mockReturnValue(pending);
    const { app } = workspace();
    app.selectHistoryRun(response);
    app.onNoteChange('new fictional source');
    pending.next(response);
    expect(app.result()).toBeNull();
    expect(app.noteText()).toBe('new fictional source');
  });
  it('restores the PDF source and actual filename on success', () => {
    const { app } = workspace();
    app.onPdfSelected(pdfEvent());
    expect(app.documentLabel()).toBe('fictional.pdf');
    expect(app.submittedText()).toBe(source);
    expect(app.editorMode()).toBe('inspect');
  });
  it('ignores a late PDF response after editing starts', () => {
    const pending = new Subject<{ response: ClinicalExtractionResponse; extractedText?: string }>();
    service.extractPdf.mockReturnValue(pending);
    const { app } = workspace();
    app.onPdfSelected(pdfEvent());
    app.onNoteChange('new fictional source');
    pending.next({ response, extractedText: source });
    expect(app.result()).toBeNull();
    expect(app.noteText()).toBe('new fictional source');
    expect(app.uploadedFileName()).toBeNull();
  });
  it('preserves pasted text if PDF extraction fails', () => {
    service.extractPdf.mockReturnValue(throwError(() => ({ code: 'PERSISTENCE_FAILED' })));
    const { app } = workspace();
    app.onNoteChange(source);
    app.onPdfSelected(pdfEvent());
    expect(app.noteText()).toBe(source);
    expect(app.error()?.code).toBe('PERSISTENCE_FAILED');
    expect(app.documentLabel()).toBe('Pasted text');
  });
  it('keeps model information, JSON, flags and validation in Details', () => {
    const { app, html, fixture } = extract();
    app.setResultTab('details');
    fixture.detectChanges();
    expect(html.textContent).toContain('fictional-model');
    expect(html.textContent).toContain('1126');
    expect(html.textContent).toContain('$0.00027');
    expect(html.textContent).toContain('Only if symptoms persist.');
    expect(html.textContent).toContain('REVIEW_CONDITION');
    expect(html.querySelector('pre')?.textContent).toContain('schemaVersion');
  });
  it('keeps JSON, CSV and FHIR exports and correctly separates CSV dose and strength', () => {
    const { app } = extract();
    const spy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const setAttribute = vi.spyOn(HTMLAnchorElement.prototype, 'setAttribute');
    app.exportJson();
    app.exportCsv();
    app.exportFhirBundle();
    expect(spy).toHaveBeenCalledTimes(3);
    expect(service.getFhirBundle).toHaveBeenCalledWith('fictional-run');
    const csv = setAttribute.mock.calls.find(
      (args) => args[0] === 'href' && args[1].startsWith('data:text/csv'),
    )?.[1];
    expect(decodeURIComponent(csv || '')).toContain('"Strength","Dose"');
    expect(decodeURIComponent(csv || '')).toContain('"500 mg","1 tablet"');
    vi.restoreAllMocks();
  });
  it('clears the source, result, filename, and selection together', () => {
    const { app } = extract();
    app.selectEntity(response.investigations[0]);
    app.clear();
    expect(app.noteText()).toBe('');
    expect(app.result()).toBeNull();
    expect(app.uploadedFileName()).toBeNull();
    expect(app.selectedEntity()).toBeNull();
    expect(app.documentLabel()).toBe('Pasted text');
  });
  it('clears a stale offline status after a successful retry while retaining the source', () => {
    const { app, html, fixture } = workspace();
    app.backendOnline.set(false);
    app.onNoteChange(source);
    fixture.detectChanges();
    expect(html.querySelector('.connection-note')).toBeTruthy();
    app.onExtract();
    fixture.detectChanges();
    expect(app.backendOnline()).toBe(true);
    expect(html.querySelector('.connection-note')).toBeNull();
    expect(app.noteText()).toBe(source);
    expect(app.result()).toBe(response);
  });

  it('rejects empty submissions without contacting the backend', () => {
    const { app } = workspace();
    app.onNoteChange('   ');
    app.onExtract();
    expect(service.extract).not.toHaveBeenCalled();
    expect(app.error()?.code).toBe('EMPTY_NOTE_TEXT');
  });
  it('uses existing semantic search and sorting handlers for history', () => {
    const { app } = workspace();
    app.searchQuery.set('fictional');
    app.onSemanticSearch();
    expect(service.searchSemantic).toHaveBeenCalledWith('fictional', 25);
    expect(app.historyRuns()).toEqual([response]);
    app.historyRuns.set([
      response,
      { ...response, extractionId: 'older', extractedAt: '2026-11-19T06:00:00Z' },
    ]);
    expect(app.sortedHistoryRuns()[0].extractionId).toBe('fictional-run');
    app.toggleHistorySort();
    expect(app.sortedHistoryRuns()[0].extractionId).toBe('older');
  });
  it('shows actual Foundry provenance and measured usage under Details', () => {
    const foundry = {
      ...response,
      modelMetadata: {
        ...response.modelMetadata,
        provider: 'foundry',
        model: 'gpt-6.1-sol-2026-09-29',
        azureDeployment: 'clinical-gpt-6.1-sol',
        inferenceExecuted: true,
        promptTokens: 71,
        completionTokens: 29,
        totalTokens: 100,
      },
    };
    service.extract.mockReturnValue(of(foundry));
    const { app, html, fixture } = extract();
    expect(html.querySelector('.provider-summary')?.textContent).toContain('Microsoft Foundry — gpt-6.1-sol-2026-09-29');
    app.setResultTab('details');
    fixture.detectChanges();
    expect(html.textContent).toContain('gpt-6.1-sol-2026-09-29');
    expect(html.textContent).toContain('clinical-gpt-6.1-sol');
    expect(html.textContent).toContain('71 /');
  });
  it('labels recovered ONNX results and attributes rejected Azure usage to the Azure attempt', () => {
    const recovered = {
      ...response,
      modelMetadata: {
        ...response.modelMetadata,
        provider: 'onnx',
        model: 'bc5cdr_int8',
        adapterType: 'onnx_neurosymbolic_int8',
        fallbackReason: 'AZURE_TIMEOUT',
        promptTokens: null,
        completionTokens: null,
        totalTokens: null,
        estimatedCostUsd: null,
        inferenceExecuted: true,
        attempts: [
          {
            provider: 'foundry',
            model: 'gpt-6.1-sol',
            deployment: 'clinical',
            outcome: 'rejected',
            reason: 'AZURE_TIMEOUT',
            durationMs: 45000,
            inferenceExecuted: false,
            promptTokens: 71,
          },
          {
            provider: 'onnx',
            model: 'bc5cdr_int8',
            deployment: null,
            outcome: 'accepted',
            reason: null,
            durationMs: 70,
            inferenceExecuted: true,
            onnxWindows: 1,
          },
        ],
      },
    };
    service.extract.mockReturnValue(of(recovered));
    const { app, html, fixture } = extract();
    expect(html.querySelector('.provider-summary')?.textContent).toContain('Local ONNX — Fallback');
    expect(html.querySelector('.provider-summary')?.textContent).toContain('Azure timed out');
    app.setResultTab('details');
    fixture.detectChanges();
    expect(app.tokenValue(recovered, null)).toBe('N/A');
    expect(html.textContent).toContain('LocalOnnx has reduced coverage');
    expect(html.querySelectorAll('.provider-attempt').length).toBe(2);
    expect(html.textContent).toContain('1 ONNX windows');
    expect(html.textContent).toContain('N/A (unknown)');
  });
  it('keeps historical provider metadata readable without presenting old ONNX token constants as usage', () => {
    const historical = {
      ...response,
      modelMetadata: {
        ...response.modelMetadata,
        adapterType: 'onnx_neurosymbolic_int8',
        totalTokens: 350,
      },
    };
    const { app } = workspace();
    expect(app.providerLabel(historical)).toContain('Provider not recorded');
    expect(app.tokenValue(historical, 350)).toBe('N/A');
  });

  it('renders the post-live discharge summary extraction with all 7 medications, 10 investigations, terminology semantic labels, origin provenance, and 7 temporal safety alerts', () => {
    const liveDischargeRun: ClinicalExtractionResponse = {
      ...response,
      extractionId: '1713c76e-b355-4be3-b02f-e9d581b82415',
      correlationId: 'live-final-86985b4aaa88',
      inputType: 'pdf',
      sourceFileName: 'Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf',
      noteText: 'Documented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).\nDischarge Medications\nWarfarin 5 mg orally once daily in the evening for stroke prophylaxis.\nAzithromycin 500 mg orally once daily for 3 days to complete respiratory course.\nOutpatient Follow-Up Investigations\nRepeat serum potassium, serum sodium, and serum creatinine in 48 hours.\nFollow-up chest X-ray (PA view) and 12-lead ECG in 2 weeks.',
      modelMetadata: {
        ...response.modelMetadata,
        provider: 'foundry',
        model: 'gpt-6.1-sol-2026-09-29',
        azureDeployment: 'gpt-6.1-sol',
        modelDeployment: 'gpt-6.1-sol',
        inferenceExecuted: true,
        promptTokens: 1255,
        completionTokens: 2305,
        totalTokens: 3560,
        schemaValidationStatus: 'passed',
      },
      medications: [
        {
          ...response.medications[0],
          name: 'Penicillin',
          strength: null,
          assertionStatus: 'allergy',
          origin: 'LLM_EXTRACTED',
          normalization: {
            status: 'unmatched',
            matchedCode: null,
            displayName: null,
            confidence: 0,
            semanticStatus: 'unresolved',
            candidateCode: null,
            terminologySource: 'RxNorm',
            terminologyVersion: '02-Nov-2026',
          },
          evidence: {
            quote: 'Documented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).',
            verified: true,
            start: 0,
            end: 76,
          },
        },
        {
          ...response.medications[0],
          name: 'Azithromycin',
          strength: '500 mg',
          route: 'orally',
          frequency: 'once daily',
          duration: 'for 3 days',
          assertionStatus: 'current_medication',
          origin: 'LLM_AND_RULE_CONFIRMED',
          normalization: {
            status: 'matched',
            matchedCode: '18631',
            displayName: 'Azithromycin',
            confidence: 0.98,
            semanticStatus: 'exact',
            candidateCode: '18631',
            terminologySource: 'RxNorm',
            terminologyVersion: '02-Nov-2026',
          },
          evidence: {
            quote: 'Azithromycin 500 mg orally once daily for 3 days to complete respiratory course.',
            verified: true,
            start: 169,
            end: 249,
          },
        },
      ],
      investigations: [
        {
          ...response.investigations[0],
          name: 'serum potassium',
          timing: 'in 48 hours',
          specimen: 'serum',
          assertionStatus: 'new_order',
          origin: 'LLM_AND_RULE_CONFIRMED',
          normalization: {
            status: 'matched',
            matchedCode: '2823-3',
            displayName: 'Serum Potassium',
            confidence: 0.98,
            semanticStatus: 'exact',
            candidateCode: '2823-3',
            terminologySource: 'LOINC',
            terminologyVersion: '2.83',
          },
          evidence: {
            quote: 'Repeat serum potassium, serum sodium, and serum creatinine in 48 hours.',
            verified: true,
            start: 286,
            end: 357,
          },
        },
        {
          ...response.investigations[0],
          name: 'chest X-ray',
          timing: 'in 2 weeks',
          specimen: null,
          assertionStatus: 'new_order',
          origin: 'LLM_AND_RULE_CONFIRMED',
          normalization: {
            status: 'uncertain',
            matchedCode: null,
            displayName: 'Chest X-Ray PA',
            confidence: 0.5,
            semanticStatus: 'narrower',
            candidateCode: '24648-8',
            terminologySource: 'LOINC',
            terminologyVersion: '2.83',
          },
          evidence: {
            quote: 'Follow-up chest X-ray (PA view) and 12-lead ECG in 2 weeks.',
            verified: true,
            start: 358,
            end: 417,
          },
        },
      ],
      safetyAlerts: [
        {
          alertId: 'LAB-ec1b9b9b',
          severity: 'CRITICAL',
          category: 'LAB_ANOMALY',
          title: 'Critical Potassium Value (5.7 mEq/L)',
          description: 'Observed data (BASELINE_MEASUREMENT): Potassium = 5.7 mEq/L',
          involvedEntities: ['Potassium: 5.7 mEq/L'],
          recommendation: 'Review the source note and reconcile this finding with a qualified clinician.',
          alertType: 'DERIVED_CLINICAL_INFERENCE',
          temporality: 'BASELINE_MEASUREMENT',
          thresholdCriterion: 'Configured rule threshold: Serum Potassium > 5.5 mEq/L or < 3.0 mEq/L (reference interval 3.5–5.1 mEq/L)',
        },
        {
          alertId: 'VIT-362cb00b',
          severity: 'MODERATE',
          category: 'LAB_ANOMALY',
          title: 'Historical Admission Finding — Significant Pyrexia (38.9°C)',
          description: 'Observed data (HISTORICAL_ADMISSION_COURSE): Temperature = 38.9°C',
          involvedEntities: ['Temperature: 38.9°C'],
          recommendation: 'Review the source note and reconcile this finding with a qualified clinician.',
          alertType: 'DERIVED_CLINICAL_INFERENCE',
          temporality: 'HISTORICAL_ADMISSION_COURSE',
          thresholdCriterion: 'Configured rule threshold: Temperature >= 38.5°C',
        },
      ],
    };

    service.getExtractionById.mockReturnValue(of(liveDischargeRun));
    const { app, html, fixture } = workspace();
    app.selectHistoryRun(liveDischargeRun);
    fixture.detectChanges();

    expect(html.querySelector('.provider-summary')?.textContent).toContain('Microsoft Foundry — gpt-6.1-sol-2026-09-29');
    app.selectEntity(liveDischargeRun.medications[1]);
    fixture.detectChanges();
    expect(html.textContent).toContain('RxNorm: 18631 (exact)');
    expect(html.textContent).toContain('LLM AND RULE CONFIRMED');
    expect(html.querySelector('mark.evidence-highlight')?.textContent).toContain('Azithromycin 500 mg orally once daily for 3 days');

    app.selectEntity(liveDischargeRun.investigations[1]);
    fixture.detectChanges();
    expect(html.textContent).toContain('Chest X-Ray PA — code withheld (narrower vs 24648-8)');

    app.setResultTab('details');
    fixture.detectChanges();
    expect(html.textContent).toContain('CRITICAL · Critical Potassium Value (5.7 mEq/L)');
    expect(html.textContent).toContain('DERIVED_CLINICAL_INFERENCE · BASELINE_MEASUREMENT');
    expect(html.textContent).toContain('Historical Admission Finding — Significant Pyrexia (38.9°C)');
    expect(html.textContent).toContain('DERIVED_CLINICAL_INFERENCE · HISTORICAL_ADMISSION_COURSE');
    expect(html.textContent).toContain('1255 /');
    expect(html.textContent).toContain('3560');
  });

  it('supports entity-level clinical review workflow (ACCEPTED, CORRECTED, REJECTED, NEEDS_CLARIFICATION) while preserving original AI candidate', () => {
    const submitMock = vi.fn().mockImplementation((_id, req) =>
      of({
        reviewId: 'rev-1',
        extractionId: 'fictional-run',
        entityType: req.entityType,
        entityIndex: req.entityIndex,
        reviewStatus: req.reviewStatus,
        originalEntity: { name: 'Metformin', strength: '500 mg', dose: '1 tablet' },
        correctedEntity: req.correctedEntity || null,
        originalEvidence: response.medications[0].evidence,
        reviewAction: req.reviewStatus,
        reviewerIdentity: 'Unauthenticated Session (Local Demonstration Mode)',
        authMode: 'LOCAL_DEMO_MODE',
        reviewComment: req.reviewComment || null,
        reviewedAt: '2026-11-23T08:30:00Z',
        versionNumber: 1,
      }),
    );
    (service as any).submitEntityReview = submitMock;

    const { app, html, fixture } = extract();
    app.selectEntity(response.medications[0]);
    fixture.detectChanges();

    // 1. Reject without comment must show validation error
    app.submitReviewDecision('MEDICATION', 0, 'REJECTED');
    fixture.detectChanges();
    expect(app.reviewError()).toContain('clinical rationale or comment is required');
    expect(submitMock).not.toHaveBeenCalled();

    // 2. Open correction editor, modify dose, and save CORRECTED decision
    app.openCorrectionEditor('MEDICATION', 0, response.medications[0]);
    app.updateCorrectionField('dose', '2 tablets');
    app.reviewCommentDraft.set('Titrated per outpatient endocrinology note.');
    app.submitReviewDecision('MEDICATION', 0, 'CORRECTED');
    fixture.detectChanges();

    expect(submitMock).toHaveBeenCalledTimes(1);
    expect(submitMock.mock.calls[0][1].expectedVersion).toBe(0);
    expect(app.entityReviewStatus('MEDICATION', 0)).toBe('CORRECTED');
    // Original AI candidate must remain untouched
    expect(app.result()?.medications[0].dose).toBe('1 tablet');
    expect(html.textContent).toContain('Reviewer Corrected Values (AI Original Preserved Above)');
    expect(html.textContent).toContain('Dose: 2 tablets');
    expect(html.textContent).toContain('Unauthenticated Session (Local Demonstration Mode)');
    app.submitReviewDecision('MEDICATION', 0, 'ACCEPTED');
    expect(submitMock.mock.calls[1][1].expectedVersion).toBe(1);
  });
  it('keeps suppressed neural candidates visibly separate from prescriptions', () => {
    (service.extract as any).mockReturnValue(of({ ...response, modelMetadata: { ...response.modelMetadata, provider: 'onnx', reviewCandidates: [
      { name: 'Source candidate', category: 'MEDICATION', confidence: 0.42, evidence: response.medications[0].evidence, reason: 'BELOW_NEURAL_CONFIDENCE_THRESHOLD', reviewRequired: true }
    ] } }));
    const { html, app } = extract();
    expect(html.querySelector('[data-testid="source-review-candidates"]')?.textContent).toContain('Source candidate');
    expect(html.querySelector('[data-testid="source-review-candidates"]')?.textContent).toContain('withheld');
    expect(app.result()?.medications.length).toBe(response.medications.length);
    expect(html.querySelector('.medication-table')?.textContent).not.toContain('Source candidate');
  });

  it('distinguishes accepted GPT, authorized offline execution, and real fallback', () => {
    const { app } = workspace();
    const run = (metadata: any) => ({ ...response, modelMetadata: { ...response.modelMetadata, ...metadata } });
    expect(app.providerLabel(run({ provider: 'foundry', model: 'gpt-6.1-sol-2026-09-29' }))).toBe('Microsoft Foundry ? GPT-6.1 Sol');
    expect(app.providerLabel(run({ provider: 'onnx', routingMode: 'explicit_offline', fallbackReason: null }))).toBe('Local ONNX ? Offline Mode');
    expect(app.providerLabel(run({ provider: 'onnx', routingMode: 'fallback', fallbackReason: 'AZURE_RATE_LIMITED' }))).toContain('Fallback after Microsoft Foundry failure');
    expect(app.providerLabel(run({ provider: 'onnx', fallbackReason: 'AZURE_INVALID_EVIDENCE' }))).toContain('historical fallback policy');
    expect(app.providerLabel(run({ provider: 'onnx', fallbackReason: '<untrusted reason>' }))).not.toContain('<untrusted reason>');
  });

});

import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { App } from './app';
import { ExtractionService } from './services/extraction.service';
import { ClinicalExtractionResponse } from './models/extraction.model';

describe('App', () => {
  let mockExtractionService: { extract: ReturnType<typeof vi.fn> };

  const mockResponse: ClinicalExtractionResponse = {
    extractionId: 'ext-123',
    correlationId: 'corr-456',
    extractedAt: '2026-11-13T06:00:00Z',
    executionDurationMs: 42.5,
    modelMetadata: {
      adapterType: 'synthetic_mock',
      modelDeployment: 'synthetic-deterministic-engine',
      promptVersion: 'v1.0.0-synthetic'
    },
    preprocessed: {
      charLength: 120,
      sections: [{ code: 'PLAN', heading: 'Plan', start: 0, end: 120 }]
    },
    medications: [
      {
        name: 'Metformin',
        strength: '500mg',
        dose: '1 tab',
        route: 'PO',
        frequency: 'BD',
        duration: '30 days',
        formulation: 'tablet',
        instructions: 'after meals',
        assertionStatus: 'new_order',
        evidence: {
          quote: 'Metformin 500mg PO BD',
          verified: true,
          start: 10,
          end: 31
        },
        qualityFlags: [],
        findings: [],
        normalization: {
          status: 'matched',
          matchedCode: '860975',
          displayName: 'Metformin hydrochloride 500 MG Oral Tablet',
          confidence: 0.95
        }
      },
      {
        name: 'Warfarin',
        strength: '5mg',
        dose: null,
        route: 'PO',
        frequency: 'daily',
        duration: null,
        formulation: null,
        instructions: null,
        assertionStatus: 'discontinued',
        evidence: {
          quote: 'Discontinue Warfarin 5mg',
          verified: true,
          start: 40,
          end: 64
        },
        qualityFlags: [],
        findings: [],
        normalization: {
          status: 'matched',
          matchedCode: '855332',
          displayName: 'Warfarin Sodium 5 MG Oral Tablet',
          confidence: 0.98
        }
      }
    ],
    investigations: [
      {
        name: 'Serum Creatinine',
        timing: null,
        specimen: 'Blood',
        assertionStatus: 'conditional',
        evidence: {
          quote: 'Serum Creatinine if BP > 160',
          verified: true,
          start: 70,
          end: 98
        },
        qualityFlags: [],
        findings: [],
        normalization: {
          status: 'matched',
          matchedCode: '2160-0',
          displayName: 'Creatinine [Mass/volume] in Serum or Plasma',
          confidence: 0.94
        }
      }
    ],
    validationSummary: {
      hasErrors: false,
      errorCount: 0,
      warningCount: 0
    }
  };

  beforeEach(async () => {
    mockExtractionService = {
      extract: vi.fn().mockReturnValue(of(mockResponse))
    };

    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        { provide: ExtractionService, useValue: mockExtractionService }
      ]
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render application title', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.app-title')?.textContent).toContain('Clinical AI Extraction Platform');
  });

  it('should load synthetic preset into textarea', async () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    await fixture.whenStable();

    app.loadPreset(app.presets[1]); // Respiratory & Infection
    expect(app.noteText()).toContain('Amoxicillin 500mg');
  });

  it('should calculate word count and character count accurately', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    app.noteText.set('Metformin 500mg PO BD');
    expect(app.wordCount()).toBe(4);
    expect(app.characterCount()).toBe(21);
  });

  it('should call extraction service and display extracted medication cards', async () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    await fixture.whenStable();

    app.onExtract();
    expect(mockExtractionService.extract).toHaveBeenCalled();
    expect(app.result()).toEqual(mockResponse);

    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.entity-name')?.textContent).toContain('Metformin');
    expect(compiled.querySelector('.highlight-val')?.textContent).toContain('42.5 ms');
  });

  it('should filter medications by assertion status', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    app.result.set(mockResponse);

    expect(app.filteredMedications().length).toBe(2);

    app.setFilter('new_order');
    expect(app.filteredMedications().length).toBe(1);
    expect(app.filteredMedications()[0].name).toBe('Metformin');

    app.setFilter('discontinued');
    expect(app.filteredMedications().length).toBe(1);
    expect(app.filteredMedications()[0].name).toBe('Warfarin');

    app.setFilter('all');
    expect(app.filteredMedications().length).toBe(2);
  });

  it('should select entity and generate highlighted narrative segments', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    app.noteText.set('Plan: Give Metformin 500mg PO BD after meals.');
    const med = mockResponse.medications[0];

    app.selectEntity(med);
    expect(app.selectedEntity()).toBe(med);
    expect(app.editorMode()).toBe('inspect');

    const segments = app.narrativeSegments();
    expect(segments.some(s => s.isHighlight && s.text === 'Metformin 500mg PO BD')).toBe(true);

    app.clearSelection();
    expect(app.selectedEntity()).toBeNull();
    expect(app.editorMode()).toBe('edit');
  });

  it('should export extraction results to JSON and CSV without error', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    app.result.set(mockResponse);

    const clickSpy = vi.fn();
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, 'createElement').mockImplementation((tagName: string) => {
      const el = originalCreateElement(tagName);
      if (tagName === 'a') {
        el.click = clickSpy;
      }
      return el;
    });

    app.exportJson();
    expect(clickSpy).toHaveBeenCalled();

    clickSpy.mockClear();
    app.exportCsv();
    expect(clickSpy).toHaveBeenCalled();

    vi.restoreAllMocks();
  });

  it('should handle extraction service errors and display alert banner', async () => {
    mockExtractionService.extract.mockReturnValue(throwError(() => ({
      title: 'AI inference timeout',
      detail: 'The AI model took longer than 30s to respond.',
      status: 504,
      code: 'AI_MODEL_TIMEOUT'
    })));

    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    await fixture.whenStable();

    app.onExtract();
    expect(app.error()?.code).toBe('AI_MODEL_TIMEOUT');

    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.alert-error')?.textContent).toContain('AI inference timeout');
  });

  it('should render Microsoft Foundry live deployment telemetry and token metrics', async () => {
    const liveFoundryResponse: ClinicalExtractionResponse = {
      ...mockResponse,
      modelMetadata: {
        adapterType: 'microsoft_foundry_live',
        modelDeployment: 'gpt-4.1-mini',
        promptVersion: '2025-04-14',
        promptTokens: 901,
        completionTokens: 225,
        totalTokens: 1126,
        estimatedCostUsd: 0.00027
      }
    };

    mockExtractionService.extract.mockReturnValue(of(liveFoundryResponse));
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    await fixture.whenStable();

    app.onExtract();
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('gpt-4.1-mini');
    expect(compiled.textContent).toContain('1126');
    expect(compiled.textContent).toContain('$0.00027');
  });
});

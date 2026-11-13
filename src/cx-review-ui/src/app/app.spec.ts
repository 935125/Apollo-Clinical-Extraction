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
        writebackEligible: true,
        normalizationStatus: 'unmatched'
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
          start: 50,
          end: 78
        },
        qualityFlags: [],
        findings: [],
        writebackEligible: false,
        normalizationStatus: 'unmatched'
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
});

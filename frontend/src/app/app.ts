import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ExtractionService } from './services/extraction.service';
import {
  ClinicalExtractionResponse,
  EntityReviewRecord,
  EntityReviewStatus,
  ExtractedInvestigation,
  ExtractedMedication,
  ProblemDetails,
} from './models/extraction.model';

export interface SyntheticPreset {
  id: string;
  title: string;
  badge: string;
  category: 'respiratory' | 'diabetes' | 'hypertension' | 'other';
  icon: string;
  text: string;
}

export interface NarrativeSegment {
  text: string;
  isHighlight: boolean;
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  private readonly extractionService = inject(ExtractionService);

  readonly noteText = signal<string>('');
  readonly submittedText = signal<string>('');
  readonly documentLabel = signal('Pasted text');
  readonly sourceUnavailable = signal(false);
  readonly isOpeningHistory = signal(false);
  private revision = 0;
  private readonly sourceByRun = new Map<string, { text: string; label: string }>();

  readonly isExtracting = signal<boolean>(false);
  readonly result = signal<ClinicalExtractionResponse | null>(null);
  readonly error = signal<ProblemDetails | null>(null);
  readonly backendOnline = signal<boolean>(true);
  readonly uploadedFileName = signal<string | null>(null);
  readonly historyRuns = signal<ClinicalExtractionResponse[]>([]);
  readonly historySortAsc = signal<boolean>(false);
  readonly searchQuery = signal<string>('');

  readonly sortedHistoryRuns = computed<ClinicalExtractionResponse[]>(() => {
    const runs = [...this.historyRuns()];
    const asc = this.historySortAsc();
    runs.sort((a, b) => {
      const ta = new Date(a.extractedAt).getTime() || 0;
      const tb = new Date(b.extractedAt).getTime() || 0;
      return asc ? ta - tb : tb - ta;
    });
    return runs;
  });

  readonly activeResultTab = signal<'structured' | 'source' | 'details'>('structured');
  readonly activeNav = signal<string>('extraction');
  readonly selectedEntity = signal<ExtractedMedication | ExtractedInvestigation | null>(null);
  readonly editorMode = signal<'edit' | 'inspect'>('edit');
  readonly selectedPresetId = signal<string>('');
  readonly exportDropdownOpen = signal<boolean>(false);

  // Stage 5: Entity-level clinical review workflow signals
  readonly entityReviews = signal<EntityReviewRecord[]>([]);
  readonly isSubmittingReview = signal<boolean>(false);
  readonly reviewError = signal<string | null>(null);
  readonly reviewSuccess = signal<string | null>(null);
  readonly editingCorrectionKey = signal<string | null>(null);
  readonly reviewCommentDraft = signal<string>('');
  readonly correctionForm = signal<Record<string, string>>({});

  readonly activePrescriptionsCount = computed<number>(() => {
    const res = this.result();
    return res ? res.medications.filter((m) => m.assertionStatus !== 'allergy').length : 0;
  });

  readonly documentedAllergiesCount = computed<number>(() => {
    const res = this.result();
    return res ? res.medications.filter((m) => m.assertionStatus === 'allergy').length : 0;
  });

  readonly plannedInvestigationsCount = computed<number>(() => {
    const res = this.result();
    return res ? res.investigations.filter((i) => i.assertionStatus !== 'historical').length : 0;
  });

  readonly historicalLabsCount = computed<number>(() => {
    const res = this.result();
    return res ? res.investigations.filter((i) => i.assertionStatus === 'historical').length : 0;
  });

  readonly wordCount = computed<number>(() => {
    const text = this.noteText().trim();
    return text ? text.split(/\s+/).length : 0;
  });

  readonly characterCount = computed<number>(() => this.noteText().length);

  readonly formattedDuration = computed<string>(() => {
    const res = this.result();
    if (!res || res.executionDurationMs === undefined || res.executionDurationMs === null) {
      return 'Not documented';
    }
    return res.executionDurationMs >= 1000
      ? (res.executionDurationMs / 1000).toFixed(2) + ' s'
      : res.executionDurationMs + ' ms';
  });

  readonly narrativeSegments = computed<NarrativeSegment[]>(() => {
    const fullText = this.submittedText();
    const entity = this.selectedEntity();

    if (!entity || !entity.evidence || !entity.evidence.quote) {
      return [{ text: fullText, isHighlight: false }];
    }

    const ev = entity.evidence;
    let start = ev.start;
    let end = ev.end;

    if (
      start == null ||
      end == null ||
      start < 0 ||
      end <= start ||
      end > fullText.length ||
      fullText.substring(start, end) !== ev.quote
    ) {
      // Validate code-point offsets from older worker results before falling back.
      const points = Array.from(fullText);
      if (
        start != null &&
        end != null &&
        start >= 0 &&
        end > start &&
        end <= points.length &&
        points.slice(start, end).join('') === ev.quote
      ) {
        const utfStart = points.slice(0, start).join('').length;
        start = utfStart;
        end = utfStart + ev.quote.length;
      } else {
        const idx = fullText.indexOf(ev.quote);
        if (idx !== -1) {
          start = idx;
          end = idx + ev.quote.length;
        } else {
          return [{ text: fullText, isHighlight: false }];
        }
      }
    }

    const segments: NarrativeSegment[] = [];
    if (start > 0) {
      segments.push({ text: fullText.substring(0, start), isHighlight: false });
    }
    segments.push({ text: fullText.substring(start, end), isHighlight: true });
    if (end < fullText.length) {
      segments.push({ text: fullText.substring(end), isHighlight: false });
    }
    return segments;
  });

  readonly presets: SyntheticPreset[] = [
    {
      id: 'respiratory',
      title: 'Respiratory Infection',
      badge: 'Community Acquired',
      category: 'respiratory',
      icon: 'lungs',
      text: `62-year-old male with type 2 diabetes and hypertension presents with 3 days of fever, productive cough and shortness of breath.

Assessment: Community acquired pneumonia.

Plan:
• Start amoxicillin-clavulanate 875/125 mg orally twice daily for 7 days.
• Add azithromycin 500 mg on day 1, then 250 mg once daily for 4 more days.
• Continue metformin 500 mg twice daily.
• Increase amlodipine to 10 mg once daily.

Investigations:
• Chest X-ray to rule out consolidation.
• CBC, CRP and U&E today.`,
    },
    {
      id: 'respiratory_alt',
      title: 'Respiratory & Infection',
      badge: 'Acute Care',
      category: 'respiratory',
      icon: 'lungs',
      text: `History of Present Illness: 42-year-old female presents with productive cough and fever x 4 days.
Medications: Discontinue Warfarin 5mg immediately due to prolonged prothrombin time.
Plan:
1. Prescribe Amoxicillin 500mg PO TDS for 7 days.
2. Paracetamol 650mg PO SOS for fever > 38.5 C.
3. Order Chest X-ray PA view.
4. Order Complete Blood Count (CBC).`,
    },
    {
      id: 'diabetes',
      title: 'Diabetes Follow-up',
      badge: 'Endocrinology',
      category: 'diabetes',
      icon: 'water',
      text: `Chief Complaint: 58-year-old male with type 2 diabetes and hypertension for routine follow-up.
Past Medical History: Prior history of gout; patient took Allopurinol 100mg daily in 2023, ceased last year.
Allergies: No Aspirin (severe bronchospasm and facial swelling).
Current Medications: Metformin 500mg PO BD with meals.
Assessment: Uncontrolled glycemia; borderline blood pressure.
Plan:
1. Increase Metformin to 1000mg PO BD after meals.
2. Start Atorvastatin 20mg PO at bedtime for dyslipidemia.
3. Order HbA1c and Lipid Profile.
4. Order Serum Creatinine if systolic blood pressure exceeds 160 mmHg.`,
    },
    {
      id: 'hypertension',
      title: 'Hypertension',
      badge: 'Cardiology',
      category: 'hypertension',
      icon: 'heart',
      text: `Patient: 65-year-old female with essential hypertension.
Current Medications: Amlodipine 5mg PO daily.
Plan:
1. Increase Amlodipine to 10mg PO daily.
2. Add Lisinopril 10mg PO daily.
3. Order Serum Electrolytes in 2 weeks.
4. Order 12-lead ECG.`,
    },
    {
      id: 'indian_outpatient',
      title: 'Indian Outpatient',
      badge: 'Apollo Outpatient',
      category: 'other',
      icon: 'dots',
      text: `Patient: 54-year-old female with hypothyroidism, dyslipidemia, and urinary tract infection.
Medications:
• Start Niftas 100 mg PO BID for 5 days.
• Continue Thyronorm 50 mcg PO OD before breakfast.
• Start Atorva 20 mg PO at bedtime for 30 days.
• Dolo 650 mg PO PRN for fever.

Investigations:
• CUE and Urine Culture & Sensitivity.
• RFT and Fasting Lipid Profile.`,
    },
  ];

  ngOnInit(): void {
    if (typeof this.extractionService.checkHealth === 'function') {
      this.extractionService.checkHealth().subscribe((res) => {
        this.backendOnline.set(res.status === 'healthy' || res.status === 'active');
      });
    }
    this.loadHistory();
    if (typeof window !== 'undefined' && window.location?.search) {
      const params = new URLSearchParams(window.location.search);
      const deepLinkId = params.get('extractionId');
      if (deepLinkId) {
        this.openExtractionById(deepLinkId);
      }
    }
  }

  openExtractionById(extractionId: string): void {
    if (!extractionId || typeof this.extractionService.getExtractionById !== 'function') return;
    const revision = this.detach();
    this.activeNav.set('extraction');
    this.isOpeningHistory.set(true);
    this.extractionService.getExtractionById(extractionId).subscribe({
      next: (full) => {
        if (revision !== this.revision) return;
        const text = full.noteText ?? '';
        this.noteText.set(text);
        this.submittedText.set(text);
        this.uploadedFileName.set(full.sourceFileName ?? null);
        this.documentLabel.set(
          full.sourceFileName || (full.inputType === 'pdf' ? 'PDF document' : 'Pasted text'),
        );
        this.selectedPresetId.set('');
        this.result.set(full);
        this.entityReviews.set(full.entityReviews || []);
        this.sourceUnavailable.set(!text);
        this.editorMode.set('inspect');
        this.isOpeningHistory.set(false);
      },
      error: (err: ProblemDetails) => {
        if (revision !== this.revision) return;
        this.error.set(err);
        this.isOpeningHistory.set(false);
      },
    });
  }

  loadHistory(): void {
    if (typeof this.extractionService.listExtractions === 'function') {
      this.extractionService.listExtractions(50).subscribe((runs) => {
        this.historyRuns.set(runs);
      });
    }
  }

  toggleHistorySort(): void {
    this.historySortAsc.set(!this.historySortAsc());
  }

  onSemanticSearch(): void {
    const q = this.searchQuery().trim();
    if (!q) {
      this.loadHistory();
      return;
    }
    if (typeof this.extractionService.searchSemantic === 'function') {
      this.extractionService.searchSemantic(q, 25).subscribe((results) => {
        const mapped = results.map((r: any) => r.extraction || r);
        this.historyRuns.set(mapped);
      });
    }
  }

  private detach(): number {
    this.revision++;
    this.result.set(null);
    this.submittedText.set('');
    this.selectedEntity.set(null);
    this.error.set(null);
    this.sourceUnavailable.set(false);
    this.isExtracting.set(false);
    this.isOpeningHistory.set(false);
    this.exportDropdownOpen.set(false);
    this.activeResultTab.set('structured');
    return this.revision;
  }

  onNoteChange(text: string): void {
    this.detach();
    this.noteText.set(text);
    this.editorMode.set('edit');
    this.uploadedFileName.set(null);
    this.selectedPresetId.set('');
    this.documentLabel.set('Pasted text');
  }

  beginEdit(): void {
    this.detach();
    this.editorMode.set('edit');
  }

  selectHistoryRun(run: ClinicalExtractionResponse): void {
    const revision = this.detach();
    this.activeNav.set('extraction');
    this.isOpeningHistory.set(true);
    const restore = (full: ClinicalExtractionResponse) => {
      if (revision !== this.revision) return;
      const cached = this.sourceByRun.get(full.extractionId);
      const text = full.noteText ?? cached?.text ?? '';
      this.noteText.set(text);
      this.submittedText.set(text);
      this.uploadedFileName.set(full.sourceFileName ?? null);
      this.documentLabel.set(
        full.sourceFileName ||
          cached?.label ||
          (full.inputType === 'pdf' ? 'PDF document' : 'Pasted text'),
      );
      this.selectedPresetId.set('');
      this.result.set(full);
      this.entityReviews.set(full.entityReviews || []);
      this.sourceUnavailable.set(!text);
      this.editorMode.set('inspect');
      this.isOpeningHistory.set(false);
    };
    if (run.extractionId && this.extractionService.getExtractionById) {
      this.extractionService
        .getExtractionById(run.extractionId)
        .subscribe({ next: restore, error: () => restore(run) });
    } else restore(run);
  }

  loadPreset(preset: SyntheticPreset): void {
    this.onNoteChange(preset.text);
    this.selectedPresetId.set(preset.id);
    this.documentLabel.set(preset.title);
  }

  clear(): void {
    this.onNoteChange('');
  }

  setResultTab(tab: 'structured' | 'source' | 'details'): void {
    this.activeResultTab.set(tab);
  }

  onTabsKey(event: KeyboardEvent): void {
    const tabs = ['structured', 'source', 'details'] as const;
    const current = tabs.indexOf(this.activeResultTab());
    let index: number;
    if (event.key === 'ArrowRight') index = (current + 1) % tabs.length;
    else if (event.key === 'ArrowLeft') index = (current + tabs.length - 1) % tabs.length;
    else if (event.key === 'Home') index = 0;
    else if (event.key === 'End') index = tabs.length - 1;
    else return;
    event.preventDefault();
    this.setResultTab(tabs[index]);
    document.getElementById('tab-' + tabs[index])?.focus();
  }

  readonly evidenceLocated = computed(() =>
    this.narrativeSegments().some((segment) => segment.isHighlight),
  );

  setNav(item: string): void {
    this.activeNav.set(item);
    if (item === 'history') {
      this.loadHistory();
    }
  }

  selectEntity(item: ExtractedMedication | ExtractedInvestigation): void {
    this.reviewError.set(null);
    this.reviewSuccess.set(null);
    this.editingCorrectionKey.set(null);
    this.reviewCommentDraft.set('');
    if (this.selectedEntity() === item) {
      this.selectedEntity.set(null);
      this.editorMode.set('inspect');
    } else {
      this.selectedEntity.set(item);
      this.editorMode.set('inspect');
    }
  }

  clearSelection(): void {
    this.selectedEntity.set(null);
  }

  toggleExportDropdown(): void {
    this.exportDropdownOpen.set(!this.exportDropdownOpen());
  }

  providerLabel(run: ClinicalExtractionResponse): string {
    const metadata = run.modelMetadata;
    if (metadata.provider === 'foundry') {
      const modelId = (metadata.model || metadata.azureDeployment || metadata.modelDeployment || '').trim();
      return `Microsoft Foundry ? ${/^gpt-6\.1-sol(?:-|$)/i.test(modelId) ? 'GPT-6.1 Sol' : modelId || 'Model not recorded'}`;
    }
    if (metadata.provider === 'onnx') {
      if (metadata.routingMode === 'explicit_offline') return 'Local ONNX ? Offline Mode';
      if (!metadata.fallbackReason) return 'Local ONNX ? Direct execution (mode not recorded)';
      const reasons: Record<string, string> = {
        AZURE_TIMEOUT: 'Microsoft Foundry timed out', AZURE_UNREACHABLE: 'Microsoft Foundry unreachable',
        AZURE_UNAVAILABLE: 'Microsoft Foundry temporarily unavailable', AZURE_RATE_LIMITED: 'Microsoft Foundry rate limit',
        AZURE_CIRCUIT_OPEN: 'Microsoft Foundry recovery circuit open', AZURE_TRUNCATED: 'Incomplete primary response',
        AZURE_UNUSABLE_JSON: 'Primary response could not be parsed',
        AZURE_INVALID_EVIDENCE: 'Evidence rejected under historical fallback policy; review required',
        AZURE_NOT_CONFIGURED: 'Historical configuration failure; review required',
        AZURE_CREDENTIALS_MISSING: 'Historical authentication failure; review required',
        AZURE_CREDENTIALS_INVALID: 'Historical authentication failure; review required'
      };
      return `Local ONNX ? Fallback after Microsoft Foundry failure ? ${reasons[metadata.fallbackReason] || 'Provider failure (reason not classified)'}`;
    }
    return 'Provider not recorded (historical run)';
  }

  tokenValue(run: ClinicalExtractionResponse, value: number | null | undefined): string {
    return run.modelMetadata.provider === 'onnx' ||
      (!run.modelMetadata.provider && run.modelMetadata.adapterType.startsWith('onnx'))
      ? 'N/A'
      : value == null
        ? 'N/A'
        : String(value);
  }

  display(value: string | null | undefined): string {
    return value?.trim() ? value : 'Not documented';
  }
  status(value: string | null | undefined): string {
    return value ? value.replace(/_/g, ' ') : 'Not documented';
  }
  terminologyLabel(entity: ExtractedMedication | ExtractedInvestigation): string {
    const norm = entity.normalization;
    if (!norm) return 'Unresolved';
    const sem = norm.semanticStatus || (norm.status === 'matched' ? 'exact' : norm.displayName ? 'narrower' : 'unresolved');
    if (norm.matchedCode) {
      return `${norm.terminologySource || 'Standard'}: ${norm.matchedCode} (${sem})`;
    }
    if (norm.displayName) {
      return `${norm.displayName} — code withheld (${sem}${norm.candidateCode ? ' vs ' + norm.candidateCode : ''})`;
    }
    return `Unresolved (${sem})`;
  }
  originLabel(entity: ExtractedMedication | ExtractedInvestigation): string {
    return (entity.origin || entity.provenance?.origin || 'LLM_EXTRACTED').replace(/_/g, ' ');
  }

  entityKey(entityType: 'MEDICATION' | 'INVESTIGATION', index: number): string {
    return `${entityType}:${index}`;
  }

  latestEntityReview(
    entityType: 'MEDICATION' | 'INVESTIGATION',
    index: number,
  ): EntityReviewRecord | null {
    const matches = this.entityReviews().filter(
      (r) => r.entityType.toUpperCase() === entityType && r.entityIndex === index,
    );
    if (!matches.length) return null;
    return matches.reduce((prev, curr) => (curr.versionNumber >= prev.versionNumber ? curr : prev));
  }

  entityReviewHistory(
    entityType: 'MEDICATION' | 'INVESTIGATION',
    index: number,
  ): EntityReviewRecord[] {
    return this.entityReviews()
      .filter((r) => r.entityType.toUpperCase() === entityType && r.entityIndex === index)
      .sort((a, b) => a.versionNumber - b.versionNumber);
  }

  entityReviewStatus(
    entityType: 'MEDICATION' | 'INVESTIGATION',
    index: number,
  ): EntityReviewStatus | string {
    return this.latestEntityReview(entityType, index)?.reviewStatus || 'PENDING_REVIEW';
  }

  openCorrectionEditor(
    entityType: 'MEDICATION' | 'INVESTIGATION',
    index: number,
    entity: ExtractedMedication | ExtractedInvestigation,
  ): void {
    const key = this.entityKey(entityType, index);
    this.editingCorrectionKey.set(key);
    this.reviewError.set(null);
    this.reviewSuccess.set(null);
    const latest = this.latestEntityReview(entityType, index);
    const existingCorr = latest?.correctedEntity || {};
    if (entityType === 'MEDICATION') {
      const m = entity as ExtractedMedication;
      this.correctionForm.set({
        name: String(existingCorr['name'] ?? m.name ?? ''),
        strength: String(existingCorr['strength'] ?? m.strength ?? ''),
        dose: String(existingCorr['dose'] ?? m.dose ?? ''),
        formulation: String(existingCorr['formulation'] ?? m.formulation ?? ''),
        route: String(existingCorr['route'] ?? m.route ?? ''),
        frequency: String(existingCorr['frequency'] ?? m.frequency ?? ''),
        duration: String(existingCorr['duration'] ?? m.duration ?? ''),
        action: String(existingCorr['action'] ?? m.action ?? ''),
        assertionStatus: String(existingCorr['assertionStatus'] ?? m.assertionStatus ?? ''),
        rxNormCode: String(
          existingCorr['rxNormCode'] ??
            m.normalization?.matchedCode ??
            m.normalization?.candidateCode ??
            '',
        ),
      });
    } else {
      const inv = entity as ExtractedInvestigation;
      this.correctionForm.set({
        name: String(existingCorr['name'] ?? inv.name ?? ''),
        timing: String(existingCorr['timing'] ?? inv.timing ?? ''),
        specimen: String(existingCorr['specimen'] ?? inv.specimen ?? ''),
        method: String(existingCorr['method'] ?? inv.method ?? ''),
        assertionStatus: String(existingCorr['assertionStatus'] ?? inv.assertionStatus ?? ''),
        loincCode: String(
          existingCorr['loincCode'] ??
            inv.normalization?.matchedCode ??
            inv.normalization?.candidateCode ??
            '',
        ),
        clinicalCategory: String(existingCorr['clinicalCategory'] ?? inv.clinicalCategory ?? ''),
      });
    }
  }

  cancelCorrectionEditor(): void {
    this.editingCorrectionKey.set(null);
    this.reviewError.set(null);
  }

  updateCorrectionField(field: string, value: string): void {
    this.correctionForm.update((curr) => ({ ...curr, [field]: value }));
  }

  submitReviewDecision(
    entityType: 'MEDICATION' | 'INVESTIGATION',
    index: number,
    reviewStatus: EntityReviewStatus,
  ): void {
    const res = this.result();
    if (!res || !res.extractionId) return;

    this.reviewError.set(null);
    this.reviewSuccess.set(null);

    const comment = this.reviewCommentDraft().trim();
    if (
      (reviewStatus === 'REJECTED' || reviewStatus === 'NEEDS_CLARIFICATION') &&
      !comment
    ) {
      this.reviewError.set(
        `A clinical rationale or comment is required when marking an entity as ${reviewStatus}.`,
      );
      return;
    }

    let correctedEntity: Record<string, any> | null = null;
    if (reviewStatus === 'CORRECTED') {
      const form = this.correctionForm();
      if (!form['name']?.trim()) {
        this.reviewError.set('The corrected entity name cannot be empty.');
        return;
      }
      correctedEntity = { ...form };
    }

    if (typeof this.extractionService.submitEntityReview !== 'function') return;

    this.isSubmittingReview.set(true);
    this.extractionService
      .submitEntityReview(res.extractionId, {
        entityType,
        entityIndex: index,
        reviewStatus,
        expectedVersion: Math.max(0, ...this.entityReviews().filter(r => r.entityType === entityType && r.entityIndex === index).map(r => r.versionNumber)),
        correctedEntity,
        reviewComment: comment || null,
      })
      .subscribe({
        next: (savedRecord) => {
          this.entityReviews.update((list) => [...list, savedRecord]);
          this.isSubmittingReview.set(false);
          this.editingCorrectionKey.set(null);
          this.reviewCommentDraft.set('');
          this.reviewSuccess.set(
            `Saved ${savedRecord.reviewStatus} decision (v${savedRecord.versionNumber})`,
          );
        },
        error: (err: ProblemDetails) => {
          this.isSubmittingReview.set(false);
          this.reviewError.set(
            err.detail || err.title || 'Could not save clinical review decision.',
          );
        },
      });
  }
  context(entity: ExtractedInvestigation): string {
    return (
      entity.evidence?.quote ||
      [entity.timing, entity.specimen].filter(Boolean).join(' \u00b7 ') ||
      'Not documented'
    );
  }
  readonly allFindings = computed(() => {
    const r = this.result();
    return r
      ? [...r.medications, ...r.investigations].flatMap((entity) => [
          ...entity.findings.map((f) => ({ ...f, entity: entity.name })),
          ...entity.qualityFlags.map((flag) => ({
            code: flag,
            severity: 'warning',
            field: null,
            message: flag,
            entity: entity.name,
          })),
        ])
      : [];
  });

  onExtract(): void {
    const text = this.noteText();
    if (!text.trim()) {
      this.error.set({
        title: 'Enter a source note',
        detail: 'Paste a clinical note or choose an example to begin.',
        code: 'EMPTY_NOTE_TEXT',
        status: 400,
      });
      return;
    }
    const revision = this.detach();
    this.isExtracting.set(true);
    this.extractionService.extract({ noteText: text }).subscribe({
      next: (response) => {
        if (revision !== this.revision || this.noteText() !== text) return;
        this.acceptResult(response, text);
      },
      error: (error: ProblemDetails) => {
        if (revision !== this.revision) return;
        this.error.set(error);
        this.isExtracting.set(false);
      },
    });
  }

  private acceptResult(response: ClinicalExtractionResponse, text: string): void {
    this.backendOnline.set(true);
    this.noteText.set(text);
    this.submittedText.set(text);
    this.sourceByRun.set(response.extractionId, { text, label: this.documentLabel() });
    this.result.set(response);
    this.entityReviews.set(response.entityReviews || []);
    this.isExtracting.set(false);
    this.editorMode.set('inspect');
    if (!response.saved)
      this.error.set({ code: 'PERSISTENCE_FAILED', title: 'Could not save extraction' });
    this.loadHistory();
  }

  exportFhirBundle(): void {
    const res = this.result();
    if (!res || !res.extractionId || typeof this.extractionService.getFhirBundle !== 'function')
      return;

    this.extractionService.getFhirBundle(res.extractionId).subscribe({
      next: (bundle) => {
        const dataStr =
          'data:text/json;charset=utf-8,' + encodeURIComponent(JSON.stringify(bundle, null, 2));
        const downloadAnchor = document.createElement('a');
        downloadAnchor.setAttribute('href', dataStr);
        downloadAnchor.setAttribute('download', `fhir-r4-bundle-${res.extractionId}.json`);
        document.body.appendChild(downloadAnchor);
        downloadAnchor.click();
        downloadAnchor.remove();
        this.exportDropdownOpen.set(false);
      },
      error: (err: ProblemDetails) => {
        if (this.result() === res) this.error.set(err);
      },
    });
  }

  onPdfSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input?.files?.[0];
    if (!file) return;
    this.processPdfFile(file);
    input.value = '';
  }

  private processPdfFile(file: File): void {
    if (typeof this.extractionService.extractPdf !== 'function') return;

    const revision = this.detach();
    this.isExtracting.set(true);
    this.extractionService.extractPdf(file).subscribe({
      next: ({ response, extractedText }) => {
        if (revision !== this.revision) return;
        this.uploadedFileName.set(response.sourceFileName || file.name);
        this.selectedPresetId.set('');
        this.documentLabel.set(response.sourceFileName || file.name);
        const text = extractedText ?? response.noteText ?? '';
        this.acceptResult(response, text);
        this.sourceUnavailable.set(!text);
      },
      error: (err: ProblemDetails) => {
        if (revision !== this.revision) return;
        this.error.set(err);
        this.isExtracting.set(false);
      },
    });
  }

  exportJson(): void {
    const res = this.result();
    if (!res) return;

    const dataStr =
      'data:text/json;charset=utf-8,' + encodeURIComponent(JSON.stringify(res, null, 2));
    const downloadAnchor = document.createElement('a');
    downloadAnchor.setAttribute('href', dataStr);
    downloadAnchor.setAttribute(
      'download',
      `clinical-extraction-${res.correlationId || 'export'}.json`,
    );
    document.body.appendChild(downloadAnchor);
    downloadAnchor.click();
    downloadAnchor.remove();
    this.exportDropdownOpen.set(false);
  }

  exportCsv(): void {
    const res = this.result();
    if (!res) return;

    const rows: (string | null | undefined)[][] = [
      [
        'Type',
        'Name',
        'Strength',
        'Dose',
        'Route',
        'Frequency',
        'Duration',
        'Status',
        'Context',
        'Evidence Quote',
        'Match Code',
        'Confidence',
      ],
    ];
    for (const m of res.medications)
      rows.push([
        'Medication',
        m.name,
        m.strength,
        m.dose,
        m.route,
        m.frequency,
        m.duration,
        m.assertionStatus,
        m.instructions,
        m.evidence?.quote,
        m.normalization?.matchedCode,
        String(m.normalization?.confidence ?? ''),
      ]);
    for (const i of res.investigations)
      rows.push([
        'Investigation',
        i.name,
        '',
        '',
        '',
        '',
        '',
        i.assertionStatus,
        this.context(i),
        i.evidence?.quote,
        i.normalization?.matchedCode,
        String(i.normalization?.confidence ?? ''),
      ]);
    const csv = rows
      .map((row) => row.map((value) => `"${String(value ?? '').replace(/"/g, '""')}"`).join(','))
      .join('\r\n');
    const encodedUri = 'data:text/csv;charset=utf-8,' + encodeURIComponent(csv);
    const downloadAnchor = document.createElement('a');
    downloadAnchor.setAttribute('href', encodedUri);
    downloadAnchor.setAttribute(
      'download',
      `clinical-extraction-${res.correlationId || 'export'}.csv`,
    );
    document.body.appendChild(downloadAnchor);
    downloadAnchor.click();
    downloadAnchor.remove();
    this.exportDropdownOpen.set(false);
  }
}

import {
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  Subject,
  catchError,
  debounceTime,
  distinctUntilChanged,
  filter,
  fromEvent,
  interval,
  map,
  merge,
  of,
  switchMap,
} from 'rxjs';
import { ApolloBrand } from '../emr/portal-shell';
import { Encounter, apiError } from '../emr/emr-api';
import {
  ExtractedInvestigation,
  ExtractedMedication,
} from '../models/extraction.model';
import {
  PatientNoteOption,
  QueueApiService,
  QueueEntryDetail,
  QueueItem,
  QueueState,
} from './queue-api';

export type QueuePageSize = 5 | 10;

export type ModelProvenanceVariant = 'foundry' | 'onnx-fallback' | 'onnx-primary' | 'unknown';

export interface ModelProvenanceDisplay {
  label: string;
  variant: ModelProvenanceVariant;
  detail: string;
}

export interface ActiveSelection {
  patientReference: string;
  noteId: string;
  version: number;
  eventId?: string | null;
  token: number;
}

export interface SourceSegment {
  text: string;
  isHighlight: boolean;
}

export interface SyntheticIntakePreset {
  id: string;
  label: string;
  text: string;
}

@Component({
  selector: 'clinical-queue',
  standalone: true,
  imports: [CommonModule, FormsModule, ApolloBrand],
  templateUrl: './clinical-queue.html',
  styleUrl: './clinical-queue.scss',
})
export class ClinicalQueueComponent implements OnInit {
  private readonly api = inject(QueueApiService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly manualRefresh$ = new Subject<void>();
  private readonly pageSizeChange$ = new Subject<QueuePageSize>();
  private readonly searchTrigger$ = new Subject<string>();
  private readonly selectionTrigger$ = new Subject<ActiveSelection | null>();
  private readonly backgroundDetailRefresh$ = new Subject<ActiveSelection>();

  private requestToken = 0;
  private lastObservedEntrySignature: string | null = null;
  private readonly lastLoadedSearch = signal('');

  // Authorization state
  readonly initializing = signal(true);
  readonly authorized = signal(false);
  readonly authError = signal<string | null>(null);

  // Queue state (minimal — no status filter pills or summary counters)
  readonly pageSizeOptions: readonly QueuePageSize[] = [5, 10];
  readonly pageSize = signal<QueuePageSize>(10);
  readonly queueItems = signal<QueueItem[]>([]);
  readonly searchQuery = signal('');
  readonly lastUpdatedAt = signal<Date | null>(null);
  readonly lastUpdatedLabel = signal('Connecting…');
  readonly tabHidden = signal(false);
  readonly isRefreshing = signal(false);
  readonly connectionIssue = signal<string | null>(null);

  // Patient-scoped selected state (strictly isolated via switchMap + monotonic requestToken)
  readonly activeSelection = signal<ActiveSelection | null>(null);
  readonly selectedEntry = signal<QueueEntryDetail | null>(null);
  readonly detailLoading = signal(false);
  readonly detailError = signal<string | null>(null);
  readonly highlightedEntity = signal<ExtractedMedication | ExtractedInvestigation | null>(null);
  readonly exportMenuOpen = signal(false);

  // Separate development-only test note intake drawer (uses same API as Swagger/Postman)
  readonly intakeOpen = signal(false);
  readonly encounters = signal<Encounter[]>([]);
  readonly selectedEncounterId = signal<string>('');
  readonly intakeText = signal('');
  readonly submittingNote = signal(false);
  readonly intakeError = signal<string | null>(null);
  readonly intakeSuccess = signal<string | null>(null);

  readonly intakePresets: readonly SyntheticIntakePreset[] = [
    {
      id: 'outpatient_complete',
      label: 'Hypertension + Automated CBC',
      text: `Chief Complaint:\nFollow-up for essential hypertension and dyslipidaemia.\n\nCurrent Medications:\nContinue Amlodipine 5 mg 1 tablet orally once daily for 30 days.\nContinue Atorvastatin 10 mg 1 tablet orally at bedtime.\n\nPlan:\nStart Telmisartan 40 mg 1/2 tablet orally once daily in the morning for 14 days.\nOrder Complete Blood Count automated and Serum Creatinine tomorrow morning.`,
    },
    {
      id: 'respiratory_acute',
      label: 'Acute Respiratory Infection',
      text: `Chief Complaint:\n3 days of productive cough and low-grade fever.\n\nPlan:\nStart Amoxicillin 500 mg 1 capsule orally three times daily for 5 days.\nTake Paracetamol 650 mg 1 tablet orally as needed for fever.\nOrder Complete Blood Count and Chest X-ray PA view if fever persists beyond 48 hours.`,
    },
  ];

  readonly visibleQueueItems = computed<QueueItem[]>(() => {
    const query = this.searchQuery().trim().toLowerCase();
    const items = this.queueItems();
    if (!query || query === this.lastLoadedSearch().toLowerCase()) {
      return items;
    }
    return items.filter(
      (item) =>
        item.patientReference.toLowerCase().includes(query) ||
        item.encounterReference.toLowerCase().includes(query) ||
        item.sourcePreview.toLowerCase().includes(query)
    );
  });

  readonly selectedNoteKey = computed(() => {
    const entry = this.selectedEntry();
    if (!entry) return '';
    return `${entry.noteId}:${entry.noteVersion}`;
  });

  /**
   * Within-patient note/version list: strictly restricted to notes belonging to
   * the currently selected patient reference. Never contains unrelated patients.
   */
  readonly withinPatientNotes = computed<PatientNoteOption[]>(() => {
    const entry = this.selectedEntry();
    if (!entry?.patientNotes?.length) return [];
    return entry.patientNotes;
  });

  readonly hasAnyExtractedEntities = computed(() => {
    const ext = this.selectedEntry()?.extraction;
    if (!ext) return false;
    return ext.medications.length > 0 || ext.investigations.length > 0;
  });

  /**
   * Derives the completed AI engine badge strictly from the selected extraction's
   * persisted execution metadata (never from current global app configuration).
   * Returns null for pending or failed extractions so no misleading completed badge renders.
   */
  readonly modelProvenance = computed<ModelProvenanceDisplay | null>(() => {
    const entry = this.selectedEntry();
    const ext = entry?.extraction;
    if (!entry || !ext || entry.queueState === 'Failed') {
      return null;
    }

    const mm = ext.modelMetadata;
    if (!mm) {
      return {
        label: 'Model not recorded',
        variant: 'unknown',
        detail: 'No persisted model provenance was recorded for this extraction.',
      };
    }

    const provider = (mm.provider ?? '').trim().toLowerCase();
    const finalProvider = (mm.finalExecutedProvider ?? '').trim().toLowerCase();
    const adapter = (mm.adapterType ?? '').trim().toLowerCase();
    const model = (mm.model ?? mm.azureDeployment ?? mm.modelDeployment ?? '').trim();
    const modelLower = model.toLowerCase();
    const routingMode = (mm.routingMode ?? '').trim().toLowerCase();
    const fallbackReason = (mm.fallbackReason ?? '').trim();

    const isFoundry =
      provider === 'foundry' ||
      provider === 'microsoft_foundry' ||
      finalProvider === 'microsoftfoundry' ||
      adapter.includes('foundry');

    if (isFoundry) {
      const displayModel =
        !modelLower || modelLower.includes('gpt-6.1-sol')
          ? 'GPT-6.1 Sol'
          : model;
      const deployment = (mm.azureDeployment ?? mm.modelDeployment ?? '').trim();
      const parts = [
        model ? `Model: ${model}` : null,
        deployment ? `Deployment: ${deployment}` : null,
      ].filter(Boolean);
      return {
        label: `✦ ${displayModel} · Microsoft Foundry`,
        variant: 'foundry',
        detail: parts.length ? parts.join(' · ') : 'Completed via Microsoft Foundry',
      };
    }

    const isOnnx =
      provider === 'onnx' ||
      finalProvider === 'localonnx' ||
      adapter.startsWith('onnx') ||
      modelLower.includes('bc5cdr') ||
      modelLower.includes('onnx');

    if (isOnnx) {
      const explicitFallback =
        mm.fallbackTriggered === true ||
        routingMode === 'fallback' ||
        fallbackReason.length > 0;

      if (explicitFallback) {
        const parts = [
          fallbackReason ? `Fallback reason: ${fallbackReason}` : 'Executed as fallback after primary provider failure',
          model ? `Model: ${model}` : null,
        ].filter(Boolean);
        return {
          label: 'Local ONNX · Fallback',
          variant: 'onnx-fallback',
          detail: parts.join(' · '),
        };
      }

      const parts = [
        model ? `Model: ${model}` : null,
        adapter ? `Adapter: ${mm.adapterType}` : null,
      ].filter(Boolean);
      return {
        label: 'Local ONNX',
        variant: 'onnx-primary',
        detail: parts.length ? parts.join(' · ') : 'Executed via Local ONNX primary engine',
      };
    }

    return {
      label: 'Model not recorded',
      variant: 'unknown',
      detail: 'Persisted execution metadata did not specify a recognized model provider.',
    };
  });

  readonly sourceSegments = computed<SourceSegment[]>(() => {
    const fullText = this.selectedEntry()?.sourceText ?? '';
    const entity = this.highlightedEntity();
    if (!fullText) return [];
    if (!entity?.evidence?.quote) {
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
      const idx = fullText.indexOf(ev.quote);
      if (idx === -1) {
        return [{ text: fullText, isHighlight: false }];
      }
      start = idx;
      end = idx + ev.quote.length;
    }

    const segments: SourceSegment[] = [];
    if (start > 0) segments.push({ text: fullText.substring(0, start), isHighlight: false });
    segments.push({ text: fullText.substring(start, end), isHighlight: true });
    if (end < fullText.length) segments.push({ text: fullText.substring(end), isHighlight: false });
    return segments;
  });

  ngOnInit(): void {
    this.setupPatientScopedDetailPipeline();
    void this.bootstrap();

    if (typeof document !== 'undefined') {
      fromEvent(document, 'visibilitychange')
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => {
          const hidden = document.hidden;
          this.tabHidden.set(hidden);
          if (!hidden && this.authorized()) {
            this.manualRefresh$.next();
          }
        });
    }

    interval(1000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.updateRelativeTimestamp());
  }

  /**
   * RxJS switchMap pipelines guarantee automatic cancellation of superseded patient requests
   * and strict identity verification before committing any detail payload to component state.
   */
  private setupPatientScopedDetailPipeline(): void {
    this.selectionTrigger$
      .pipe(
        switchMap((target) => {
          if (!target) return of(null);
          return this.api
            .getQueueEntry(target.noteId, {
              version: target.version,
              patientReference: target.patientReference,
            })
            .pipe(
              map((detail) => ({ target, detail, error: null as unknown })),
              catchError((error) => of({ target, detail: null as QueueEntryDetail | null, error }))
            );
        }),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe((outcome) => {
        if (!outcome) return;
        const { target, detail, error } = outcome;
        const current = this.activeSelection();

        // Reject any stale, cancelled, or mismatched patient/version response
        if (!current || current.token !== target.token || target.token !== this.requestToken) {
          return;
        }

        if (error || !detail) {
          this.selectedEntry.set(null);
          this.detailLoading.set(false);
          this.detailError.set(apiError(error));
          return;
        }

        if (!this.matchesSelectionIdentity(target, detail)) {
          this.selectedEntry.set(null);
          this.detailLoading.set(false);
          this.detailError.set(
            'Returned clinical record did not match the selected patient identifier and was discarded.'
          );
          return;
        }

        this.selectedEntry.set(detail);
        this.detailLoading.set(false);
        this.detailError.set(null);
      });

    this.backgroundDetailRefresh$
      .pipe(
        switchMap((target) =>
          this.api
            .getQueueEntry(target.noteId, {
              version: target.version,
              patientReference: target.patientReference,
            })
            .pipe(
              map((detail) => ({ target, detail })),
              catchError(() => of(null))
            )
        ),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe((outcome) => {
        if (!outcome) return;
        const { target, detail } = outcome;
        const current = this.activeSelection();
        if (!current || current.token !== target.token || target.token !== this.requestToken) {
          return;
        }
        if (!this.matchesSelectionIdentity(target, detail)) {
          return;
        }
        this.selectedEntry.set(detail);
        this.detailLoading.set(false);
        this.detailError.set(null);
      });
  }

  private matchesSelectionIdentity(target: ActiveSelection, detail: QueueEntryDetail): boolean {
    if (detail.noteId !== target.noteId) return false;
    if (detail.patientReference.toLowerCase() !== target.patientReference.toLowerCase()) return false;
    if (target.version != null && detail.noteVersion !== target.version) return false;
    if (detail.extraction && detail.extractionId && detail.extraction.extractionId !== detail.extractionId) {
      return false;
    }
    return true;
  }

  async bootstrap(): Promise<void> {
    this.initializing.set(true);
    const session = await this.api.ensureAuthorizedSession();
    this.initializing.set(false);

    if (!session.authorized) {
      this.authorized.set(false);
      this.authError.set(session.reason ?? 'Authorization required.');
      return;
    }

    this.authorized.set(true);
    this.authError.set(null);
    this.startLivePolling();
  }

  private startLivePolling(): void {
    const pollTicks$ = interval(4000).pipe(
      filter(() => !this.tabHidden() && !this.isRefreshing())
    );
    const searchChanges$ = this.searchTrigger$.pipe(
      debounceTime(250),
      distinctUntilChanged()
    );

    merge(
      of('init'),
      pollTicks$,
      this.manualRefresh$,
      this.pageSizeChange$,
      searchChanges$
    )
      .pipe(
        switchMap(() => {
          if (this.tabHidden()) {
            return of(null);
          }
          const requestedLimit = this.pageSize();
          const requestedSearch = this.searchQuery().trim();
          this.isRefreshing.set(true);
          return this.api
            .listQueue({
              limit: requestedLimit,
              search: requestedSearch || undefined,
            })
            .pipe(
              map((res) => ({ res, requestedLimit, requestedSearch })),
              catchError(() => {
                this.connectionIssue.set(
                  'Connection issue — retaining last received queue.'
                );
                this.isRefreshing.set(false);
                return of(null);
              })
            );
        }),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe((outcome) => {
        if (!outcome) return;
        const { res, requestedLimit, requestedSearch } = outcome;
        if (
          requestedLimit !== this.pageSize() ||
          requestedSearch !== this.searchQuery().trim()
        ) {
          return;
        }
        this.isRefreshing.set(false);
        this.connectionIssue.set(null);
        this.lastLoadedSearch.set(requestedSearch);
        this.queueItems.set(res.items);
        this.lastUpdatedAt.set(new Date());
        this.updateRelativeTimestamp();

        // Keep an actively viewed submission synchronized by exact (noteId, noteVersion, patientReference)
        const current = this.activeSelection();
        if (current) {
          const updatedItem = res.items.find(
            (i) =>
              i.noteId === current.noteId &&
              i.noteVersion === current.version &&
              i.patientReference.toLowerCase() === current.patientReference.toLowerCase()
          );
          if (updatedItem) {
            const sig = `${updatedItem.eventId ?? ''}:${updatedItem.noteVersion}:${updatedItem.queueState}:${updatedItem.extractionId ?? ''}:${updatedItem.receivedAt}`;
            const isInProgress = this.isJobActive(updatedItem.queueState);
            if (sig !== this.lastObservedEntrySignature || isInProgress) {
              this.lastObservedEntrySignature = sig;
              this.backgroundDetailRefresh$.next(current);
            }
          }
        }
      });
  }

  onPageSizeChange(rawSize: number | string): void {
    const parsed = Number(rawSize);
    if (parsed !== 5 && parsed !== 10) return;
    if (this.pageSize() === parsed) return;
    this.pageSize.set(parsed);
    if (this.authorized()) {
      this.pageSizeChange$.next(parsed);
    }
  }

  onSearchChange(query: string): void {
    this.searchQuery.set(query);
    if (this.authorized()) {
      this.searchTrigger$.next(query.trim());
    }
  }

  refreshNow(): void {
    if (!this.authorized()) {
      void this.bootstrap();
      return;
    }
    this.manualRefresh$.next();
    const current = this.activeSelection();
    if (current) {
      this.backgroundDetailRefresh$.next(current);
    }
  }

  /**
   * Immediately clears all previous patient data, cancels any in-flight request via switchMap,
   * and fetches ONLY the newly selected patient's authorized record.
   */
  openEntry(item: QueueItem): void {
    const token = ++this.requestToken;
    const selection: ActiveSelection = {
      patientReference: item.patientReference,
      noteId: item.noteId,
      version: item.noteVersion,
      eventId: item.eventId ?? null,
      token,
    };

    // Atomic state reset — zero previous-patient or previous-version text or entities remain visible
    this.selectedEntry.set(null);
    this.highlightedEntity.set(null);
    this.detailError.set(null);
    this.exportMenuOpen.set(false);
    this.detailLoading.set(true);
    this.activeSelection.set(selection);
    this.lastObservedEntrySignature = `${item.eventId ?? ''}:${item.noteVersion}:${item.queueState}:${item.extractionId ?? ''}:${item.receivedAt}`;

    this.selectionTrigger$.next(selection);
  }

  /**
   * Switches note or immutable version strictly within the currently selected patient.
   */
  onWithinPatientNoteChange(compositeKey: string): void {
    const current = this.activeSelection();
    if (!current || !compositeKey) return;
    const [noteId, rawVersion] = compositeKey.split(':');
    const version = Number(rawVersion);
    if (!noteId || !Number.isFinite(version) || version <= 0) return;

    const matchingOption = this.withinPatientNotes().find(
      (n) => n.noteId === noteId && n.noteVersion === version
    );
    if (!matchingOption) return;

    const token = ++this.requestToken;
    const nextSelection: ActiveSelection = {
      patientReference: current.patientReference,
      noteId: matchingOption.noteId,
      version: matchingOption.noteVersion,
      token,
    };

    this.selectedEntry.set(null);
    this.highlightedEntity.set(null);
    this.detailError.set(null);
    this.exportMenuOpen.set(false);
    this.detailLoading.set(true);
    this.activeSelection.set(nextSelection);
    this.lastObservedEntrySignature = `${matchingOption.noteVersion}:${matchingOption.queueState}:${matchingOption.extractionId ?? ''}:${matchingOption.receivedAt}`;

    this.selectionTrigger$.next(nextSelection);
  }

  backToQueue(): void {
    this.requestToken++;
    this.activeSelection.set(null);
    this.selectedEntry.set(null);
    this.highlightedEntity.set(null);
    this.detailLoading.set(false);
    this.detailError.set(null);
    this.exportMenuOpen.set(false);
    this.lastObservedEntrySignature = null;
    this.selectionTrigger$.next(null);
    if (this.authorized()) {
      this.manualRefresh$.next();
    }
  }

  toggleEntityHighlight(entity: ExtractedMedication | ExtractedInvestigation): void {
    if (this.highlightedEntity() === entity) {
      this.highlightedEntity.set(null);
    } else {
      this.highlightedEntity.set(entity);
      setTimeout(() => {
        const mark = document.querySelector('.source-document mark');
        mark?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }, 30);
    }
  }

  // Separate development-only test note intake drawer
  async toggleIntakeDrawer(): Promise<void> {
    const next = !this.intakeOpen();
    this.intakeOpen.set(next);
    this.intakeError.set(null);
    this.intakeSuccess.set(null);
    if (next && this.encounters().length === 0) {
      try {
        const list = await this.api.listEncounters();
        this.encounters.set(list);
        if (list.length > 0 && !this.selectedEncounterId()) {
          this.selectedEncounterId.set(list[0].id);
        }
      } catch (err) {
        this.intakeError.set(apiError(err));
      }
    }
  }

  applyIntakePreset(preset: SyntheticIntakePreset): void {
    this.intakeText.set(preset.text);
    this.intakeError.set(null);
  }

  async submitIntakeNote(): Promise<void> {
    const text = this.intakeText().trim();
    const encounter = this.encounters().find((e) => e.id === this.selectedEncounterId());
    if (!encounter) {
      this.intakeError.set('Select an assigned synthetic encounter.');
      return;
    }
    if (text.length < 15) {
      this.intakeError.set('Enter at least 15 characters of synthetic clinical note text.');
      return;
    }

    this.submittingNote.set(true);
    this.intakeError.set(null);
    this.intakeSuccess.set(null);
    try {
      const signed = await this.api.submitClinicalNote(encounter, text);
      this.intakeSuccess.set(
        `Submitted test note for ${signed.patientReference} (${signed.encounterReference}, v${signed.version}).`
      );
      this.intakeText.set('');
      this.intakeOpen.set(false);
      this.manualRefresh$.next();
    } catch (err) {
      this.intakeError.set(apiError(err));
    } finally {
      this.submittingNote.set(false);
    }
  }

  // Minimal display helpers
  isJobActive(state: QueueState): boolean {
    return state === 'Waiting' || state === 'Queued' || state === 'Extracting';
  }

  private updateRelativeTimestamp(): void {
    const updated = this.lastUpdatedAt();
    if (!updated) {
      this.lastUpdatedLabel.set('Connecting…');
      return;
    }
    const sec = Math.max(0, Math.floor((Date.now() - updated.getTime()) / 1000));
    if (sec < 5) this.lastUpdatedLabel.set('Updated just now');
    else if (sec < 60) this.lastUpdatedLabel.set(`Updated ${sec}s ago`);
    else this.lastUpdatedLabel.set(`Updated ${Math.floor(sec / 60)}m ago`);
  }

  formatTimestamp(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString('en-GB', {
      day: '2-digit',
      month: 'short',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
    });
  }

  assertionLabel(status: string | null | undefined): string {
    if (!status) return 'Not documented';
    const map: Record<string, string> = {
      new_order: 'New order',
      current_medication: 'Current medication',
      continued: 'Continued',
      discontinued: 'Discontinued',
      historical: 'Historical',
      negated: 'Negated',
      ruled_out: 'Ruled out',
      conditional: 'Conditional',
      allergy: 'Allergy',
      ordered: 'New order',
      planned: 'Planned',
      performed: 'Performed',
    };
    return map[status.toLowerCase()] || status.replace(/_/g, ' ');
  }

  assertionClass(status: string | null | undefined): string {
    const s = (status || '').toLowerCase();
    if (s === 'allergy' || s === 'discontinued') return 'chip-danger';
    if (s === 'conditional' || s === 'negated' || s === 'ruled_out') return 'chip-amber';
    if (s === 'historical') return 'chip-neutral';
    return 'chip-green';
  }

  investigationOrderIntent(inv: ExtractedInvestigation): string {
    const s = (inv.assertionStatus || '').toLowerCase();
    if (s === 'historical' || s === 'performed') return 'Historical';
    if (s === 'negated' || s === 'ruled_out') return 'Not ordered';
    if (s === 'conditional') return 'Conditional order';
    return 'New order';
  }

  investigationCodeDisplay(inv: ExtractedInvestigation): string {
    const n = inv.normalization;
    if (!n) return 'Not resolved';
    if (n.matchedCode) {
      return `${n.terminologySource || 'LOINC'} ${n.matchedCode}`;
    }
    if (n.candidateCode) {
      return `Candidate ${n.terminologySource || 'LOINC'} ${n.candidateCode} (withheld)`;
    }
    return 'Not resolved';
  }

  medicationCodeSubtext(med: ExtractedMedication): string | null {
    const n = med.normalization;
    if (!n?.matchedCode) return null;
    return `${n.terminologySource || 'RxNorm'} ${n.matchedCode}`;
  }

  docValue(val: string | null | undefined): string {
    return val?.trim() ? val.trim() : 'Not documented';
  }

  // Export helpers
  exportJson(): void {
    const ext = this.selectedEntry()?.extraction;
    if (!ext) return;
    this.triggerDownload(
      'application/json;charset=utf-8',
      JSON.stringify(ext, null, 2),
      `apollo-extraction-${ext.extractionId}.json`
    );
    this.exportMenuOpen.set(false);
  }

  exportCsv(): void {
    const ext = this.selectedEntry()?.extraction;
    if (!ext) return;
    const rows: (string | null | undefined)[][] = [
      ['Type', 'Name', 'Strength', 'Dose', 'Route', 'Frequency', 'Duration', 'Assertion', 'Terminology', 'Evidence Quote'],
    ];
    for (const m of ext.medications) {
      rows.push([
        'Medication', m.name, m.strength, m.dose, m.route, m.frequency, m.duration,
        m.assertionStatus, this.medicationCodeSubtext(m) ?? 'Not resolved', m.evidence?.quote,
      ]);
    }
    for (const i of ext.investigations) {
      rows.push([
        'Investigation', i.name, '', '', '', '', '',
        i.assertionStatus, this.investigationCodeDisplay(i), i.evidence?.quote,
      ]);
    }
    const csv = rows
      .map((r) => r.map((v) => `"${String(v ?? '').replace(/"/g, '""')}"`).join(','))
      .join('\r\n');
    this.triggerDownload('text/csv;charset=utf-8', csv, `apollo-extraction-${ext.extractionId}.csv`);
    this.exportMenuOpen.set(false);
  }

  exportFhir(): void {
    const ext = this.selectedEntry()?.extraction;
    if (!ext?.extractionId) return;
    this.api
      .getFhirBundle(ext.extractionId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (bundle) => {
          this.triggerDownload(
            'application/json;charset=utf-8',
            JSON.stringify(bundle, null, 2),
            `apollo-fhir-r4-${ext.extractionId}.json`
          );
          this.exportMenuOpen.set(false);
        },
        error: () => {
          this.exportMenuOpen.set(false);
        },
      });
  }

  private triggerDownload(mime: string, content: string, filename: string): void {
    const url = `data:${mime},${encodeURIComponent(content)}`;
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
  }
}

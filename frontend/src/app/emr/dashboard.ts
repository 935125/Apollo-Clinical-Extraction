import { Component, computed, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { EmrApi, Encounter, apiError } from './emr-api';

interface PatientSummary {
  patientReference: string;
  encounterCount: number;
  latestEncounter: Encounter;
  signedCount: number;
  draftCount: number;
}

@Component({
  selector: 'doctor-dashboard',
  standalone: true,
  imports: [DatePipe, FormsModule, RouterLink],
  template: `
    <main class="emr-page dashboard-page">
      <div class="emr-page-heading">
        <div>
          <h1>{{ title() }}</h1>
          <p class="emr-description">{{ subtitle() }}</p>
        </div>
        <button class="emr-button primary" (click)="chooseEncounter()">
          <span class="plus" aria-hidden="true">+</span> New Clinical Note
        </button>
      </div>

      @if (mode === 'review') {
        <div class="emr-alert info" role="note">
          <strong>Physician Read-Only Inspection:</strong> You can inspect extraction results for
          your assigned encounters below. Formal entity approval and cross-doctor review queues
          require <strong>ClinicalReviewer</strong> authorization.
        </div>
      }

      @if (mode === 'dashboard' || mode === 'encounters') {
        <section class="kpi-grid" aria-label="Operational clinical metrics">
          <div class="kpi-card">
            <div class="kpi-card-top">
              <svg class="kpi-icon" viewBox="0 0 24 24" aria-hidden="true">
                <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
                <circle cx="9" cy="7" r="4" />
                <path d="M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" />
              </svg>
              <span class="kpi-value" data-testid="kpi-assigned">{{ encounters().length }}</span>
            </div>
            <span class="kpi-label">Assigned Encounters</span>
          </div>

          <div class="kpi-card">
            <div class="kpi-card-top">
              <svg class="kpi-icon" viewBox="0 0 24 24" aria-hidden="true">
                <path d="M6 3h9l4 4v14H6z" />
                <path d="M14 3v5h5M9 13h6M9 17h4" />
              </svg>
              <span class="kpi-value" data-testid="kpi-drafts">{{ drafts() }}</span>
            </div>
            <span class="kpi-label">Draft Notes</span>
          </div>

          <div class="kpi-card">
            <div class="kpi-card-top">
              <svg class="kpi-icon" viewBox="0 0 24 24" aria-hidden="true">
                <path d="M17 3a2.83 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
              </svg>
              <span class="kpi-value" data-testid="kpi-signed">{{ signedNotes() }}</span>
            </div>
            <span class="kpi-label">Signed Notes</span>
          </div>

          <div class="kpi-card">
            <div class="kpi-card-top">
              <svg class="kpi-icon" viewBox="0 0 24 24" aria-hidden="true">
                <path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z" />
              </svg>
              <span class="kpi-value" data-testid="kpi-awaiting">{{ awaitingExtraction() }}</span>
            </div>
            <span class="kpi-label">Awaiting Extraction</span>
          </div>

          <div class="kpi-card">
            <div class="kpi-card-top">
              <svg class="kpi-icon" viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="12" cy="12" r="9" />
                <path d="m8.5 12 2.5 2.5 4.5-5" />
              </svg>
              <span class="kpi-value" data-testid="kpi-completed">{{ completed() }}</span>
            </div>
            <span class="kpi-label">Extraction Completed</span>
          </div>
        </section>
      }

      @if (error()) {
        <div class="emr-alert error" role="alert">
          {{ error() }}
          <button class="emr-text-button" (click)="load()">Try again</button>
        </div>
      }

      @if (mode === 'patients') {
        <section class="encounters-section" aria-label="Assigned synthetic patients">
          <div class="filter-toolbar">
            <div class="encounter-search">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="10.5" cy="10.5" r="6.5" />
                <path d="m16 16 5 5" />
              </svg>
              <input
                aria-label="Search assigned patients"
                placeholder="Search by synthetic patient ID or encounter…"
                [ngModel]="query()"
                (ngModelChange)="query.set($event)"
              />
            </div>
          </div>

          @if (loading()) {
            <div class="emr-empty" role="status">
              <span class="loading-ring"></span>
              <p>Loading assigned patients…</p>
            </div>
          } @else {
            <div class="clinical-table-wrap">
              <table class="clinical-table">
                <thead>
                  <tr>
                    <th>Patient (ID)</th>
                    <th>Assigned Encounters</th>
                    <th>Latest Encounter Date</th>
                    <th>Latest Note Status</th>
                    <th>Latest Extraction</th>
                    <th class="action-column">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  @for (p of patients(); track p.patientReference) {
                    <tr class="clickable-row" (click)="open(p.latestEncounter)">
                      <td>
                        <strong class="patient-link">Synthetic Patient ({{ p.patientReference }})</strong>
                        <span class="table-secondary">{{ p.latestEncounter.reference }}</span>
                      </td>
                      <td>{{ p.encounterCount }} encounter{{ p.encounterCount === 1 ? '' : 's' }}</td>
                      <td>{{ p.latestEncounter.encounterDate | date: 'dd MMM yyyy' }}</td>
                      <td>
                        <span
                          class="status-label"
                          [class.draft]="p.latestEncounter.noteStatus === 'Draft'"
                          [class.success]="p.latestEncounter.noteStatus === 'Signed'"
                        >
                          {{ p.latestEncounter.noteStatus || 'Not started' }}
                        </span>
                      </td>
                      <td>
                        @if (p.latestEncounter.extractionStatus) {
                          <span
                            class="status-label"
                            [class.review]="p.latestEncounter.extractionStatus === 'Needs Review' || p.latestEncounter.extractionStatus === 'Pending Publication'"
                            [class.processing]="p.latestEncounter.extractionStatus === 'Processing' || p.latestEncounter.extractionStatus === 'Queued'"
                            [class.failed]="p.latestEncounter.extractionStatus === 'Failed'"
                            [class.success]="p.latestEncounter.extractionStatus === 'Completed'"
                          >
                            {{ p.latestEncounter.extractionStatus }}
                          </span>
                        } @else {
                          <span class="muted">—</span>
                        }
                      </td>
                      <td class="action-column">
                        <button
                          class="emr-button table-action"
                          (click)="$event.stopPropagation(); open(p.latestEncounter)"
                        >
                          Open
                        </button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (!patients().length) {
              <div class="emr-empty">
                <h3>No matching patients found.</h3>
                <p>Only synthetic patients with encounters assigned to your account are shown.</p>
              </div>
            }
          }
        </section>
      } @else {
        <section class="encounters-section" aria-label="Encounter workspace">
          <div class="workspace-tabs-bar" role="tablist" aria-label="Filter encounters">
            <button
              role="tab"
              [attr.aria-selected]="filter() === 'all'"
              [class.active]="filter() === 'all'"
              (click)="filter.set('all')"
            >
              My Encounters
            </button>
            <button
              role="tab"
              [attr.aria-selected]="filter() === 'review'"
              [class.active]="filter() === 'review'"
              (click)="filter.set('review')"
            >
              Notes Requiring Review ({{ awaiting() }})
            </button>
            <button
              role="tab"
              [attr.aria-selected]="filter() === 'draft'"
              [class.active]="filter() === 'draft'"
              (click)="filter.set('draft')"
            >
              Draft Notes ({{ drafts() }})
            </button>
          </div>

          <div class="filter-toolbar">
            <div class="encounter-search">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="10.5" cy="10.5" r="6.5" />
                <path d="m16 16 5 5" />
              </svg>
              <input
                aria-label="Search assigned encounters"
                placeholder="Search by patient ID, encounter ID or consultation type…"
                [ngModel]="query()"
                (ngModelChange)="query.set($event)"
              />
            </div>

            <select
              class="filter-select"
              aria-label="Filter by status"
              [ngModel]="statusFilter()"
              (ngModelChange)="statusFilter.set($event)"
            >
              <option value="all">All Status</option>
              <option value="Draft">Draft</option>
              <option value="Signed">Signed</option>
              <option value="Processing">Processing</option>
              <option value="Queued">Queued</option>
              <option value="Pending Publication">Pending Publication</option>
              <option value="Needs Review">Needs Review</option>
              <option value="Completed">Completed</option>
              <option value="Failed">Failed</option>
              <option value="Not started">Not started</option>
            </select>

            <select
              class="filter-select"
              aria-label="Filter by date range"
              [ngModel]="dateRange()"
              (ngModelChange)="dateRange.set($event)"
            >
              <option value="all">All Dates</option>
              <option value="30d">Last 30 Days</option>
              <option value="90d">Last 90 Days</option>
              <option value="365d">Last 12 Months</option>
            </select>
          </div>

          @if (loading()) {
            <div class="emr-empty" role="status">
              <span class="loading-ring"></span>
              <p>Loading your encounters…</p>
            </div>
          } @else {
            <div class="clinical-table-wrap">
              <table class="clinical-table">
                <thead>
                  <tr>
                    <th class="sortable" (click)="toggleSort('patient')">
                      Patient (ID)
                      @if (sortField() === 'patient') {
                        <span class="sort-indicator">{{ sortDir() === 'asc' ? '▲' : '▼' }}</span>
                      }
                    </th>
                    <th class="sortable" (click)="toggleSort('date')">
                      Encounter Date
                      @if (sortField() === 'date') {
                        <span class="sort-indicator">{{ sortDir() === 'asc' ? '▲' : '▼' }}</span>
                      }
                    </th>
                    <th>Consultation Type</th>
                    <th class="sortable" (click)="toggleSort('noteStatus')">
                      Note Status
                      @if (sortField() === 'noteStatus') {
                        <span class="sort-indicator">{{ sortDir() === 'asc' ? '▲' : '▼' }}</span>
                      }
                    </th>
                    <th class="sortable" (click)="toggleSort('extractionStatus')">
                      Extraction Status
                      @if (sortField() === 'extractionStatus') {
                        <span class="sort-indicator">{{ sortDir() === 'asc' ? '▲' : '▼' }}</span>
                      }
                    </th>
                    <th class="action-column">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  @for (e of visible(); track e.id) {
                    <tr class="clickable-row" (click)="open(e)">
                      <td>
                        <a
                          [routerLink]="e.noteId ? ['/emr/notes', e.noteId, 'edit'] : ['/emr/encounters', e.id, 'new']"
                          (click)="$event.stopPropagation()"
                          class="patient-link"
                        >
                          {{ e.patientReference }}
                        </a>
                        <span class="table-secondary">{{ e.reference }}</span>
                      </td>
                      <td>
                        {{ e.encounterDate | date: 'dd MMM yyyy' }}
                        @if (e.version) {
                          <span class="table-secondary">Note v{{ e.version }}</span>
                        }
                      </td>
                      <td>
                        {{ consultationType(e.context) }}
                        @if (departmentName(e.context)) {
                          <span class="table-secondary">{{ departmentName(e.context) }}</span>
                        }
                      </td>
                      <td>
                        <span
                          class="status-label"
                          [class.draft]="e.noteStatus === 'Draft'"
                          [class.success]="e.noteStatus === 'Signed'"
                        >
                          {{ e.noteStatus || 'Not started' }}
                        </span>
                      </td>
                      <td>
                        @if (e.extractionStatus) {
                          <span
                            class="status-label"
                            [class.review]="e.extractionStatus === 'Needs Review' || e.extractionStatus === 'Pending Publication'"
                            [class.processing]="e.extractionStatus === 'Processing' || e.extractionStatus === 'Queued'"
                            [class.failed]="e.extractionStatus === 'Failed'"
                            [class.success]="e.extractionStatus === 'Completed'"
                          >
                            @if (e.extractionStatus === 'Processing') {
                              <span class="status-spinner" aria-hidden="true"></span>
                            }
                            {{ e.extractionStatus }}
                          </span>
                        } @else {
                          <span class="muted" aria-label="No extraction">—</span>
                        }
                      </td>
                      <td class="action-column">
                        <div class="row-actions">
                          @if (e.extractionId && mode !== 'extractions') {
                            <a
                              class="emr-button table-action"
                              routerLink="/extraction/result"
                              [queryParams]="{
                                extractionId: e.extractionId,
                                noteId: e.noteId,
                                patient: e.patientReference,
                                encounter: e.reference,
                                status: e.extractionStatus || 'Completed'
                              }"
                              (click)="$event.stopPropagation()"
                            >
                              Results
                            </a>
                          }
                          <button
                            class="emr-button table-action"
                            (click)="$event.stopPropagation(); open(e)"
                            [attr.aria-label]="'Open encounter for ' + e.patientReference"
                          >
                            Open
                          </button>
                        </div>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>

            @if (!visible().length) {
              <div class="emr-empty">
                <div class="empty-document">
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6M9 16h6" />
                  </svg>
                </div>
                <h3>{{ encounters().length ? 'No matching clinical encounters.' : 'No assigned encounters yet.' }}</h3>
                <p>
                  {{
                    query() || statusFilter() !== 'all' || dateRange() !== 'all' || filter() !== 'all'
                      ? 'Adjust your search query or filters to view assigned encounters.'
                      : 'Open an assigned encounter to start documenting a structured clinical note.'
                  }}
                </p>
                @if (filter() !== 'all' || query() || statusFilter() !== 'all' || dateRange() !== 'all') {
                  <button
                    class="emr-button secondary"
                    (click)="clearFilters()"
                  >
                    Clear filters
                  </button>
                }
              </div>
            }
          }

          <div class="table-footnote">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M12 3 4 6v6c0 4 8 9 8 9s8-5 8-9V6z" />
            </svg>
            <span>Only outpatient encounters assigned to your account are shown. All patient records are synthetic.</span>
          </div>
        </section>
      }
    </main>

    <dialog #encounterDialog class="emr-dialog">
      <form (submit)="$event.preventDefault(); startSelected()">
        <p class="emr-eyebrow">NEW CLINICAL NOTE</p>
        <h2>Select an assigned encounter</h2>
        <p>Every clinical note must be associated with an authorized outpatient encounter.</p>
        <label for="encounter-choice">Assigned encounter</label>
        <select id="encounter-choice" name="encounter" [(ngModel)]="selectedEncounter">
          <option value="" disabled>Select an encounter…</option>
          @for (e of encounters(); track e.id) {
            <option [value]="e.id">
              {{ e.patientReference }} · {{ e.reference }} · {{ e.encounterDate | date: 'dd MMM yyyy' }} · {{ e.noteStatus || 'Not started' }}
            </option>
          }
        </select>
        <div class="dialog-actions">
          <button
            class="emr-button secondary"
            type="button"
            (click)="dialog()?.nativeElement?.close()"
          >
            Cancel
          </button>
          <button class="emr-button primary" type="submit" [disabled]="!selectedEncounter">
            Open encounter
          </button>
        </div>
      </form>
    </dialog>
  `,
})
export class DoctorDashboard implements OnInit {
  private api = inject(EmrApi);
  private router = inject(Router);
  private route = inject(ActivatedRoute);

  readonly mode = this.route.snapshot.data['mode'] || 'dashboard';

  title = computed(() => {
    switch (this.mode) {
      case 'patients':
        return 'Patients';
      case 'encounters':
        return 'Encounters';
      case 'notes':
        return 'Clinical Notes';
      case 'tasks':
        return 'My Tasks';
      case 'extractions':
        return 'Clinical Extraction';
      case 'review':
        return 'Review & Validation';
      case 'history':
        return 'Extraction History';
      default:
        return 'Doctor Dashboard';
    }
  });

  subtitle = computed(() => {
    switch (this.mode) {
      case 'patients':
        return 'Synthetic outpatient records assigned to your clinical practice.';
      case 'encounters':
        return 'All assigned outpatient consultations and documentation statuses.';
      case 'notes':
        return 'Draft and signed structured clinical notes across your encounters.';
      case 'tasks':
        return 'Open drafts, pending extractions, and notes requiring your review.';
      case 'extractions':
        return 'Source-grounded AI extraction results from your signed clinical notes.';
      case 'review':
        return 'Extracted clinical entities awaiting review across your assigned encounters.';
      case 'history':
        return 'Complete audit history of your signed clinical notes and extractions.';
      default:
        return 'Outpatient encounters and clinical documentation.';
    }
  });

  encounters = signal<Encounter[]>([]);
  loading = signal(true);
  error = signal('');
  query = signal('');
  filter = signal<'all' | 'draft' | 'review'>('all');
  statusFilter = signal('all');
  dateRange = signal<'all' | '30d' | '90d' | '365d'>('all');
  sortField = signal<'date' | 'patient' | 'noteStatus' | 'extractionStatus'>('date');
  sortDir = signal<'asc' | 'desc'>('desc');
  selectedEncounter = '';

  dialog = viewChild<ElementRef<HTMLDialogElement>>('encounterDialog');

  drafts = computed(() => this.encounters().filter((e) => e.noteStatus === 'Draft').length);
  signedNotes = computed(() => this.encounters().filter((e) => e.noteStatus === 'Signed').length);
  awaiting = computed(
    () => this.encounters().filter((e) => e.extractionStatus === 'Needs Review').length,
  );
  awaitingExtraction = computed(
    () =>
      this.encounters().filter(
        (e) =>
          e.noteStatus === 'Signed' &&
          (!e.extractionStatus ||
            ['Pending Publication', 'Queued', 'Processing'].includes(e.extractionStatus)),
      ).length,
  );
  completed = computed(
    () =>
      this.encounters().filter((e) =>
        ['Completed', 'Needs Review'].includes(e.extractionStatus || ''),
      ).length,
  );

  patients = computed<PatientSummary[]>(() => {
    const q = this.query().trim().toLowerCase();
    const map = new Map<string, Encounter[]>();
    for (const e of this.encounters()) {
      const list = map.get(e.patientReference) ?? [];
      list.push(e);
      map.set(e.patientReference, list);
    }
    const items: PatientSummary[] = [];
    for (const [patientReference, list] of map.entries()) {
      const sorted = [...list].sort(
        (a, b) => new Date(b.encounterDate).getTime() - new Date(a.encounterDate).getTime(),
      );
      if (
        q &&
        !patientReference.toLowerCase().includes(q) &&
        !list.some((x) => x.reference.toLowerCase().includes(q))
      ) {
        continue;
      }
      items.push({
        patientReference,
        encounterCount: list.length,
        latestEncounter: sorted[0],
        signedCount: list.filter((x) => x.noteStatus === 'Signed').length,
        draftCount: list.filter((x) => x.noteStatus === 'Draft').length,
      });
    }
    return items.sort((a, b) => a.patientReference.localeCompare(b.patientReference));
  });

  visible = computed(() => {
    const q = this.query().trim().toLowerCase();
    const st = this.statusFilter();
    const range = this.dateRange();
    const now = Date.now();
    const rangeMs =
      range === '30d'
        ? 30 * 86400000
        : range === '90d'
          ? 90 * 86400000
          : range === '365d'
            ? 365 * 86400000
            : null;

    const filtered = this.encounters().filter((e) => {
      if (this.mode === 'extractions' && !e.extractionStatus) return false;
      if (this.mode === 'history' && !e.noteId) return false;
      if (this.mode === 'notes' && !e.noteId) return false;
      if (this.mode === 'review' && e.extractionStatus !== 'Needs Review') return false;
      if (
        this.mode === 'tasks' &&
        !(
          e.noteStatus === 'Draft' ||
          e.extractionStatus === 'Needs Review' ||
          e.extractionStatus === 'Processing' ||
          e.extractionStatus === 'Pending Publication' ||
          e.extractionStatus === 'Failed'
        )
      ) {
        return false;
      }

      if (this.filter() === 'draft' && e.noteStatus !== 'Draft') return false;
      if (this.filter() === 'review' && e.extractionStatus !== 'Needs Review') return false;

      if (st !== 'all') {
        if (st === 'Not started' && e.noteStatus) return false;
        else if (st === 'Draft' || st === 'Signed') {
          if (e.noteStatus !== st) return false;
        } else if (e.extractionStatus !== st) {
          return false;
        }
      }

      if (rangeMs !== null) {
        const ts = new Date(e.encounterDate).getTime();
        if (!Number.isNaN(ts) && now - ts > rangeMs) return false;
      }

      if (
        q &&
        ![e.patientReference, e.reference, e.context].some((s) =>
          (s || '').toLowerCase().includes(q),
        )
      ) {
        return false;
      }

      return true;
    });

    const field = this.sortField();
    const dir = this.sortDir() === 'asc' ? 1 : -1;
    return [...filtered].sort((a, b) => {
      if (field === 'patient') return a.patientReference.localeCompare(b.patientReference) * dir;
      if (field === 'noteStatus')
        return (a.noteStatus || '').localeCompare(b.noteStatus || '') * dir;
      if (field === 'extractionStatus')
        return (a.extractionStatus || '').localeCompare(b.extractionStatus || '') * dir;
      return (new Date(a.encounterDate).getTime() - new Date(b.encounterDate).getTime()) * dir;
    });
  });

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      this.encounters.set(await this.api.encounters());
    } catch (e) {
      this.error.set(apiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  consultationType(context: string): string {
    const parts = (context || '').split(' · ');
    return parts.length > 1 ? parts[1] : parts[0] || 'OP Consultation';
  }

  departmentName(context: string): string {
    const parts = (context || '').split(' · ');
    return parts.length > 1 ? parts[0] : '';
  }

  toggleSort(field: 'date' | 'patient' | 'noteStatus' | 'extractionStatus'): void {
    if (this.sortField() === field) {
      this.sortDir.set(this.sortDir() === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortField.set(field);
      this.sortDir.set(field === 'date' ? 'desc' : 'asc');
    }
  }

  clearFilters(): void {
    this.filter.set('all');
    this.query.set('');
    this.statusFilter.set('all');
    this.dateRange.set('all');
  }

  chooseEncounter(): void {
    if (!this.encounters().length) {
      this.error.set(
        'No encounters are assigned to this account. Ask your clinical coordinator to assign one.',
      );
      return;
    }
    this.dialog()?.nativeElement.showModal();
  }

  startSelected(): void {
    const e = this.encounters().find((item) => item.id === this.selectedEncounter);
    if (e) {
      this.dialog()?.nativeElement.close();
      this.open(e);
    }
  }

  open(e: Encounter): void {
    if (e.extractionId && (this.mode === 'extractions' || this.mode === 'review')) {
      void this.router.navigate(['/extraction/result'], {
        queryParams: {
          extractionId: e.extractionId,
          noteId: e.noteId,
          patient: e.patientReference,
          encounter: e.reference,
          status: e.extractionStatus || 'Completed',
        },
      });
    } else if (e.noteId) {
      void this.router.navigate([
        '/emr/notes',
        e.noteId,
        e.noteStatus === 'Signed' && this.mode !== 'notes' ? 'status' : 'edit',
      ]);
    } else {
      void this.router.navigate(['/emr/encounters', e.id, 'new']);
    }
  }
}

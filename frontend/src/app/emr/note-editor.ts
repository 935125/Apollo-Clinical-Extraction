import {
  Component,
  computed,
  ElementRef,
  HostListener,
  inject,
  OnInit,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ClinicalNote, EmrApi, Encounter, NoteVersion, apiError } from './emr-api';
import {
  CLINICAL_SECTIONS,
  ClinicalSectionValues,
  emptySectionValues,
  parseClinicalNote,
  populatedSectionsCount,
  serializeClinicalSections,
} from './clinical-note-sections';

@Component({
  selector: 'clinical-note-editor',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink],
  template: `
    <main class="emr-page note-page">
      <div class="note-header-bar">
        <div>
          <div class="note-header-title-row">
            <a routerLink="/emr" class="back-icon-btn" aria-label="Back to Doctor Dashboard">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="m14 6-6 6 6 6M8 12h13" />
              </svg>
            </a>
            <h1>
              {{
                note()?.status === 'Signed'
                  ? 'Signed Clinical Note'
                  : note()
                    ? 'Edit Draft'
                    : 'New Clinical Note'
              }}
            </h1>
          </div>

          @if (context(); as c) {
            <div class="note-meta-line">
              <span>Encounter: <strong>{{ c.reference }}</strong></span>
              <span class="meta-sep">|</span>
              <span>Patient: <strong>{{ c.patientReference }}</strong></span>
              <span class="meta-sep">|</span>
              <span>Date: <strong>{{ c.encounterDate | date: 'dd MMM yyyy' }}</strong></span>
              <span class="meta-sep">|</span>
              <span>{{ c.context || 'OP Consultation' }}</span>
              <span
                class="status-label"
                [class.draft]="note()?.status !== 'Signed'"
                [class.success]="note()?.status === 'Signed'"
              >
                {{ note()?.status || 'Unsaved Draft' }}
              </span>
              @if (note()) {
                <span class="secondary-meta">v{{ note()!.version }}</span>
              }
            </div>
          }
        </div>

        <div class="note-actions">
          @if (note()?.status !== 'Signed') {
            <button
              class="emr-button secondary"
              type="button"
              (click)="save()"
              [disabled]="busy() || !text().trim()"
            >
              {{ busy() && action() === 'save' ? 'Saving…' : 'Save Draft' }}
            </button>
            <button
              class="emr-button primary sign-button"
              type="button"
              (click)="confirmSign()"
              [disabled]="busy() || !text().trim()"
            >
              <svg
                viewBox="0 0 24 24"
                aria-hidden="true"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                class="sign-icon"
              >
                <path d="M17 3a2.828 2.828 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
                <path d="m15 5 4 4" />
              </svg>
              {{ busy() && action() === 'sign' ? 'Signing…' : 'Sign Note' }}
            </button>
          } @else {
            <button
              class="emr-button secondary"
              type="button"
              (click)="correction()"
              [disabled]="busy()"
            >
              Create Correction
            </button>
            @if (note()?.extractionId) {
              <a
                class="emr-button primary"
                routerLink="/extraction/result"
                [queryParams]="{
                  extractionId: note()!.extractionId,
                  noteId: note()!.id,
                  patient: note()!.patientReference,
                  encounter: note()!.encounterReference,
                  status: note()!.extractionStatus || 'Completed'
                }"
              >
                Extraction Result
              </a>
            } @else {
              <a class="emr-button primary" [routerLink]="['/emr/notes', note()!.id, 'status']">
                View Extraction Status
              </a>
            }
          }
        </div>
      </div>

      @if (loading()) {
        <div class="emr-empty" role="status">
          <span class="loading-ring"></span>
          <p>Loading clinical note…</p>
        </div>
      }

      @if (error()) {
        <div class="emr-alert error" role="alert">
          {{ error() }}
          @if (!api.doctor()) {
            <a routerLink="/login">Sign in again</a>
          }
        </div>
      }

      @if (success()) {
        <div class="emr-alert success" role="status">{{ success() }}</div>
      }

      @if (context()) {
        <div class="structured-editor-layout">
          <nav class="section-nav-rail" aria-label="Clinical note sections">
            @for (section of sectionDefs; track section.id) {
              <button
                type="button"
                class="section-nav-item"
                [class.active]="activeSection() === section.id"
                [attr.aria-current]="activeSection() === section.id ? 'true' : null"
                (click)="focusSection(section.id)"
              >
                <span>{{ section.label }}</span>
                @if (sectionValues()[section.id]?.trim()) {
                  <span class="section-dot" title="Section documented" aria-hidden="true"></span>
                }
              </button>
            }
          </nav>

          <section class="section-editor-surface" aria-label="Structured clinical documentation">
            @if (note()?.status === 'Signed') {
              <div class="signed-readonly-banner" role="status">
                <span>
                  <strong>Immutable Signed Version (v{{ note()!.version }}):</strong> This record is
                  locked for audit integrity. Use <em>Create Correction</em> to create a new draft
                  version without altering historical clinical records.
                </span>
                @if (note()?.signedAt) {
                  <span>Signed {{ note()!.signedAt | date: 'dd MMM yyyy, HH:mm' }}</span>
                }
              </div>
            }

            @for (section of sectionDefs; track section.id) {
              <div class="clinical-section-block" [id]="'section-block-' + section.id">
                <label class="clinical-section-label" [for]="'section-' + section.id">
                  <span>{{ section.label }}</span>
                </label>
                <textarea
                  [id]="'section-' + section.id"
                  class="clinical-section-textarea"
                  [attr.data-section]="section.id"
                  [rows]="section.rows"
                  [ngModel]="sectionValues()[section.id]"
                  (ngModelChange)="updateSection(section.id, $event)"
                  (focus)="activeSection.set(section.id)"
                  [readOnly]="note()?.status === 'Signed'"
                  [placeholder]="section.placeholder"
                  spellcheck="false"
                ></textarea>
              </div>
            }

            <div class="editor-footer-bar">
              <div class="editor-footer-left">
                <span>
                  {{ completedSectionCount() }} / {{ sectionDefs.length }} sections documented ·
                  {{ words() }} words · {{ text().length | number }} characters
                </span>
                @if (note()?.status !== 'Signed') {
                  <button
                    type="button"
                    class="emr-text-button"
                    (click)="pdfInput.click()"
                    [disabled]="busy()"
                  >
                    <svg viewBox="0 0 24 24" aria-hidden="true" width="15" height="15">
                      <path d="M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6" />
                    </svg>
                    Import text-based PDF
                  </button>
                  <input
                    #pdfInput
                    type="file"
                    accept="application/pdf,.pdf"
                    class="sr-only"
                    aria-label="Upload text-based PDF"
                    (change)="upload($event)"
                  />
                }
              </div>

              <span>
                {{
                  note()?.status === 'Signed'
                    ? 'Signed version · Read only'
                    : dirty()
                      ? 'Unsaved changes'
                      : note()
                        ? 'All changes saved'
                        : 'Ready to document'
                }}
              </span>
            </div>

            @if (note()) {
              <details class="version-history" (toggle)="loadVersions($event)">
                <summary>Version history (immutable snapshots)</summary>
                @for (v of versions(); track v.version) {
                  <details>
                    <summary>
                      Version {{ v.version }} · {{ v.signedAt ? 'Signed' : 'Draft snapshot' }} ·
                      {{ v.createdAt | date: 'dd MMM yyyy, HH:mm' }}
                    </summary>
                    <pre>{{ v.sourceText }}</pre>
                    <span class="hash-meta">SHA-256: {{ v.sourceHash }}</span>
                  </details>
                }
              </details>
            }
          </section>
        </div>
      }
    </main>

    <dialog #signDialog class="emr-dialog">
      <p class="emr-eyebrow">SIGN CLINICAL NOTE</p>
      <h2>Ready to sign this clinical note?</h2>
      <p>
        Signing creates an immutable version snapshot and atomically queues clinical extraction via
        Azure Service Bus.
      </p>

      @if (populatedSectionLabels().length > 0) {
        <div class="sign-section-summary">
          <strong>Sections included ({{ populatedSectionLabels().length }}):</strong>
          <ul>
            @for (label of populatedSectionLabels(); track label) {
              <li>{{ label }}</li>
            }
          </ul>
        </div>
      }

      <div class="sign-notice">
        Signing the note does not approve AI-generated prescriptions or investigation orders.
        Extraction results require human review.
      </div>
      <div class="dialog-actions">
        <button
          class="emr-button secondary"
          type="button"
          (click)="dialog()?.nativeElement?.close()"
        >
          Continue editing
        </button>
        <button class="emr-button primary" type="button" (click)="sign()" [disabled]="busy()">
          Sign &amp; Submit
        </button>
      </div>
    </dialog>
  `,
})
export class NoteEditor implements OnInit {
  readonly api = inject(EmrApi);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  readonly sectionDefs = CLINICAL_SECTIONS;
  readonly sectionValues = signal<ClinicalSectionValues>(emptySectionValues());
  readonly activeSection = signal<string>(CLINICAL_SECTIONS[0].id);

  note = signal<ClinicalNote | null>(null);
  encounter = signal<Encounter | null>(null);
  text = signal('');
  loading = signal(true);
  busy = signal(false);
  error = signal('');
  success = signal('');
  action = signal('');
  versions = signal<NoteVersion[]>([]);
  private persisted = signal('');

  dialog = viewChild<ElementRef<HTMLDialogElement>>('signDialog');

  words = computed(() => (this.text().trim() ? this.text().trim().split(/\s+/).length : 0));
  dirty = computed(() => this.text() !== this.persisted());
  completedSectionCount = computed(() => populatedSectionsCount(this.sectionValues()));
  populatedSectionLabels = computed(() =>
    CLINICAL_SECTIONS.filter((s) => !!this.sectionValues()[s.id]?.trim()).map((s) => s.label),
  );

  context = computed(() => {
    const n = this.note(),
      e = this.encounter();
    return n
      ? {
          patientReference: n.patientReference,
          reference: n.encounterReference,
          encounterDate: n.encounterDate,
          context: n.context,
        }
      : e;
  });

  async ngOnInit(): Promise<void> {
    try {
      const id = this.route.snapshot.paramMap.get('id')!;
      if (this.route.snapshot.data['new']) {
        this.encounter.set(await this.api.encounter(id));
      } else {
        this.accept(await this.api.note(id));
      }
      await this.api.csrf();
    } catch (e) {
      this.error.set(apiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  private accept(n: ClinicalNote): void {
    this.note.set(n);
    const parsed = parseClinicalNote(n.text);
    this.sectionValues.set(parsed.values);
    // Preserve exact persisted text to avoid spurious dirty flags on legacy unstructured notes
    this.persisted.set(n.text);
    this.text.set(n.text);

    const firstPopulated = CLINICAL_SECTIONS.find((s) => !!parsed.values[s.id]?.trim());
    if (firstPopulated) {
      this.activeSection.set(firstPopulated.id);
    }
  }

  updateSection(sectionId: string, value: string): void {
    this.sectionValues.update((curr) => {
      const next = { ...curr, [sectionId]: value };
      this.text.set(serializeClinicalSections(next));
      return next;
    });
    this.success.set('');
  }

  changeText(text: string): void {
    this.text.set(text);
    const parsed = parseClinicalNote(text);
    this.sectionValues.set(parsed.values);
    this.success.set('');
  }

  focusSection(sectionId: string): void {
    this.activeSection.set(sectionId);
    if (typeof document !== 'undefined') {
      if (sectionId === CLINICAL_SECTIONS[0].id) {
        window.scrollTo({ top: 0, behavior: 'smooth' });
      } else {
        const block = document.getElementById('section-block-' + sectionId);
        block?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
      const input = document.getElementById('section-' + sectionId) as HTMLTextAreaElement | null;
      input?.focus({ preventScroll: true });
    }
  }

  async persist(): Promise<ClinicalNote> {
    const n = this.note();
    const payload = this.text();
    const updated = n
      ? await this.api.save(n, payload)
      : await this.api.create(this.encounter()!.id, payload);
    this.accept(updated);
    return updated;
  }

  async save(): Promise<void> {
    if (this.busy()) return;
    if (!this.text().trim()) {
      this.error.set('Document at least one clinical section before saving a draft.');
      return;
    }
    this.busy.set(true);
    this.action.set('save');
    this.error.set('');
    try {
      await this.persist();
      this.success.set('Structured draft saved. All sections will persist across sessions.');
    } catch (e) {
      this.error.set(apiError(e));
    } finally {
      this.busy.set(false);
    }
  }

  confirmSign(): void {
    this.error.set('');
    if (!this.text().trim()) {
      this.error.set('Document at least one clinical section before signing this note.');
      return;
    }
    this.dialog()?.nativeElement.showModal();
  }

  async sign(): Promise<void> {
    if (this.busy()) return;
    this.dialog()?.nativeElement.close();
    this.busy.set(true);
    this.action.set('sign');
    this.error.set('');
    try {
      const n = await this.persist();
      const signed = await this.api.sign(n);
      this.accept(signed);
      await this.router.navigate(['/emr/notes', signed.id, 'status']);
    } catch (e) {
      this.error.set(apiError(e));
    } finally {
      this.busy.set(false);
    }
  }

  async correction(): Promise<void> {
    if (this.busy() || !this.note()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      this.accept(await this.api.correct(this.note()!));
      this.success.set(
        'A new editable draft version has been created. Previous signed versions remain immutable in version history.',
      );
    } catch (e) {
      this.error.set(apiError(e));
    } finally {
      this.busy.set(false);
    }
  }

  async upload(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement,
      file = input.files?.[0];
    if (!file) return;
    input.value = '';
    if (file.size > 20 * 1024 * 1024 || !file.name.toLowerCase().endsWith('.pdf')) {
      this.error.set('Choose a PDF of 20 MiB or smaller.');
      return;
    }
    if (!window.confirm('Importing this PDF will replace the current draft text. Continue?')) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const n = this.note();
      this.accept(
        n ? await this.api.upload(n, file) : await this.api.uploadNew(this.encounter()!.id, file),
      );
      this.success.set('PDF text imported and saved as a new draft version. Review it before signing.');
    } catch (e) {
      this.error.set(apiError(e));
    } finally {
      this.busy.set(false);
    }
  }

  async loadVersions(event: Event): Promise<void> {
    if ((event.target as HTMLDetailsElement).open && this.note()) {
      try {
        this.versions.set(await this.api.versions(this.note()!.id));
      } catch (e) {
        this.error.set(apiError(e));
      }
    }
  }

  canLeave(): boolean {
    return !this.dirty() || window.confirm('Leave this note? Your unsaved changes will be lost.');
  }

  @HostListener('window:beforeunload', ['$event'])
  beforeUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }
}

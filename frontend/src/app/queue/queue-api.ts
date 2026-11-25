import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom, Observable } from 'rxjs';
import { ClinicalExtractionResponse } from '../models/extraction.model';
import { ClinicalNote, Doctor, EmrApi, Encounter } from '../emr/emr-api';

export type QueueState = 'Waiting' | 'Queued' | 'Extracting' | 'Ready' | 'Failed';

export interface QueueItem {
  patientId?: string;
  encounterId?: string;
  noteId: string;
  noteVersion: number;
  patientReference: string;
  encounterReference: string;
  encounterDate: string;
  receivedAt: string;
  sourcePreview: string;
  wordCount: number;
  noteStatus: string;
  queueState: QueueState;
  verificationRecommended: boolean;
  rawProcessingStatus: string | null;
  eventId?: string | null;
  extractionId: string | null;
  provider: string | null;
  model: string | null;
  fallbackTriggered: boolean;
  warningCount: number | null;
  errorCount: number | null;
  error: string | null;
  patientNoteCount?: number;
}

export interface QueueListResponse {
  items: QueueItem[];
  total: number;
  serverTimeUtc: string;
  requestedPrimaryProvider: string;
  fallbackProvider: string;
}

export interface PatientNoteOption {
  noteId: string;
  encounterId: string;
  encounterReference: string;
  noteVersion: number;
  receivedAt: string;
  noteStatus: string;
  queueState: QueueState;
  extractionId: string | null;
}

export interface QueueEntryDetail {
  patientId?: string;
  noteId: string;
  encounterId: string;
  noteVersion: number;
  patientReference: string;
  encounterReference: string;
  encounterDate: string;
  context: string;
  noteStatus: string;
  sourceText: string;
  sourceHash: string;
  signedAt: string | null;
  updatedAt: string;
  queueState: QueueState;
  verificationRecommended: boolean;
  rawProcessingStatus: string | null;
  eventId?: string | null;
  extractionId?: string | null;
  error: string | null;
  extraction: ClinicalExtractionResponse | null;
  patientNotes?: PatientNoteOption[] | null;
}

export interface AuthorizedSessionResult {
  authorized: boolean;
  doctor: Doctor | null;
  reason?: string;
}

@Injectable({ providedIn: 'root' })
export class QueueApiService {
  private readonly http = inject(HttpClient);
  private readonly emrApi = inject(EmrApi);

  readonly authorizedDoctor = signal<Doctor | null>(null);
  readonly authFailureReason = signal<string | null>(null);

  /**
   * Ensures an authorized clinician session exists without embedding credentials.
   * Development loopback calls POST /api/v1/emr/auth/dev-session directly when no
   * XSRF-TOKEN cookie is present, failing closed outside Development loopback.
   */
  async ensureAuthorizedSession(): Promise<AuthorizedSessionResult> {
    this.authFailureReason.set(null);
    if (this.emrApi.doctor()) {
      const existing = this.emrApi.doctor()!;
      this.authorizedDoctor.set(existing);
      return { authorized: true, doctor: existing };
    }

    const hasSessionHint =
      typeof document !== 'undefined' && /(?:^|; )XSRF-TOKEN=/.test(document.cookie);

    if (hasSessionHint) {
      const existing = await this.emrApi.session();
      if (existing) {
        this.authorizedDoctor.set(existing);
        return { authorized: true, doctor: existing };
      }
    }

    try {
      const doctor = await firstValueFrom(
        this.http.post<Doctor>('/api/v1/emr/auth/dev-session', {})
      );
      this.emrApi.doctor.set(doctor);
      this.authorizedDoctor.set(doctor);
      return { authorized: true, doctor };
    } catch (err: unknown) {
      if (!hasSessionHint) {
        const existing = await this.emrApi.session();
        if (existing) {
          this.authorizedDoctor.set(existing);
          return { authorized: true, doctor: existing };
        }
      }
      const detail =
        (err as { error?: { detail?: string } })?.error?.detail ||
        'Authorized clinical session required. Automatic local session is only available in Development on localhost.';
      this.authorizedDoctor.set(null);
      this.authFailureReason.set(detail);
      return { authorized: false, doctor: null, reason: detail };
    }
  }

  listQueue(options?: {
    search?: string;
    limit?: number;
    offset?: number;
  }): Observable<QueueListResponse> {
    let params = new HttpParams();
    if (options?.search?.trim()) params = params.set('search', options.search.trim());
    if (options?.limit != null) params = params.set('limit', String(options.limit));
    if (options?.offset != null) params = params.set('offset', String(options.offset));
    return this.http.get<QueueListResponse>('/api/v1/queue', { params });
  }

  /**
   * Fetches strictly scoped detail for a single patient note & optional immutable version.
   * Passing patientReference allows the backend to reject any mismatched patient/note pair.
   */
  getQueueEntry(
    noteId: string,
    options?: { version?: number; patientReference?: string }
  ): Observable<QueueEntryDetail> {
    let params = new HttpParams();
    if (options?.version != null && options.version > 0) {
      params = params.set('version', String(options.version));
    }
    if (options?.patientReference?.trim()) {
      params = params.set('patient', options.patientReference.trim());
    }
    return this.http.get<QueueEntryDetail>(
      `/api/v1/queue/${encodeURIComponent(noteId)}`,
      { params }
    );
  }

  getFhirBundle(extractionId: string): Observable<Record<string, unknown>> {
    return this.http.get<Record<string, unknown>>(
      `/api/v1/extractions/${encodeURIComponent(extractionId)}/fhir`
    );
  }

  listEncounters(): Promise<Encounter[]> {
    return this.emrApi.encounters();
  }

  /**
   * Development-only test note submission through the same authorized EMR persistence
   * + transactional outbox + Azure Service Bus API used by Swagger and Postman.
   */
  async submitClinicalNote(encounter: Encounter, noteText: string): Promise<ClinicalNote> {
    await this.emrApi.csrf();
    let draft: ClinicalNote;
    if (!encounter.noteId) {
      draft = await this.emrApi.create(encounter.id, noteText);
    } else {
      const current = await this.emrApi.note(encounter.noteId);
      if (current.status === 'Draft') {
        draft = await this.emrApi.save(current, noteText);
      } else {
        draft = await firstValueFrom(
          this.http.post<ClinicalNote>(`/api/v1/emr/notes/${current.id}/corrections`, {
            text: noteText,
            expectedVersion: current.version,
          })
        );
      }
    }
    return this.emrApi.sign(draft);
  }
}

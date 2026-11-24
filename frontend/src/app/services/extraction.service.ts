import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';
import {
  ClinicalExtractionRequest,
  ClinicalExtractionResponse,
  EntityReviewRecord,
  EntityReviewSubmissionRequest,
  ProblemDetails,
} from '../models/extraction.model';

@Injectable({
  providedIn: 'root',
})
export class ExtractionService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = '';

  extract(request: ClinicalExtractionRequest): Observable<ClinicalExtractionResponse> {
    return this.http.post<ClinicalExtractionResponse>(`${this.apiBase}/api/v1/extractions`, request).pipe(
      catchError((error: HttpErrorResponse) => this.handleHttpError(error))
    );
  }

  listExtractions(limit = 50): Observable<ClinicalExtractionResponse[]> {
    return this.http.get<ClinicalExtractionResponse[]>(`${this.apiBase}/api/v1/extractions?limit=${limit}`).pipe(
      catchError(() => of([]))
    );
  }

  getExtractionById(id: string): Observable<ClinicalExtractionResponse> {
    return this.http.get<ClinicalExtractionResponse>(`${this.apiBase}/api/v1/extractions/${id}`).pipe(
      catchError((error: HttpErrorResponse) => this.handleHttpError(error))
    );
  }

  listEntityReviews(extractionId: string): Observable<EntityReviewRecord[]> {
    return this.http.get<EntityReviewRecord[]>(`${this.apiBase}/api/v1/extractions/${extractionId}/reviews`).pipe(
      catchError(() => of([]))
    );
  }

  submitEntityReview(extractionId: string, request: EntityReviewSubmissionRequest): Observable<EntityReviewRecord> {
    return this.http.post<EntityReviewRecord>(`${this.apiBase}/api/v1/extractions/${extractionId}/reviews`, request).pipe(
      catchError((error: HttpErrorResponse) => this.handleHttpError(error))
    );
  }

  getFhirBundle(id: string): Observable<Record<string, any>> {
    return this.http.get<Record<string, any>>(`${this.apiBase}/api/v1/extractions/${id}/fhir`).pipe(
      catchError((error: HttpErrorResponse) => this.handleHttpError(error))
    );
  }

  listQuarantineItems(status?: string): Observable<any[]> {
    const query = status ? `?status=${encodeURIComponent(status)}` : '';
    return this.http.get<any[]>(`${this.apiBase}/api/v1/quarantine${query}`).pipe(
      catchError(() => of([]))
    );
  }

  searchSemantic(query: string, limit = 10): Observable<any[]> {
    return this.http.get<any[]>(`${this.apiBase}/api/v1/extractions/search?q=${encodeURIComponent(query)}&limit=${limit}`).pipe(
      catchError(() => of([]))
    );
  }

  extractPdf(file: File): Observable<{ response: ClinicalExtractionResponse; extractedText?: string }> {
    const formData = new FormData();
    formData.append('file', file, file.name);

    return this.http.post<ClinicalExtractionResponse>(`${this.apiBase}/api/v1/extractions/pdf`, formData).pipe(
      map((response) => ({ response, extractedText: response.noteText ?? undefined })),
      catchError((error: HttpErrorResponse) => this.handleHttpError(error))
    );
  }

  checkHealth(): Observable<{ status: string; version?: string }> {
    return this.http.get<{ status: string; version?: string }>(`${this.apiBase}/v1/health`).pipe(
      catchError(() => of({ status: 'offline' }))
    );
  }

  private handleHttpError(error: HttpErrorResponse): Observable<never> {
    let problem: ProblemDetails = {
      title: 'Extraction Request Failed',
      detail: 'An unexpected network error occurred while communicating with ASP.NET Core.',
      status: error.status,
    };

    const rawErr = error.error;
    const isParseFailure =
      rawErr instanceof Error ||
      (rawErr && typeof rawErr === 'object' && 'error' in rawErr && 'text' in rawErr) ||
      error.status === 200;

    if (isParseFailure) {
      problem = {
        title: 'Malformed server response',
        detail: 'The extraction service returned an unreadable response. Your note is retained; please try again.',
        status: error.status,
        code: 'MALFORMED_RESPONSE',
      };
    } else if (rawErr && typeof rawErr === 'object') {
      const code = rawErr.code || rawErr.error_code || rawErr.extensions?.code;
      let title = rawErr.title || rawErr.error;
      let detail = rawErr.detail || rawErr.message;

      if (code === 'CLINICAL_ACCESS_DENIED' || error.status === 403) {
        title = title || 'Direct Extraction Restricted';
        detail = detail || 'Direct extraction is reserved for clinical review staff. Outpatient clinical notes must be documented and signed within assigned encounters in OP EMR.';
      } else if (code === 'CSRF_INVALID' || (error.status === 400 && code === 'CSRF_INVALID')) {
        title = title || 'Security Token Expired';
        detail = detail || 'Your verification token has expired or is invalid. Please reload the page.';
      } else {
        title = title || problem.title;
        detail = detail || error.message;
      }

      problem = {
        title,
        detail,
        status: error.status,
        code,
        correlationId: rawErr.correlationId || rawErr.extensions?.correlationId,
      };
    }

    return throwError(() => problem);
  }
}

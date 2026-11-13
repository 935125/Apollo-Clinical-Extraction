import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, throwError } from 'rxjs';
import { ClinicalExtractionRequest, ClinicalExtractionResponse, ProblemDetails } from '../models/extraction.model';

@Injectable({
  providedIn: 'root'
})
export class ExtractionService {
  private readonly http = inject(HttpClient);
  // Default to local ASP.NET Core Web API port
  private readonly baseUrl = 'http://localhost:5000/api/v1';

  extract(request: ClinicalExtractionRequest): Observable<ClinicalExtractionResponse> {
    return this.http.post<ClinicalExtractionResponse>(`${this.baseUrl}/extractions`, request).pipe(
      catchError((error: HttpErrorResponse) => {
        let problem: ProblemDetails = {
          title: 'Extraction Request Failed',
          detail: 'An unexpected network error occurred while communicating with ASP.NET Core.',
          status: error.status
        };

        if (error.error && typeof error.error === 'object') {
          problem = {
            title: error.error.title || problem.title,
            detail: error.error.detail || error.message,
            status: error.status,
            code: error.error.code || error.error.extensions?.code,
            correlationId: error.error.correlationId || error.error.extensions?.correlationId
          };
        }

        return throwError(() => problem);
      })
    );
  }
}


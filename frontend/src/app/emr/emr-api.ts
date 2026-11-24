import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Router, CanActivateFn, CanDeactivateFn } from '@angular/router';
import { firstValueFrom, catchError, throwError } from 'rxjs';

export interface Doctor { id: string; login: string; displayName: string; role: string; }
export interface Encounter { id: string; reference: string; patientReference: string; encounterDate: string; context: string; noteId: string | null; version: number | null; noteStatus: string | null; extractionStatus: string | null; extractionId: string | null; }
export interface ClinicalNote { id: string; encounterId: string; patientReference: string; encounterReference: string; encounterDate: string; context: string; text: string; version: number; status: string; updatedAt: string; signedAt: string | null; extractionStatus: string | null; extractionId: string | null; error: string | null; }
export interface NoteVersion { version: number; sourceText: string; sourceHash: string; signedAt: string | null; createdAt: string; }
export function apiError(error: unknown): string {
  const e = error as HttpErrorResponse;
  if (e.status === 0) return 'The clinical service is unavailable. Your text remains in this editor. Please try again.';
  if (e.status === 401) return 'Your session has expired. Sign in again to continue.';
  return e.error?.detail || 'The operation could not complete. Please reload the latest state and try again.';
}
@Injectable({providedIn: 'root'})
export class EmrApi {
  private http = inject(HttpClient);
  private base = '/api/v1/emr';
  readonly doctor = signal<Doctor | null>(null);
  private sessionRequest?: Promise<Doctor | null>;
  async session(): Promise<Doctor | null> {
    if (this.doctor()) return this.doctor();
    return this.sessionRequest ??= firstValueFrom(this.http.get<Doctor>(this.base + '/auth/me'))
      .then(d => {this.doctor.set(d); return d;}).catch(() => null).finally(() => this.sessionRequest = undefined);
  }
  async csrf() { await firstValueFrom(this.http.get(this.base + '/auth/csrf')); }
  async login(login: string, password: string) {
    await this.csrf();
    const d = await firstValueFrom(this.http.post<Doctor>(this.base + '/auth/login', {login, password}));
    this.doctor.set(d); await this.csrf(); return d;
  }
  async logout() { await firstValueFrom(this.http.post(this.base + '/auth/logout', {})); this.doctor.set(null); }
  encounters() { return firstValueFrom(this.http.get<Encounter[]>(this.base + '/encounters')); }
  encounter(id: string) { return firstValueFrom(this.http.get<Encounter>(this.base + '/encounters/' + id)); }
  note(id: string) { return firstValueFrom(this.http.get<ClinicalNote>(this.base + '/notes/' + id)); }
  versions(id: string) { return firstValueFrom(this.http.get<NoteVersion[]>(this.base + '/notes/' + id + '/versions')); }
  create(encounter: string, text: string) { return firstValueFrom(this.http.post<ClinicalNote>(this.base + '/encounters/' + encounter + '/notes', {text})); }
  save(note: ClinicalNote, text: string) { return firstValueFrom(this.http.put<ClinicalNote>(this.base + '/notes/' + note.id, {text, expectedVersion: note.version})); }
  correct(note: ClinicalNote) { return firstValueFrom(this.http.post<ClinicalNote>(this.base + '/notes/' + note.id + '/corrections', {text: note.text, expectedVersion: note.version})); }
  sign(note: ClinicalNote) { return firstValueFrom(this.http.post<ClinicalNote>(this.base + '/notes/' + note.id + '/sign', {expectedVersion: note.version})); }
  upload(note: ClinicalNote, file: File) { const form = new FormData(); form.append('file', file); form.append('expectedVersion', String(note.version)); return firstValueFrom(this.http.post<ClinicalNote>(this.base + '/notes/' + note.id + '/pdf', form)); }
  uploadNew(encounter: string, file: File) { const form = new FormData(); form.append('file', file); return firstValueFrom(this.http.post<ClinicalNote>(this.base + '/encounters/' + encounter + '/pdf', form)); }
}
export const doctorGuard: CanActivateFn = async () => {
  const api = inject(EmrApi), router = inject(Router);
  return await api.session() ? true : router.createUrlTree(['/login']);
};
export interface UnsavedEditor { canLeave(): boolean; }
export const unsavedGuard: CanDeactivateFn<UnsavedEditor> = c => c.canLeave();
function getXsrfCookie(): string | null {
  if (typeof document === 'undefined') return null;
  const match = document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/);
  return match ? decodeURIComponent(match[1]) : null;
}
export const sessionInterceptor: HttpInterceptorFn = (request, next) => {
  const api = inject(EmrApi), router = inject(Router);
  const method = request.method.toUpperCase();
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method) && !request.headers.has('X-XSRF-TOKEN')) {
    const token = getXsrfCookie();
    if (token) {
      request = request.clone({
        headers: request.headers.set('X-XSRF-TOKEN', token)
      });
    }
  }
  return next(request).pipe(catchError((error: HttpErrorResponse) => {
    if (error.status === 401 && api.doctor() && !request.url.endsWith('/auth/login')) {
      api.doctor.set(null);
      // Leave the editor mounted until the doctor chooses to sign in; unsaved text is retained in memory.
      if (!router.url.includes('/notes/') && !router.url.includes('/encounters/')) void router.navigate(['/login']);
    }
    return throwError(() => error);
  }));
};

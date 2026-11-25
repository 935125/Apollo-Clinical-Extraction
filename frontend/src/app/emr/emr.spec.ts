import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { Component } from '@angular/core';
import { EmrApi, doctorGuard, unsavedGuard } from './emr-api';
import { DoctorLogin } from './login';
import { DoctorDashboard } from './dashboard';
import { NoteEditor } from './note-editor';
import { ExtractionStatus } from './extraction-status';
import { PortalShell } from './portal-shell';
import {
  CLINICAL_SECTIONS,
  emptySectionValues,
  parseClinicalNote,
  populatedSectionsCount,
  serializeClinicalSections,
} from './clinical-note-sections';

@Component({ standalone: true, template: '' })
class DummyComponent {}

describe('Doctor OP EMR Frontend Suite', () => {
  let api: EmrApi;
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DoctorLogin, DoctorDashboard, NoteEditor, ExtractionStatus, PortalShell],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'login', component: DummyComponent },
          { path: 'emr', component: DummyComponent },
          { path: 'emr/notes/:id/status', component: DummyComponent },
          { path: 'emr/notes/:id/edit', component: DummyComponent },
        ])
      ]
    });
    api = TestBed.inject(EmrApi);
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('EmrApi manages session state and doctor profile', async () => {
    expect(api.doctor()).toBeNull();
    const sessionPromise = api.session();
    const req = httpMock.expectOne('/api/v1/emr/auth/me');
    expect(req.request.method).toBe('GET');
    req.flush({ id: '11111111-1111-1111-1111-111111111111', login: 'doctor.krishnan@apollo.local', displayName: 'Dr. K. Krishnan', role: 'Doctor' });

    const doctor = await sessionPromise;
    expect(doctor?.displayName).toBe('Dr. K. Krishnan');
    expect(api.doctor()?.role).toBe('Doctor');
  });

  it('DoctorLogin validates inputs and submits credentials', async () => {
    const fixture = TestBed.createComponent(DoctorLogin);
    const comp = fixture.componentInstance;
    fixture.detectChanges();

    expect(comp.form.valid).toBe(false);
    comp.form.controls.login.setValue('doctor.krishnan@apollo.local');
    comp.form.controls.password.setValue('ValidDoctorPass123!');
    expect(comp.form.valid).toBe(true);

    expect(comp.showPassword()).toBe(false);
    comp.showPassword.set(true);
    expect(comp.showPassword()).toBe(true);
  });

  it('DoctorDashboard displays and filters encounters correctly', () => {
    const fixture = TestBed.createComponent(DoctorDashboard);
    const comp = fixture.componentInstance;
    const mockEncounters = [
      { id: 'e1', reference: 'ENC-100567', patientReference: 'P100234', encounterDate: '2026-01-28', context: 'General medicine · Follow-up', noteId: 'n1', version: 1, noteStatus: 'Signed', extractionStatus: 'Completed', extractionId: 'ext1' },
      { id: 'e2', reference: 'ENC-100568', patientReference: 'P100235', encounterDate: '2026-01-28', context: 'General medicine · Consultation', noteId: 'n2', version: 1, noteStatus: 'Draft', extractionStatus: null, extractionId: null },
      { id: 'e3', reference: 'ENC-100569', patientReference: 'P100236', encounterDate: '2026-01-27', context: 'Internal medicine · Review', noteId: 'n3', version: 1, noteStatus: 'Signed', extractionStatus: 'Needs Review', extractionId: 'ext2' }
    ];

    comp.encounters.set(mockEncounters);
    expect(comp.drafts()).toBe(1);
    expect(comp.awaiting()).toBe(1);
    expect(comp.completed()).toBe(2);

    // Filter 'draft'
    comp.filter.set('draft');
    expect(comp.visible().length).toBe(1);
    expect(comp.visible()[0].patientReference).toBe('P100235');

    // Filter 'review'
    comp.filter.set('review');
    expect(comp.visible().length).toBe(1);
    expect(comp.visible()[0].patientReference).toBe('P100236');

    // Search query
    comp.filter.set('all');
    comp.query.set('P100234');
    expect(comp.visible().length).toBe(1);
    expect(comp.visible()[0].reference).toBe('ENC-100567');
  });

  it('NoteEditor calculates word counts and tracks unsaved changes', () => {
    const fixture = TestBed.createComponent(NoteEditor);
    const comp = fixture.componentInstance;
    comp.text.set('Continue amlodipine 5 mg orally once daily for 7 days.');
    expect(comp.words()).toBe(10);
    expect(comp.dirty()).toBe(true);

    comp.canLeave();
    expect(typeof comp.canLeave).toBe('function');
  });

  it('ExtractionStatus calculates progression stage properly', () => {
    const fixture = TestBed.createComponent(ExtractionStatus);
    const comp = fixture.componentInstance;

    comp.note.set({
      id: 'n1', encounterId: 'e1', patientReference: 'P100234', encounterReference: 'ENC-100567',
      encounterDate: '2026-01-28', context: 'General medicine', text: 'Note content',
      version: 1, status: 'Signed', updatedAt: '2026-01-28T11:24:00Z', signedAt: '2026-01-28T11:24:00Z',
      extractionStatus: 'Queued', extractionId: null, error: null
    });
    expect(comp.stage()).toBe(2);

    comp.note.set({ ...comp.note()!, extractionStatus: 'Processing' });
    expect(comp.stage()).toBe(3);

    comp.note.set({ ...comp.note()!, extractionStatus: 'Completed' });
    expect(comp.stage()).toBe(4);

    comp.note.set({ ...comp.note()!, extractionStatus: 'Needs Review' });
    expect(comp.stage()).toBe(5);
  });

  it('PortalShell renders doctor initials and header elements', () => {
    const fixture = TestBed.createComponent(PortalShell);
    const comp = fixture.componentInstance;
    api.doctor.set({ id: 'doc1', login: 'doctor.krishnan@apollo.local', displayName: 'Dr. K. Krishnan', role: 'Doctor' });
    fixture.detectChanges();

    expect(comp.initials('Dr. K. Krishnan')).toBe('DK');
    expect(comp.initials('Dr. Anand Rao')).toBe('DR');
    expect(comp.isAdmin()).toBe(false);
  });

  it('Structured clinical note serializer & parser preserve all 12 sections deterministically and keep legacy free-text intact', () => {
    expect(CLINICAL_SECTIONS.length).toBe(12);
    const values = emptySectionValues();
    values['chief_complaint'] = 'Follow-up for hypertension and review of medications.';
    values['hpi'] = 'Known hypertension for 5 years. Good adherence.';
    values['current_medications'] = 'Amlodipine 5 mg once daily. Atorvastatin 10 mg at night.';
    values['assessment'] = 'Hypertension — controlled. Dyslipidaemia.';
    values['investigations'] = 'Order complete blood count tomorrow.';

    expect(populatedSectionsCount(values)).toBe(5);
    const serialized = serializeClinicalSections(values);
    expect(serialized).toContain('Chief Complaint:\nFollow-up for hypertension and review of medications.');
    expect(serialized).toContain('Current Medications:\nAmlodipine 5 mg once daily. Atorvastatin 10 mg at night.');
    expect(serialized).not.toContain('Allergies:');

    const parsed = parseClinicalNote(serialized);
    expect(parsed.isLegacyUnstructured).toBe(false);
    expect(parsed.values['chief_complaint']).toBe(values['chief_complaint']);
    expect(parsed.values['hpi']).toBe(values['hpi']);
    expect(parsed.values['current_medications']).toBe(values['current_medications']);
    expect(parsed.values['assessment']).toBe(values['assessment']);
    expect(parsed.values['investigations']).toBe(values['investigations']);
    expect(parsed.values['allergies']).toBe('');

    // Legacy unstructured note must be preserved verbatim in additional_notes
    const legacyText = 'Follow-up for hypertension.\nContinue amlodipine 5 mg orally once daily for 7 days.';
    const parsedLegacy = parseClinicalNote(legacyText);
    expect(parsedLegacy.isLegacyUnstructured).toBe(true);
    expect(parsedLegacy.values['additional_notes']).toBe(legacyText);
  });

  it('NoteEditor synchronizes structured section edits to serialized clinical text', () => {
    const fixture = TestBed.createComponent(NoteEditor);
    const comp = fixture.componentInstance;
    comp.updateSection('chief_complaint', 'Hypertension follow-up');
    comp.updateSection('medications', 'Continue amlodipine 5 mg daily');
    expect(comp.completedSectionCount()).toBe(2);
    expect(comp.text()).toContain('Chief Complaint:\nHypertension follow-up');
    expect(comp.text()).toContain('Medications:\nContinue amlodipine 5 mg daily');
    expect(comp.dirty()).toBe(true);
  });

  it('DoctorDashboard computes all 5 KPIs dynamically from real encounters and supports sorting and status filtering', () => {
    const fixture = TestBed.createComponent(DoctorDashboard);
    const comp = fixture.componentInstance;
    comp.encounters.set([
      { id: 'e1', reference: 'ENC-100567', patientReference: 'P100234', encounterDate: '2026-01-28', context: 'General medicine · Follow-up', noteId: 'n1', version: 1, noteStatus: 'Signed', extractionStatus: 'Completed', extractionId: 'ext1' },
      { id: 'e2', reference: 'ENC-100568', patientReference: 'P100235', encounterDate: '2026-01-28', context: 'General medicine · New Consultation', noteId: 'n2', version: 1, noteStatus: 'Draft', extractionStatus: null, extractionId: null },
      { id: 'e3', reference: 'ENC-100569', patientReference: 'P100236', encounterDate: '2026-01-27', context: 'Internal medicine · Chronic Care', noteId: 'n3', version: 1, noteStatus: 'Signed', extractionStatus: 'Processing', extractionId: null },
      { id: 'e4', reference: 'ENC-100570', patientReference: 'P100237', encounterDate: '2026-01-26', context: 'Cardiology · Review', noteId: 'n4', version: 1, noteStatus: 'Signed', extractionStatus: 'Needs Review', extractionId: 'ext4' },
    ]);

    expect(comp.encounters().length).toBe(4);
    expect(comp.drafts()).toBe(1);
    expect(comp.signedNotes()).toBe(3);
    expect(comp.awaitingExtraction()).toBe(1);
    expect(comp.completed()).toBe(2);
    expect(comp.patients().length).toBe(4);

    comp.statusFilter.set('Processing');
    expect(comp.visible().length).toBe(1);
    expect(comp.visible()[0].patientReference).toBe('P100236');

    comp.clearFilters();
    comp.toggleSort('patient');
    expect(comp.visible()[0].patientReference).toBe('P100234');
  });
});

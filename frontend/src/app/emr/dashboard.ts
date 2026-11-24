import { Component, computed, inject, OnInit, signal, viewChild, ElementRef } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { EmrApi, Encounter, apiError } from './emr-api';

@Component({selector:'doctor-dashboard', standalone:true, imports:[DatePipe,FormsModule,RouterLink], template:`
<main class="emr-page dashboard-page">
  <div class="emr-page-heading"><div><p class="emr-eyebrow">OUTPATIENT CARE</p><h1>{{title()}}</h1><p class="emr-description">{{mode==='extractions' ? 'Source-grounded results from your signed clinical notes.' : mode==='history' ? 'Your clinical notes, signed versions and extraction history.' : 'Your outpatient consultations and clinical notes.'}}</p></div><button class="emr-button primary" (click)="chooseEncounter()"><span class="plus" aria-hidden="true">＋</span> New Clinical Note</button></div>
  @if(mode==='dashboard' && !loading() && encounters().length) {<div class="summary-strip" aria-label="Clinical note summary"><div><span class="summary-number">{{awaiting()}}</span><span>Notes awaiting review</span></div><span class="summary-divider"></span><div><span class="summary-number">{{completed()}}</span><span>Extractions completed</span></div><span class="summary-note">Every note, with a clear next step.</span></div>}
  <section class="encounters-section" aria-labelledby="encounters-heading">
    <div class="section-heading"><h2 id="encounters-heading">{{mode==='dashboard' ? 'Recent Encounters' : mode==='history' ? 'Clinical note history' : 'Extraction results'}}</h2><span class="secondary-meta">{{encounters().length}} assigned encounters</span></div>
    <div class="table-toolbar"><div class="encounter-tabs" role="group" aria-label="Filter encounters"><button [class.active]="filter()==='all'" (click)="filter.set('all')">My Encounters</button><button [class.active]="filter()==='draft'" (click)="filter.set('draft')">Draft Notes <span>{{drafts()}}</span></button><button [class.active]="filter()==='review'" (click)="filter.set('review')">Awaiting Review <span>{{awaiting()}}</span></button></div><div class="encounter-search"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/></svg><input aria-label="Search assigned encounters" placeholder="Search patient or encounter" [ngModel]="query()" (ngModelChange)="query.set($event)"></div></div>
    @if(error()) {<div class="emr-alert error" role="alert">{{error()}} <button class="emr-text-button" (click)="load()">Try again</button></div>}
    @if(loading()) {<div class="emr-empty" role="status"><span class="loading-ring"></span><p>Loading your encounters…</p></div>} @else {
      <div class="clinical-table-wrap"><table class="clinical-table"><thead><tr><th>Patient Reference</th><th>Encounter Date</th><th>Note Status</th><th>Extraction</th><th class="action-column">Action</th></tr></thead><tbody>
      @for(e of visible(); track e.id) {<tr (click)="open(e)" [class.clickable-row]="true"><td><a [routerLink]="e.noteId ? ['/emr/notes',e.noteId] : ['/emr/encounters',e.id,'new']" (click)="$event.stopPropagation()" class="patient-link">{{e.patientReference}}</a><span class="table-secondary">{{e.context.split(' · ')[0]}}</span></td><td>{{e.encounterDate | date:'dd MMM yyyy'}}<span class="table-secondary">{{e.reference}}</span></td><td><span class="status-label" [class.draft]="e.noteStatus==='Draft'" [class.success]="e.noteStatus==='Signed'">{{e.noteStatus || 'Not started'}}</span></td><td>@if(e.extractionStatus) {<span class="status-label" [class.review]="e.extractionStatus==='Needs Review'" [class.processing]="e.extractionStatus==='Processing' || e.extractionStatus==='Queued'" [class.failed]="e.extractionStatus==='Failed'" [class.success]="e.extractionStatus==='Completed'">@if(e.extractionStatus==='Processing'){<span class="status-spinner" aria-hidden="true"></span>}{{e.extractionStatus}}</span>} @else {<span class="muted" aria-label="No extraction">—</span>}</td><td class="action-column"><button class="emr-button table-action" (click)="$event.stopPropagation();open(e)" [attr.aria-label]="'Open encounter for '+e.patientReference">Open</button></td></tr>}
      </tbody></table></div>
      @if(!visible().length) {<div class="emr-empty"><div class="empty-document"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6M9 16h6"/></svg></div><h3>{{encounters().length ? 'No matching clinical notes.' : 'No clinical notes yet.'}}</h3><p>{{query() ? 'Try another patient or encounter reference.' : mode==='extractions' ? 'Results will appear here after a signed note is processed.' : 'Open an assigned encounter to begin your clinical note.'}}</p>@if(filter()!=='all' || query()) {<button class="emr-button secondary" (click)="filter.set('all');query.set('')">Clear filters</button>}</div>}
    }
    <div class="table-footnote"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 4 6v6c0 4 8 9 8 9s8-5 8-9V6z"/></svg><span>Only encounters assigned to you are shown. All patient references are synthetic.</span></div>
  </section>
  <aside class="clinical-reminder"><div class="reminder-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6M9 16h4"/></svg></div><div><h3>Document. Sign. Review.</h3><p>Save your consultation as a draft, or sign to begin clinical extraction. You remain in control of every clinical decision.</p></div><span class="reminder-label">YOUR CLINICAL WORKSPACE</span></aside>
</main>
<dialog #encounterDialog class="emr-dialog"><form (submit)="$event.preventDefault();startSelected()"><p class="emr-eyebrow">NEW CLINICAL NOTE</p><h2>Choose an encounter</h2><p>Start a note within an encounter assigned to you.</p><label for="encounter-choice">Assigned encounter</label><select id="encounter-choice" name="encounter" [(ngModel)]="selectedEncounter"><option value="" disabled>Select an encounter</option>@for(e of encounters();track e.id){<option [value]="e.id">{{e.patientReference}} · {{e.encounterDate | date:'dd MMM yyyy'}} · {{e.noteStatus || 'Not started'}}</option>}</select><div class="dialog-actions"><button class="emr-button secondary" type="button" (click)="dialog()?.nativeElement?.close()">Cancel</button><button class="emr-button primary" type="submit" [disabled]="!selectedEncounter">Open encounter</button></div></form></dialog>
`})
export class DoctorDashboard implements OnInit {
  private api=inject(EmrApi); private router=inject(Router); private route=inject(ActivatedRoute);
  readonly mode=this.route.snapshot.data['mode'] || 'dashboard';
  title=computed(()=>this.mode==='extractions'?'Clinical Extraction':this.mode==='history'?'History':'Doctor Dashboard');
  encounters=signal<Encounter[]>([]); loading=signal(true); error=signal('');query=signal('');filter=signal('all');selectedEncounter='';
  dialog=viewChild<ElementRef<HTMLDialogElement>>('encounterDialog');
  drafts=computed(()=>this.encounters().filter(e=>e.noteStatus==='Draft').length);
  awaiting=computed(()=>this.encounters().filter(e=>e.extractionStatus==='Needs Review').length);
  completed=computed(()=>this.encounters().filter(e=>['Completed','Needs Review'].includes(e.extractionStatus||'')).length);
  visible=computed(()=>this.encounters().filter(e=>
    (this.mode!=='extractions'||!!e.extractionStatus) &&
    (this.mode!=='history'||!!e.noteId) &&
    (this.filter()==='all'||this.filter()==='draft'&&e.noteStatus==='Draft'||this.filter()==='review'&&e.extractionStatus==='Needs Review') &&
    (!this.query()||[e.patientReference,e.reference].some(s=>s.toLowerCase().includes(this.query().toLowerCase())))
  ));
  ngOnInit(){void this.load();}
  async load(){this.loading.set(true);this.error.set('');try{this.encounters.set(await this.api.encounters());}catch(e){this.error.set(apiError(e));}finally{this.loading.set(false);}}
  chooseEncounter(){if(!this.encounters().length){this.error.set('No encounters are assigned to this account. Ask your clinical coordinator to assign one.');return;}this.dialog()?.nativeElement.showModal();}
  startSelected(){const e=this.encounters().find(e=>e.id===this.selectedEncounter);if(e){this.dialog()?.nativeElement.close();this.open(e);}}
  open(e:Encounter){if(e.extractionId&&this.mode==='extractions')void this.router.navigate(['/extraction/result'],{queryParams:{extractionId:e.extractionId,noteId:e.noteId,patient:e.patientReference}});else if(e.noteId)void this.router.navigate(['/emr/notes',e.noteId,e.noteStatus==='Signed'?'status':'edit']);else void this.router.navigate(['/emr/encounters',e.id,'new']);}
}

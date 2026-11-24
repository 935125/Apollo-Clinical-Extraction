import { Component, computed, ElementRef, HostListener, inject, OnInit, signal, viewChild } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ClinicalNote, EmrApi, Encounter, NoteVersion, apiError } from './emr-api';

@Component({selector:'clinical-note-editor', standalone:true, imports:[DatePipe,DecimalPipe,FormsModule,RouterLink], template:`
<main class="emr-page note-page">
  <a routerLink="/emr" class="back-link"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m14 6-6 6 6 6M8 12h13"/></svg> Doctor Dashboard</a>
  <div class="emr-page-heading"><div><p class="emr-eyebrow">OUTPATIENT DOCUMENTATION</p><h1>{{note()?.status==='Signed'?'Signed Clinical Note':note()?'Clinical Note':'New Clinical Note'}}</h1></div><div class="note-actions">@if(note()?.status!=='Signed') {<button class="emr-button secondary" (click)="save()" [disabled]="busy()||!text().trim()">{{busy()&&action()==='save'?'Saving…':'Save Draft'}}</button><button class="emr-button primary sign-button" (click)="confirmSign()" [disabled]="busy()||!text().trim()"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" class="sign-icon"><path d="M17 3a2.828 2.828 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z"/><path d="m15 5 4 4"/></svg> {{busy()&&action()==='sign'?'Submitting…':'Sign & Submit'}}</button>} @else {<button class="emr-button secondary" (click)="correction()" [disabled]="busy()">Create correction</button><a class="emr-button primary" [routerLink]="['/emr/notes',note()!.id,'status']">View extraction status</a>}</div></div>
  @if(loading()){<div class="emr-empty" role="status">Loading clinical note…</div>}
  @if(error()){<div class="emr-alert error" role="alert">{{error()}} @if(!api.doctor()){<a routerLink="/login">Sign in again</a>}</div>}
  @if(success()){<div class="emr-alert success" role="status">{{success()}}</div>}
  @if(context(); as c){
    <div class="encounter-context"><div><span>Patient: <strong>{{c.patientReference}}</strong></span><span class="ctx-divider">|</span><span>Encounter: <strong>{{c.reference}}</strong></span><span class="ctx-divider">|</span><span>Date: <strong>{{c.encounterDate | date:'dd MMM yyyy'}}</strong></span></div><span class="synthetic-tag">Synthetic record</span></div>
    <section class="note-editor-frame" aria-labelledby="clinical-note-label"><div class="note-editor-heading"><h2 id="clinical-note-label">Clinical Note</h2><div>@if(note()){<span class="editor-version">Version {{note()!.version}}</span>}<span class="status-label" [class.draft]="note()?.status!=='Signed'" [class.success]="note()?.status==='Signed'">{{note()?.status||'Unsaved'}}</span></div></div>
      <div class="note-writing-surface"><label class="sr-only" for="clinical-note-text">Clinical note text</label><textarea id="clinical-note-text" [ngModel]="text()" (ngModelChange)="changeText($event)" [readOnly]="note()?.status==='Signed'" maxlength="65536" placeholder="Document the consultation here…&#10;&#10;Include the assessment, medications, investigations and follow-up plan." spellcheck="false"></textarea><div class="editor-counts"><span>{{words()}} words <span class="count-divider">·</span> {{text().length | number}} characters</span><span>{{note()?.status==='Signed' ? 'Signed version · Read only' : dirty() ? 'Unsaved changes' : note() ? 'All changes saved' : 'Ready to document'}}</span></div></div>
      @if(note()?.status!=='Signed'){<div class="note-editor-footer"><button class="emr-text-button upload-button" (click)="pdfInput.click()" [disabled]="busy()"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6"/></svg> Upload text-based PDF</button><input #pdfInput type="file" accept="application/pdf,.pdf" class="sr-only" aria-label="Upload text-based PDF" (change)="upload($event)"><span>Up to 20 MiB · Import replaces the draft text</span></div>}
    </section>
    <div class="note-bottom-line"><p><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 4 6v6c0 4 8 9 8 9s8-5 8-9V6z"/></svg> Signing saves an immutable version and submits it for clinical extraction.</p>@if(note()){<span>Last saved {{note()!.updatedAt | date:'dd MMM yyyy, HH:mm'}}</span>}</div>
    @if(note()){<details class="version-history" (toggle)="loadVersions($event)"><summary>Version history</summary>@for(v of versions();track v.version){<details><summary>Version {{v.version}} · {{v.signedAt?'Signed':'Draft snapshot'}} · {{v.createdAt | date:'dd MMM, HH:mm'}}</summary><pre>{{v.sourceText}}</pre><span class="hash-meta">SHA-256 {{v.sourceHash}}</span></details>}</details>}
  }
</main>
<dialog #signDialog class="emr-dialog"><p class="emr-eyebrow">SIGN CLINICAL NOTE</p><h2>Ready to sign this note?</h2><p>Your note will be saved as an immutable signed version and submitted for clinical extraction.</p><div class="sign-notice">Signing the note does not approve AI-generated prescriptions or investigation orders. Extraction results require human review.</div><div class="dialog-actions"><button class="emr-button secondary" (click)="dialog()?.nativeElement?.close()">Continue editing</button><button class="emr-button primary" (click)="sign()" [disabled]="busy()">Sign & Submit</button></div></dialog>
`})
export class NoteEditor implements OnInit {
  readonly api=inject(EmrApi);private route=inject(ActivatedRoute);private router=inject(Router);
  note=signal<ClinicalNote|null>(null);encounter=signal<Encounter|null>(null);text=signal('');loading=signal(true);busy=signal(false);error=signal('');success=signal('');action=signal('');versions=signal<NoteVersion[]>([]);private persisted=signal('');
  dialog=viewChild<ElementRef<HTMLDialogElement>>('signDialog');
  words=computed(()=>this.text().trim()?this.text().trim().split(/\s+/).length:0);
  dirty=computed(()=>this.text()!==this.persisted());
  context=computed(()=>{const n=this.note(),e=this.encounter();return n?{patientReference:n.patientReference,reference:n.encounterReference,encounterDate:n.encounterDate,context:n.context}:e;});
  async ngOnInit(){try{const id=this.route.snapshot.paramMap.get('id')!;if(this.route.snapshot.data['new'])this.encounter.set(await this.api.encounter(id));else this.accept(await this.api.note(id));await this.api.csrf();}catch(e){this.error.set(apiError(e));}finally{this.loading.set(false);}}
  private accept(n:ClinicalNote){this.note.set(n);this.persisted.set(n.text);this.text.set(n.text);}
  changeText(text:string){this.text.set(text);this.success.set('');}
  async persist(){const n=this.note();const updated=n?await this.api.save(n,this.text()):await this.api.create(this.encounter()!.id,this.text());this.accept(updated);return updated;}
  async save(){if(this.busy())return;this.busy.set(true);this.action.set('save');this.error.set('');try{await this.persist();this.success.set('Draft saved. You can return to it at any time.');}catch(e){this.error.set(apiError(e));}finally{this.busy.set(false);}}
  confirmSign(){this.error.set('');this.dialog()?.nativeElement.showModal();}
  async sign(){if(this.busy())return;this.dialog()?.nativeElement.close();this.busy.set(true);this.action.set('sign');this.error.set('');try{const n=await this.persist();const signed=await this.api.sign(n);this.accept(signed);await this.router.navigate(['/emr/notes',signed.id,'status']);}catch(e){this.error.set(apiError(e));}finally{this.busy.set(false);}}
  async correction(){if(this.busy()||!this.note())return;this.busy.set(true);this.error.set('');try{this.accept(await this.api.correct(this.note()!));this.success.set('A new draft version is ready. Previous signed versions remain in the version history.');}catch(e){this.error.set(apiError(e));}finally{this.busy.set(false);}}
  async upload(event:Event){const input=event.target as HTMLInputElement,file=input.files?.[0];if(!file)return;input.value='';if(file.size>20*1024*1024||!file.name.toLowerCase().endsWith('.pdf')){this.error.set('Choose a PDF of 20 MiB or smaller.');return;}if(!window.confirm('Importing this PDF will replace the current draft text. Continue?'))return;this.busy.set(true);this.error.set('');try{const n=this.note();this.accept(n?await this.api.upload(n,file):await this.api.uploadNew(this.encounter()!.id,file));this.success.set('PDF text imported and saved as a new draft version. Review it before signing.');}catch(e){this.error.set(apiError(e));}finally{this.busy.set(false);}}
  async loadVersions(event:Event){if((event.target as HTMLDetailsElement).open&&this.note())try{this.versions.set(await this.api.versions(this.note()!.id));}catch(e){this.error.set(apiError(e));}}
  canLeave(){return !this.dirty()||window.confirm('Leave this note? Your unsaved changes will be lost.');}
  @HostListener('window:beforeunload',['$event']) beforeUnload(event:BeforeUnloadEvent){if(this.dirty()){event.preventDefault();event.returnValue='';}}
}

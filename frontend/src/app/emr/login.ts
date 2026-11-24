import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { EmrApi, apiError } from './emr-api';
import { ApolloBrand } from './portal-shell';

@Component({selector:'doctor-login', standalone:true, imports:[ReactiveFormsModule,ApolloBrand], template:`
  <main class="login-page">
    <section class="identity-panel" aria-label="Apollo clinical workspace">
      <a class="portal-brand light-brand" href="/" aria-label="Apollo Hospitals"><apollo-brand /></a>
      <div class="identity-copy"><span class="identity-eyebrow">APOLLO CLINICAL WORKSPACE</span><h1>Your clinical<br>workspace.</h1><p>Secure access to outpatient documentation and AI-assisted clinical extraction.</p><span class="identity-line"></span><span class="identity-caption">More clarity. More time for care.</span></div>
      <div class="identity-footer"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 4 6v6c0 4 8 9 8 9s8-5 8-9V6z"/><path d="m8 12 3 3 5-6"/></svg> A trusted space for clinical care</div>
    </section>
    <section class="login-form-panel">
      <form class="login-form" [formGroup]="form" (ngSubmit)="signIn()">
        <p class="emr-eyebrow">DOCTOR OP EMR</p><h2>Welcome back</h2><p class="login-subtitle">Sign in to continue.</p>
        <label for="doctor-login">Email / Doctor ID</label>
        <div class="input-with-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="m3 6 9 7 9-7"/></svg><input id="doctor-login" formControlName="login" autocomplete="username" placeholder="Enter your email or doctor ID" maxlength="256" [attr.aria-invalid]="form.controls.login.touched && form.controls.login.invalid" aria-describedby="login-validation"></div>
        @if(form.controls.login.touched && form.controls.login.invalid) {<p class="field-error" id="login-validation">Enter your email or doctor ID.</p>}
        <label for="doctor-password">Password</label>
        <div class="input-with-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V6a4 4 0 0 1 8 0v4M12 14v3"/></svg><input id="doctor-password" formControlName="password" [type]="showPassword() ? 'text' : 'password'" autocomplete="current-password" placeholder="Enter your password" maxlength="1024" [attr.aria-invalid]="form.controls.password.touched && form.controls.password.invalid"><button class="password-toggle" type="button" (click)="showPassword.set(!showPassword())" [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'" [attr.aria-pressed]="showPassword()"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>@if(showPassword()) {<path d="M3 3 21 21"/>}</svg></button></div>
        @if(form.controls.password.touched && form.controls.password.invalid) {<p class="field-error">Enter your password.</p>}
        @if(error()) {<p class="emr-alert error" role="alert">{{error()}}</p>}
        <button class="emr-button primary sign-in" type="submit" [disabled]="busy()">{{busy() ? 'Signing in…' : 'Sign in'}}</button>
        <div class="login-security"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 4 6v6c0 4 8 9 8 9s8-5 8-9V6z"/><path d="m8 12 3 3 5-6"/></svg><span>Your session is protected and expires after 30 minutes.</span></div>
      </form>
      <footer class="login-footer"><span>Local demonstration authentication</span><span>Synthetic data only · Institutional access is not connected.</span></footer>
    </section>
  </main>
`})
export class DoctorLogin {
  private api=inject(EmrApi); private router=inject(Router);
  readonly form=new FormGroup({login:new FormControl('',{nonNullable:true,validators:[Validators.required,Validators.maxLength(256)]}),password:new FormControl('',{nonNullable:true,validators:[Validators.required,Validators.maxLength(1024)]})});
  showPassword=signal(false); busy=signal(false); error=signal('');
  async signIn() {if(this.busy())return;this.form.markAllAsTouched();if(this.form.invalid)return;this.busy.set(true);this.error.set('');try{const v=this.form.getRawValue();await this.api.login(v.login,v.password);this.form.controls.password.reset();await this.router.navigate(['/emr']);}catch(e){const err=e as {status:number;error?:{detail?:string}};this.error.set(err.status===401 ? 'Unable to sign in. Check your credentials or try again later.' : apiError(e));}finally{this.busy.set(false);}}
}

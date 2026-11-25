import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { EmrApi, apiError } from './emr-api';
import { ApolloBrand } from './portal-shell';

@Component({
  selector: 'doctor-login',
  standalone: true,
  imports: [ReactiveFormsModule, ApolloBrand],
  template: `
    <div class="login-shell">
      <header class="login-topbar">
        <a class="portal-brand" href="/" aria-label="Apollo Hospitals">
          <apollo-brand />
        </a>
        <span class="login-platform-title">Apollo Clinical Extraction Platform</span>
      </header>

      <main class="login-page">
        <section class="identity-panel" aria-label="Apollo clinical workspace">
          <svg class="identity-wave-svg" viewBox="0 0 600 800" preserveAspectRatio="none" aria-hidden="true">
            <path d="M-40 490 C140 420 260 630 470 540 C550 505 610 430 660 390 L660 860 L-40 860 Z" fill="#0c6b5d" stroke="none" />
            <path d="M-40 610 C180 540 310 730 560 620 L660 580 L660 860 L-40 860 Z" fill="#03312a" stroke="none" />
          </svg>

          <div class="identity-copy">
            <h1>Your clinical<br />workspace.</h1>
            <p>
              Secure access to outpatient records, clinical documentation and AI-assisted extraction.
            </p>

            <ul class="identity-highlights">
              <li>
                <span class="highlight-icon" aria-hidden="true">
                  <svg viewBox="0 0 24 24">
                    <path d="M6 3h9l4 4v14H6z" />
                    <path d="M14 3v5h5M9 13h6M9 17h4" />
                  </svg>
                </span>
                <span>Document OP consultation notes</span>
              </li>
              <li>
                <span class="highlight-icon" aria-hidden="true">
                  <svg viewBox="0 0 24 24">
                    <circle cx="12" cy="5" r="2.5" />
                    <circle cx="6" cy="18" r="2.5" />
                    <circle cx="18" cy="18" r="2.5" />
                    <path d="M12 7.5v4.5m0 0-4.5 4M12 12l4.5 4" />
                  </svg>
                </span>
                <span>AI extraction of medications &amp; investigations</span>
              </li>
              <li>
                <span class="highlight-icon" aria-hidden="true">
                  <svg viewBox="0 0 24 24">
                    <path d="M12 3 4 6v6c0 4.5 8 9 8 9s8-4.5 8-9V6z" />
                    <path d="m8.5 12 2.5 2.5 4.5-5" />
                  </svg>
                </span>
                <span>Review, validate and track extraction results</span>
              </li>
            </ul>
          </div>
        </section>

        <section class="login-form-panel" aria-label="Sign in form">
          <form class="login-form" [formGroup]="form" (ngSubmit)="signIn()">
            <h2>Welcome back</h2>
            <p class="login-subtitle">Sign in to continue to your OP EMR workspace.</p>

            <label for="doctor-login">Email / Account ID</label>
            <div class="input-with-icon">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <rect x="3" y="5" width="18" height="14" rx="2" />
                <path d="m3 6 9 7 9-7" />
              </svg>
              <input
                id="doctor-login"
                formControlName="login"
                autocomplete="username"
                placeholder="Enter your email or doctor ID"
                maxlength="256"
                [attr.aria-invalid]="form.controls.login.touched && form.controls.login.invalid"
                aria-describedby="login-validation"
              />
            </div>
            @if (form.controls.login.touched && form.controls.login.invalid) {
              <p class="field-error" id="login-validation">Enter your email or doctor ID.</p>
            }

            <label for="doctor-password">Password</label>
            <div class="input-with-icon">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <rect x="5" y="10" width="14" height="11" rx="2" />
                <path d="M8 10V6a4 4 0 0 1 8 0v4M12 14v3" />
              </svg>
              <input
                id="doctor-password"
                formControlName="password"
                [type]="showPassword() ? 'text' : 'password'"
                autocomplete="current-password"
                placeholder="Enter your password"
                maxlength="1024"
                [attr.aria-invalid]="form.controls.password.touched && form.controls.password.invalid"
                aria-describedby="password-validation"
              />
              <button
                class="password-toggle"
                type="button"
                (click)="showPassword.set(!showPassword())"
                [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'"
                [attr.aria-pressed]="showPassword()"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Z" />
                  <circle cx="12" cy="12" r="3" />
                  @if (showPassword()) {
                    <path d="M3 3 21 21" />
                  }
                </svg>
              </button>
            </div>
            @if (form.controls.password.touched && form.controls.password.invalid) {
              <p class="field-error" id="password-validation">Enter your password.</p>
            }

            @if (error()) {
              <p class="emr-alert error" role="alert" style="margin-top: 16px;">{{ error() }}</p>
            }

            <button class="emr-button primary sign-in" type="submit" [disabled]="busy()">
              {{ busy() ? 'Signing in…' : 'Sign in' }}
            </button>

            <div class="login-recovery-row">
              <button
                type="button"
                class="emr-text-button"
                (click)="showResetHelp.set(!showResetHelp())"
                [attr.aria-expanded]="showResetHelp()"
              >
                Need password reset assistance?
              </button>

              @if (showResetHelp()) {
                <div class="recovery-help-box" role="note">
                  <strong>Account &amp; Password Support</strong>
                  Self-service password recovery is disabled for clinical security. Contact your
                  Apollo Hospital IT Administrator, or in local development run the protected
                  <code>EmrAdmin</code> seeding utility to re-initialize credentials.
                </div>
              }
            </div>

            <div class="login-security">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <rect x="5" y="11" width="14" height="10" rx="2" />
                <path d="M8 11V7a4 4 0 0 1 8 0v4" />
              </svg>
              <span>
                This is a secure clinical application for authorized users only. All access is
                logged and monitored. Sessions expire automatically after 30 minutes.
              </span>
            </div>
          </form>
        </section>
      </main>
    </div>
  `,
})
export class DoctorLogin {
  private api = inject(EmrApi);
  private router = inject(Router);
  readonly form = new FormGroup({
    login: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(256)],
    }),
    password: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(1024)],
    }),
  });
  showPassword = signal(false);
  showResetHelp = signal(false);
  busy = signal(false);
  error = signal('');

  async signIn(): Promise<void> {
    if (this.busy()) return;
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const v = this.form.getRawValue();
      await this.api.login(v.login, v.password);
      this.form.controls.password.reset();
      await this.router.navigate(['/emr']);
    } catch (e) {
      const err = e as { status: number; error?: { detail?: string } };
      this.error.set(
        err.status === 401
          ? 'Unable to sign in. Check your credentials or try again later.'
          : apiError(e),
      );
    } finally {
      this.busy.set(false);
    }
  }
}

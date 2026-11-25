import { Component, computed, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { EmrApi } from './emr-api';

@Component({
  selector: 'emr-restricted-view',
  standalone: true,
  imports: [RouterLink],
  template: `
    <main class="emr-page">
      <div class="emr-page-heading">
        <div>
          <p class="emr-eyebrow">{{ eyebrow() }}</p>
          <h1>{{ heading() }}</h1>
          <p class="emr-description">{{ subtitle() }}</p>
        </div>
        <a class="emr-button primary" routerLink="/emr">Back to Doctor Dashboard</a>
      </div>

      <section class="restricted-card" role="region" aria-labelledby="restricted-title">
        <div class="restricted-icon" aria-hidden="true">
          <svg viewBox="0 0 24 24">
            <rect x="4" y="11" width="16" height="10" rx="2" />
            <path d="M8 11V7a4 4 0 0 1 8 0v4" />
          </svg>
        </div>
        <div class="restricted-body">
          <span class="status-label draft">Role Boundary Enforced</span>
          <h2 id="restricted-title">{{ noticeTitle() }}</h2>
          <p>{{ noticeBody() }}</p>
          <dl class="restricted-meta">
            <div>
              <dt>Signed-in account</dt>
              <dd>{{ api.doctor()?.displayName || 'Unknown' }} ({{ api.doctor()?.login || '—' }})</dd>
            </div>
            <div>
              <dt>Assigned role</dt>
              <dd>{{ api.doctor()?.role || 'Doctor' }}</dd>
            </div>
            <div>
              <dt>Required role</dt>
              <dd>{{ requiredRole() }}</dd>
            </div>
          </dl>
          <div class="restricted-actions">
            <a class="emr-button primary" routerLink="/emr">Return to OP EMR</a>
            <a class="emr-button secondary" routerLink="/extraction">View Assigned Extractions</a>
          </div>
        </div>
      </section>
    </main>
  `,
})
export class RestrictedView {
  readonly api = inject(EmrApi);
  private readonly route = inject(ActivatedRoute);
  private readonly section = this.route.snapshot.data['section'] || 'admin';

  readonly eyebrow = computed(() =>
    this.section === 'admin' ? 'PLATFORM ADMINISTRATION' : 'ROLE ACCESS POLICY',
  );
  readonly heading = computed(() =>
    this.section === 'admin' ? 'Administration' : 'Restricted Workspace',
  );
  readonly subtitle = computed(() =>
    this.section === 'admin'
      ? 'System configuration, user management, and quarantine oversight.'
      : 'This workspace is restricted by backend role policy.',
  );
  readonly requiredRole = computed(() =>
    this.section === 'admin' ? 'Administrator / ClinicalAdministrator' : 'ClinicalReviewer',
  );
  readonly noticeTitle = computed(() =>
    this.section === 'admin'
      ? 'Administrator privileges required'
      : 'Clinical Reviewer privileges required',
  );
  readonly noticeBody = computed(() =>
    this.section === 'admin'
      ? 'Your account holds the Doctor role for outpatient care documentation. System administration, quarantine management, and provider configuration are restricted to Administrator accounts and are enforced by ASP.NET Core authorization policies.'
      : 'Direct unanchored extractions and cross-doctor validation queues are restricted to authorized Clinical Reviewer accounts. Outpatient physicians document and sign notes within OP EMR and inspect extraction results linked to their assigned encounters.',
  );
}

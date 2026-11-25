import { Component, computed, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { EmrApi } from './emr-api';

@Component({
  selector: 'apollo-brand',
  standalone: true,
  template: `
    <span class="apollo-lockup">
      <svg class="apollo-emblem" viewBox="0 0 36 36" aria-hidden="true">
        <path d="M18 3C13 3 9 7 9 12c0 3 1.5 5.4 4 7l-1.5 11.5h13L23 19c2.5-1.6 4-4 4-7 0-5-4-9-9-9Z" fill="currentColor" stroke="none" opacity="0.14"/>
        <path d="M18 5v24M12 11h12M14 17h8M15 29h6" stroke="currentColor" stroke-width="2.1" stroke-linecap="round"/>
        <circle cx="18" cy="7.5" r="2.2" fill="currentColor" stroke="none"/>
      </svg>
      <span class="apollo-text-stack">
        <span class="apollo-wordmark">Apollo</span>
        <span class="apollo-hospitals">Hospitals<sup class="reg-mark">®</sup></span>
      </span>
    </span>
  `,
})
export class ApolloBrand { }

/**
 * Stage 12 minimal root shell: renders the active single-screen workspace directly
 * without legacy multi-page OP EMR chrome (no 5-tab bar, no left sidebar, no login wall).
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `<router-outlet />`,
})
export class PortalShell {
  readonly api = inject(EmrApi);
  readonly isAdmin = computed(() => {
    const role = this.api.doctor()?.role || '';
    return ['Administrator', 'ClinicalAdministrator'].includes(role);
  });

  initials(name: string): string {
    const parts = name.trim().split(/\s+/);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return `${parts[0][0] ?? ''}${parts[parts.length - 1][0] ?? ''}`.toUpperCase();
  }
}

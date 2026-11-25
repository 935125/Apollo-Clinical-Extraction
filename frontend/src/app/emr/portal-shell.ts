import { Component, computed, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { EmrApi } from './emr-api';

@Component({
  selector: 'apollo-brand',
  standalone: true,
  template: `
    <span class="apollo-lockup">
      <img
        class="apollo-logo-img"
        src="apollo-logo.png"
        alt="Apollo Hospitals"
      />
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

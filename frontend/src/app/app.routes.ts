import { Routes } from '@angular/router';

/**
 * Stage 12 Single-Screen Clinical Extraction Queue Routing.
 * Opens directly to the live Clinical Extraction Queue at '/'.
 * All obsolete multi-page Doctor OP EMR / Login / Review / Admin paths redirect cleanly to '/'.
 */
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () =>
      import('./queue/clinical-queue').then((m) => m.ClinicalQueueComponent),
  },
  {
    path: 'standalone',
    loadComponent: () => import('./app').then((m) => m.App),
  },
  { path: '**', redirectTo: '' },
];

import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { PortalShell } from './app/emr/portal-shell';

bootstrapApplication(PortalShell, appConfig)
  .catch((err) => console.error(err));

import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { EmrApi, apiError } from './emr-api';

@Component({selector:'apollo-brand', standalone:true, template:`<span class="apollo-wordmark">Apollo</span><span class="apollo-hospitals">HOSPITALS</span>`})
export class ApolloBrand {}
@Component({selector:'app-root', standalone:true, imports:[RouterOutlet, RouterLink, RouterLinkActive, ApolloBrand], template:`
  @if (api.doctor(); as doctor) {
    <header class="portal-header">
      <a class="portal-brand" routerLink="/emr" aria-label="Apollo Hospitals home"><apollo-brand /></a>
      <span class="header-rule" aria-hidden="true"></span>
      <nav aria-label="Main navigation">
        <a routerLink="/emr" routerLinkActive="selected" [routerLinkActiveOptions]="{exact:true}">OP EMR</a>
        <a routerLink="/extraction" routerLinkActive="selected">Clinical Extraction</a>
        <a routerLink="/history" routerLinkActive="selected">History</a>
      </nav>
      <div class="doctor-menu">
        <button class="notification-bell" type="button" aria-label="Notifications" title="Notifications">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>
        </button>
        <button class="profile-button" (click)="profileOpen.set(!profileOpen())" [attr.aria-expanded]="profileOpen()" aria-controls="doctor-profile">
          <span class="avatar">{{ initials(doctor.displayName) }}</span><span class="profile-name">{{doctor.displayName}}</span><span aria-hidden="true">⌄</span>
        </button>
        @if(profileOpen()) {<div class="profile-popover" id="doctor-profile"><strong>{{doctor.displayName}}</strong><span>{{doctor.role}}</span><span>{{doctor.login}}</span><p>Local demonstration session · 30 minutes</p><button class="emr-button secondary" (click)="logout()" [disabled]="busy()">{{busy() ? 'Signing out…' : 'Sign out'}}</button>@if(error()) {<p role="alert">{{error()}}</p>}</div>}
      </div>
    </header>
  }
  <div [class.portal-clinical-content]="api.doctor() && (router.url.startsWith('/extraction') || router.url.startsWith('/history'))"><router-outlet /></div>
  @if(api.doctor()) {<footer class="portal-footer"><span>Apollo Clinical Extraction Platform</span><span>Synthetic demonstration data · Local authentication</span></footer>}
`})
export class PortalShell {
  readonly api=inject(EmrApi); readonly router=inject(Router);
  profileOpen=signal(false); busy=signal(false); error=signal('');
  initials(name:string) {
    const parts = name.split(/[\s.]+/).filter(Boolean);
    if (parts.length >= 2 && /^dr$/i.test(parts[0])) return 'D' + parts[parts.length - 1][0].toUpperCase();
    return parts.slice(0, 2).map(p => p[0].toUpperCase()).join('');
  }
  async logout() { this.busy.set(true); this.error.set(''); try { await this.api.logout(); this.profileOpen.set(false); await this.router.navigate(['/login']); } catch(e) {this.error.set(apiError(e));} finally {this.busy.set(false);} }
}

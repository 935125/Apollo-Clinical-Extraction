import { Routes } from '@angular/router';
import { doctorGuard, unsavedGuard } from './emr/emr-api';

export const routes: Routes = [
  {path:'',pathMatch:'full',redirectTo:'emr'},
  {path:'login',loadComponent:()=>import('./emr/login').then(m=>m.DoctorLogin)},
  {path:'emr',canActivate:[doctorGuard],loadComponent:()=>import('./emr/dashboard').then(m=>m.DoctorDashboard)},
  {path:'emr/encounters/:id/new',canActivate:[doctorGuard],canDeactivate:[unsavedGuard],data:{new:true},loadComponent:()=>import('./emr/note-editor').then(m=>m.NoteEditor)},
  {path:'emr/notes/:id/status',canActivate:[doctorGuard],loadComponent:()=>import('./emr/extraction-status').then(m=>m.ExtractionStatus)},
  {path:'emr/notes/:id/edit',canActivate:[doctorGuard],canDeactivate:[unsavedGuard],loadComponent:()=>import('./emr/note-editor').then(m=>m.NoteEditor)},
  {path:'emr/notes/:id',pathMatch:'full',redirectTo:'emr/notes/:id/edit'},
  {path:'extraction',canActivate:[doctorGuard],data:{mode:'extractions'},loadComponent:()=>import('./emr/dashboard').then(m=>m.DoctorDashboard)},
  {path:'extraction/result',canActivate:[doctorGuard],loadComponent:()=>import('./app').then(m=>m.App)},
  {path:'history',canActivate:[doctorGuard],data:{mode:'history'},loadComponent:()=>import('./emr/dashboard').then(m=>m.DoctorDashboard)},
  {path:'standalone',loadComponent:()=>import('./app').then(m=>m.App)},
  {path:'**',redirectTo:'emr'},
];

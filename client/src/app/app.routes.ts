import { Routes } from '@angular/router';

// Auth (later phase): add `canActivate` guards on these routes; nothing else needs to move.
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Payroll dashboard',
    loadComponent: () => import('./features/dashboard').then((m) => m.Dashboard),
  },
  {
    path: 'batches/:id',
    title: 'Batch details',
    loadComponent: () => import('./features/batches/batch-detail').then((m) => m.BatchDetail),
  },
  { path: '**', redirectTo: '' },
];

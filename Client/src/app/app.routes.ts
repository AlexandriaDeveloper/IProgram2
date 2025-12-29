import { Routes } from '@angular/router';
import { LoginComponent } from './account/login/login.component';
import { authGuard } from './shared/guards/auth.guard';

export const routes: Routes = [
  {
    path: 'account/login',
    component: LoginComponent
  },
  {
    path: '',
    loadComponent: () => import('./dashboard/dashboard.component').then(m => m.DashboardComponent),
    canActivate: [authGuard]
  },

  //lazyloading
  {
    path: 'account',
    loadChildren: () => import('./account/account.module').then(m => m.AccountModule)

  },
  {
    path: 'employee',
    loadChildren: () => import('./employee/employee.module').then(m => m.EmployeeModule)

  }
  ,
  {
    path: 'daily',
    loadChildren: () => import('./daily/daily.module').then(m => m.DailyModule)

  }
  ,
  {
    path: 'department',
    loadChildren: () => import('./department/department.module').then(m => m.DepartmentModule)

  }
  ,
  {
    path: 'settings',
    loadChildren: () => import('./settings/settings.module').then(m => m.SettingsModule)

  }
];

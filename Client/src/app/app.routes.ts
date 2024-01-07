import { Routes } from '@angular/router';
import { LoginComponent } from './account/login/login.component';

export const routes: Routes = [
  {
    path: 'account/login',
    component: LoginComponent
  },

  //lazyloading
  {
    path:'account',
    loadChildren: () => import('./account/account.module').then(m => m.AccountModule)

  },
  {
    path:'employee',
    loadChildren: () => import('./employee/employee.module').then(m => m.EmployeeModule)

  }
  ,
  {
    path:'daily',
    loadChildren: () => import('./daily/daily.module').then(m => m.DailyModule)

  }
];

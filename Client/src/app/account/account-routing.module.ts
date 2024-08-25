import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LoginComponent } from './login/login.component';
import { RegisterComponent } from './register/register.component';
import { authGuard } from '../shared/guards/auth.guard';
import { ChangePasswordComponent } from './change-password/change-password.component';

const routes: Routes = [{
  path :'login',
  component:LoginComponent,
},
{path:'register',
component:RegisterComponent,
canActivate: [
  authGuard
]
},
{
  path:'change-password',
  component:ChangePasswordComponent,
  },
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class AccountRoutingModule { }

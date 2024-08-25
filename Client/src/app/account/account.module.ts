import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';

import { AccountRoutingModule } from './account-routing.module';
import { SharedModule } from '../shared/shared.module';
import { RegisterComponent } from './register/register.component';
import { LoginComponent } from './login/login.component';
import { ChangePasswordComponent } from './change-password/change-password.component';


@NgModule({
  declarations: [RegisterComponent,LoginComponent,ChangePasswordComponent],
  imports: [
    SharedModule,
    AccountRoutingModule
  ]
})
export class AccountModule { }

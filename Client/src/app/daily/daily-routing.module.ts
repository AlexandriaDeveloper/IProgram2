import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { FormComponent } from './form/form.component';
import { DailyComponent } from './daily/daily.component';
import { FormDetailsComponent } from './form/form-details/form-details.component';
import { ArchivedFormsComponent } from './archived-forms/archived-forms.component';

const routes: Routes = [
  {path:'',component:DailyComponent},

  {path:':id/form',component:FormComponent},
  {path:'archivedform',component:ArchivedFormsComponent,},
  {path:':id/form/:formid',component:FormDetailsComponent},
  {path:':form/:formid',component:FormDetailsComponent}
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class DailyRoutingModule { }

import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DailyRoutingModule } from './daily-routing.module';
import { SharedModule } from '../shared/shared.module';
import { FormComponent } from './form/form.component';
import { DailyComponent } from './daily/daily.component';
import { InputTextComponent } from '../shared/components/input-text/input-text.component';
import { AddDailyComponent } from './add-daily/add-daily.component';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { AddFormComponent } from './form/add-form/add-form.component';
import { FormDetailsComponent } from './form/form-details/form-details.component';
import { DescriptionDialogComponent } from './form/form-details/description-dialog/description-dialog.component';
import { AddEmployeeDialogComponent } from './form/form-details/add-employee-dialog/add-employee-dialog.component';
import { ArchivedFormsComponent } from './archived-forms/archived-forms.component';
import { MoveToDailyDialogComponent } from './archived-forms/move-to-daily-dialog/move-to-daily-dialog.component';




@NgModule({
  declarations: [
    DailyComponent,
    AddFormComponent,
    FormComponent,
    AddEmployeeDialogComponent,
    DescriptionDialogComponent,
    ArchivedFormsComponent,
     FormDetailsComponent,
     MoveToDailyDialogComponent
],
  imports: [
    MatDatepickerModule ,
    AddDailyComponent,
    CommonModule,
    DailyRoutingModule,
    SharedModule,
    InputTextComponent,
  ]
})
export class DailyModule { }

import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AddEmployeeComponent } from './add-employee/add-employee.component';
import { SharedModule } from '../shared/shared.module';
import { EmployeeRoutingModule } from './employee.route';
import { InputTextComponent } from '../shared/components/input-text/input-text.component';
import { UploadEmployeeComponent } from './upload-employee/upload-employee.component';
import { DndDirective } from '../shared/directives/dnd.directive';
import { UploadComponent } from '../shared/components/upload/upload.component';
import { ListComponent } from './list/list.component';
import { EmployeeDetailsComponent } from './list/employee-details/employee-details.component';
import { EmployeeReportComponent } from './list/employee-details/employee-report/employee-report.component';
import { EmployeeInfoComponent } from './list/employee-details/employee-info/employee-info.component';
import { EmployeeReferencesComponent } from './list/employee-details/employee-references/employee-references.component';
import { BankInfoComponent } from './list/employee-details/bank-info/bank-info.component';
import { UploadEmployeeReferncesDialogComponent } from './list/employee-details/employee-references/upload-employee-refernces-dialog/upload-employee-refernces-dialog.component';
import { UploadEmployeeTegaraComponent } from './upload-employee-tegara/upload-employee-tegara.component';



@NgModule({
  declarations: [AddEmployeeComponent,UploadEmployeeComponent,UploadEmployeeTegaraComponent,ListComponent,EmployeeInfoComponent,
      EmployeeDetailsComponent, EmployeeDetailsComponent,EmployeeReportComponent,EmployeeReferencesComponent,BankInfoComponent,UploadEmployeeReferncesDialogComponent],
  imports: [
    SharedModule,
    CommonModule,
    EmployeeRoutingModule,
    UploadComponent,




  ]
})
export class EmployeeModule { }

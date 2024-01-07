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



@NgModule({
  declarations: [AddEmployeeComponent,UploadEmployeeComponent,ListComponent],
  imports: [
    SharedModule,
    CommonModule,
    EmployeeRoutingModule,
    UploadComponent



  ]
})
export class EmployeeModule { }

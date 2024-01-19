import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';

import { DepartmentRoutingModule } from './department-routing.module';
import { ListComponent } from './list/list.component';
import { SharedModule } from '../shared/shared.module';
import { AddDepartmentDialogComponent } from './list/add-department-dialog/add-department-dialog.component';
import { EmployeesDepartmentComponent } from './list/employees-department/employees-department.component';
import { AddEmployeeDialogComponent } from './list/employees-department/add-employee-dialog/add-employee-dialog.component';


@NgModule({
  declarations: [ListComponent,AddDepartmentDialogComponent,EmployeesDepartmentComponent,AddEmployeeDialogComponent],
  imports: [
    CommonModule,
    DepartmentRoutingModule,
    SharedModule
  ]
})
export class DepartmentModule { }

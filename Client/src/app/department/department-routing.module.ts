import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { ListComponent } from './list/list.component';
import { EmployeesDepartmentComponent } from './list/employees-department/employees-department.component';

const routes: Routes = [
  {path: '',component:ListComponent},
  {path:'employees-department/:id',component:EmployeesDepartmentComponent,data:{title:''}}
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class DepartmentRoutingModule { }

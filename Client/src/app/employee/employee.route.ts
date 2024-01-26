import { NgModule } from "@angular/core";
import { authGuard } from "../shared/guards/auth.guard";
import { AddEmployeeComponent } from "./add-employee/add-employee.component";
import { RouterModule, Routes } from "@angular/router";
import { UploadEmployeeComponent } from "./upload-employee/upload-employee.component";
import { ListComponent } from "./list/list.component";
import { EmployeeDetailsComponent } from "./list/employee-details/employee-details.component";
import { UploadEmployeeTegaraComponent } from "./upload-employee-tegara/upload-employee-tegara.component";

const routes: Routes = [
{path:'add',
component:AddEmployeeComponent,

},
{
  path:'upload',
  component:UploadEmployeeComponent,


},
{
  path:'upload-tegara',
  component:UploadEmployeeTegaraComponent,
}
,{
path:'list',
component :ListComponent}
,{
  path:'details/:id',
  component :EmployeeDetailsComponent}
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class EmployeeRoutingModule { }

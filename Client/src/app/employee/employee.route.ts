import { NgModule } from "@angular/core";
import { authGuard } from "../shared/guards/auth.guard";
import { AddEmployeeComponent } from "./add-employee/add-employee.component";
import { RouterModule, Routes } from "@angular/router";
import { UploadEmployeeComponent } from "./upload-employee/upload-employee.component";
import { ListComponent } from "./list/list.component";

const routes: Routes = [
{path:'add',
component:AddEmployeeComponent,

},
{
  path:'upload',
  component:UploadEmployeeComponent,


},{
path:'list',
component :ListComponent}
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class EmployeeRoutingModule { }

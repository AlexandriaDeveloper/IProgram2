import { Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { ReportpdfService } from '../../../shared/service/reportpdf.service';
import { EmployeeService } from '../../../shared/service/employee.service';
import { EmployeeParam, EmployeeReportRequest, IEmployee } from '../../../shared/models/IEmployee';

@Component({
  selector: 'app-employee-details',
  standalone: false,

  templateUrl: './employee-details.component.html',
  styleUrl: './employee-details.component.scss'
})
export class EmployeeDetailsComponent implements OnInit{

employeeId =inject(ActivatedRoute).snapshot.params['id'];

employeeService =inject(EmployeeService);
param  :EmployeeParam =new EmployeeParam();

employee ?:IEmployee;
ngOnInit(): void {
  this.param.id=this.employeeId
  this.employeeService.GetEmployee(this.param).subscribe(x=>{
    this.employee=x;

  })
}

}

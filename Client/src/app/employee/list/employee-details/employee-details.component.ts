import { Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
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

  router = inject(Router);
employeeId =inject(ActivatedRoute).snapshot.params['id'];

employeeService =inject(EmployeeService);
param  :EmployeeParam =new EmployeeParam();

employee ?:IEmployee;
ngOnInit(): void {
  this.param.id=this.employeeId
  this.employeeService.GetEmployee(this.param).subscribe({
    next:(x)=>{
      this.employee=x
    },
    error:(err)=>{
      console.log(err);

      if(err.status==404){
       this.router.navigate(['/employee'])
      }
    },
    complete  : ()=>{
    console.log('ended');
    }
  })
}

}

import { Component, Input, OnInit, inject } from '@angular/core';
import { EmployeeReportRequest, IEmployee } from '../../../../shared/models/IEmployee';
import { EmployeeService } from '../../../../shared/service/employee.service';

@Component({
  selector: 'app-employee-info',
  standalone: false,
  templateUrl: './employee-info.component.html',
  styleUrl: './employee-info.component.scss'
})
export class EmployeeInfoComponent  implements OnInit {
  @Input("employee") employee:IEmployee;
  ngOnInit(): void {

  }


}

import { Component, Input } from '@angular/core';
import { IEmployee } from '../../../../shared/models/IEmployee';

@Component({
  selector: 'app-employee-info',
  standalone: false,
  templateUrl: './employee-info.component.html',
  styleUrl: './employee-info.component.scss'
})
export class EmployeeInfoComponent {
  @Input("employee") employee:IEmployee;

}

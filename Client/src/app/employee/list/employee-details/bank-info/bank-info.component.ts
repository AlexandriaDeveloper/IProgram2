import { Component, Input } from '@angular/core';
import { IEmployee } from '../../../../shared/models/IEmployee';

@Component({
  selector: 'app-bank-info',
  standalone: false,

  templateUrl: './bank-info.component.html',
  styleUrl: './bank-info.component.scss'
})
export class BankInfoComponent {
  @Input('employee') employee:IEmployee

}

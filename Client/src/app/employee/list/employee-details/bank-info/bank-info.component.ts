import { Component, Input, inject } from '@angular/core';
import { IEmployee } from '../../../../shared/models/IEmployee';
import { EmployeeBankService } from '../../../../shared/service/employee-bank.service';
import { ToasterService } from '../../../../shared/components/toaster/toaster.service';
import { Router } from '@angular/router';
import { EmployeeDetailsComponent } from '../employee-details.component';

@Component({
  selector: 'app-bank-info',
  standalone: false,

  templateUrl: './bank-info.component.html',
  styleUrl: './bank-info.component.scss'
})
export class BankInfoComponent {
  @Input('employee') employee:IEmployee
  employeeBankServic = inject(EmployeeBankService)
  toaster = inject(ToasterService)
  employeeDetails =new EmployeeDetailsComponent();

//router = inject(Router)
  deleteBank(){
    if(confirm("انت على وشك حذف البيانات البنكيه للموظف هل انت متأكد ؟!!!")){
    this.employeeBankServic.deleteEmployeeBank(this.employee?.id).subscribe(res=>{

      setTimeout(() => {
        window.location.reload()
      }, 600);

      this.toaster.openSuccessToaster('تم الحذف بنجاح','check')

    })}
  }

}

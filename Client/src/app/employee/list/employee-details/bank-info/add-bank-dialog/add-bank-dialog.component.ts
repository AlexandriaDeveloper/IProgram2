import { Component, Inject, OnInit, inject } from '@angular/core';
import { FormGroup, FormBuilder, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { AddDailyComponent } from '../../../../../daily/add-daily/add-daily.component';
import { EmployeeBank } from '../../../../../shared/models/EmployeeBank';
import { EmployeeBankService } from '../../../../../shared/service/employee-bank.service';

@Component({
  selector: 'app-add-bank-dialog',
  standalone: false,

  templateUrl: './add-bank-dialog.component.html',
  styleUrl: './add-bank-dialog.component.scss'
})
export class AddBankDialogComponent implements OnInit {

  form : FormGroup;
  fb =  inject(FormBuilder);
  employeeBank = new EmployeeBank();
  employeeBankService =inject(EmployeeBankService)

  constructor(private dialogRef : MatDialogRef<AddBankDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
    ) {

    }
  ngOnInit(): void {
    this.employeeBank.employeeId=this.data.employeeId;
    this.form=this.initForm()
  }

  initForm(){
    return this.fb.group({
      employeeId:[this.employeeBank.employeeId,Validators.required],
      accountNumber:[this.employeeBank.accountNumber,Validators.required],
      bankName:[this.employeeBank.bankName,Validators.required],
      branchName:[this.employeeBank.branchName,Validators.required]
    })
  }
  onNoClick(): void {
    this.dialogRef.close();
  }
  onSubmit(){


    this.employeeBankService.addEmployeeBank(this.form.value).subscribe(res=>{
      this.dialogRef.close();
    })

  }
}

import { FormDetailsService } from './../../../../shared/service/form-details.service';
import { Component, Inject, OnInit, inject } from '@angular/core';
import { FormGroup, FormBuilder, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import moment from 'moment';
import { IDaily } from '../../../../shared/models/IDaily';
import { DailyService } from '../../../../shared/service/daily.service';
import { AddDailyComponent } from '../../../add-daily/add-daily.component';
import { EmployeeParam, IEmployee, IEmployeeSearch } from '../../../../shared/models/IEmployee';
import { EmployeeService } from '../../../../shared/service/employee.service';
import { AddEmployeeDetails } from '../../../../shared/models/EmployeeDetails';

@Component({
  selector: 'app-add-employee-dialog',
  standalone: false,
  templateUrl: './add-employee-dialog.component.html',
  styleUrl: './add-employee-dialog.component.scss'
})
export class AddEmployeeDialogComponent implements OnInit {

  employeeSearchForm : FormGroup;
  form : FormGroup;
  fb =  inject(FormBuilder);
  dailyService =inject(DailyService);
  formDetailsService =inject(FormDetailsService);
  employeeService =inject(EmployeeService);
  employeeParam : EmployeeParam=new EmployeeParam();
  employeeSearch :IEmployeeSearch={
    employeeId:null,
    tegaraCode:null,
    tabCode:null
  };
  employee: IEmployee;
  employeeDetails :AddEmployeeDetails=null;
  //@ViewChild("nameInput") nameInput :ElementRef;
  //@ViewChild("dailyDateInput") dailyDateInput :ElementRef;

  constructor(
    private dialogRef: MatDialogRef<AddDailyComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
    ) {

    }
  ngOnInit(): void {
    // console.log(this.data?.employeeDetails?.amount);

    if(this.data.employeeDetails !== null){
   // this.employeeDetails=new AddEmployeeDetails();
     this.employee={...this.employee
      ,id : this.data?.employeeDetails?.employeeId,
      name : this.data?.employeeDetails?.name,

      tegaraCode : this.data?.employeeDetails?.tegaraCode,
      tabCode : this.data?.employeeDetails?.tabCode,
      collage : this.data?.employeeDetails?.collage}

     this.employeeDetails={
       ...this.employeeDetails,
       id:this.data?.employeeDetails?.id,
       employeeId:this.data?.employeeDetails?.employeeId,
       formId:this.data?.employeeDetails?.formId,
       amount:this.data?.employeeDetails?.amount
     }
       this.form= this.initEmployeeForm();
    }


   this.employeeSearchForm=this.initForm();
  }
    initForm(){
      return this.fb.group({
        nationalId:[this.employeeSearch?.employeeId,[]],
        tegaraCode : [this.employeeSearch?.tegaraCode,[]],
        tabCode : [this.employeeSearch?.tabCode,[]]
      })
    }
  onNoClick(){
    this.dialogRef.close();
  }
  onEmployeeSearch(){
    this.employeeParam=Object.assign(this.employeeParam,this.employeeSearchForm.value);
    this.loadEmployee();


  }

  loadEmployee(){
    this.employeeService.GetEmployee(this.employeeParam).subscribe({
      next:(x:IEmployee)=>{
        this.employee= x;
        this.employeeDetails={
          id:this.data.employeeDetails?.id?this.data.employeeDetails?.id:0,
          employeeId:x.id,
          formId:this.data?.formId,
          amount:this.employeeDetails?.amount??0,

        }
      this.form=  this.initEmployeeForm();

      },
      error:(err)=> console.log(err),


    });
  }
  initEmployeeForm(){
   return this.fb.group({
    id:[this.employeeDetails?.id,[]],
      employeeId : [this.employeeDetails?.employeeId,[]],
      formId:[this.employeeDetails?.formId,[]],
      amount :[this.employeeDetails?.amount,Validators.required]
    })
  }
  onSubmit(){
// console.log(this.form.value);
if(this.form.value.id ===0){
  this.formDetailsService.addEmployeeToFormDetails(this.form.value).subscribe(x =>{
    this.dialogRef.close();
  })
}
else{
  this.formDetailsService.editEmployeeToFormDetails(this.form.value).subscribe(x =>{
    this.dialogRef.close();
  })
}


  }
}

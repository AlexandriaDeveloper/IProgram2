import { AfterViewInit, Component, Inject, OnInit, inject } from '@angular/core';
import { FormGroup, FormBuilder, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { AddDailyComponent } from '../../../../daily/add-daily/add-daily.component';
import { AddEmployeeDetails } from '../../../../shared/models/EmployeeDetails';
import { EmployeeParam, IEmployeeSearch, IEmployee } from '../../../../shared/models/IEmployee';
import { DailyService } from '../../../../shared/service/daily.service';
import { EmployeeService } from '../../../../shared/service/employee.service';
import { FormDetailsService } from '../../../../shared/service/form-details.service';
import { DepartmentService } from '../../../../shared/service/department.service';

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
  departmentService = inject(DepartmentService)
  employeeParam : EmployeeParam=new EmployeeParam();
  employeeSearch :IEmployeeSearch={
    nationalId:null,
    tegaraCode:null,
    tabCode:null
  };
  employee: IEmployee;
  employeeDetails :AddEmployeeDetails=null;
  ids :number[]=[];
  //@ViewChild("nameInput") nameInput :ElementRef;
  //@ViewChild("dailyDateInput") dailyDateInput :ElementRef;

  constructor(
    private dialogRef: MatDialogRef<AddDailyComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
    ) {

    }
  ngOnInit(): void {
    // console.log(this.data?.employeeDetails?.amount);



   this.employeeSearchForm=this.initForm();
  }
    initForm(){
      return this.fb.group({
        nationalId:[this.employeeSearch?.nationalId,[]],
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
// console.log(x);

      this.form=  this.initEmployeeForm();

      },
      error:(err)=> console.log(err),


    });
  }
  initEmployeeForm(){
   return this.fb.group({

      employeeId : [this.employee?.id,[]],
      departmentId:[this.data.departmentId,[]]
    })
  }
  onSubmit(){
    this.ids.push(this.form.value.employeeId);
      this.departmentService.addEmployeesToDepartment(this.data.departmentId, this.ids).subscribe({
        next:(x)=>{
          this.dialogRef.close();
        },
        error:(err)=> console.log(err),
      })
  }
}

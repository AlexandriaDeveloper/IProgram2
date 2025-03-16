import { Component, Inject, OnInit, inject } from '@angular/core';
import { FormGroup, FormBuilder, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { EmployeeBank } from '../../../../shared/models/EmployeeBank';
import { EmployeeBankService } from '../../../../shared/service/employee-bank.service';
import { AddBankDialogComponent } from '../bank-info/add-bank-dialog/add-bank-dialog.component';
import { DepartmentService } from '../../../../shared/service/department.service';
import { EmployeeService } from '../../../../shared/service/employee.service';
import { EmployeeParam, IEmployee } from '../../../../shared/models/IEmployee';

@Component({
  selector: 'app-edit-employee-dialog',
  standalone: false,

  templateUrl: './edit-employee-dialog.component.html',
  styleUrl: './edit-employee-dialog.component.scss'
})
export class EditEmployeeDialogComponent implements OnInit {

  form: FormGroup;
  fb = inject(FormBuilder);
  employeeService = new EmployeeService();
  employeeBankService = inject(EmployeeBankService)
  departmentService = inject(DepartmentService)
  departments;
  employeeParam: EmployeeParam = new EmployeeParam();
  employee: IEmployee = {
    name: '',
    tabCode: null,
    tegaraCode: null,
    id: null,
    collage: '',
    departmentId: null,
    email: null,

  };
  constructor(private dialogRef: MatDialogRef<AddBankDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
  ) {

  }
  ngOnInit(): void {
    this.employeeParam.id = this.data.employeeId;



    this.loadDepartrments();
    this.loadEmployee();
    this.form = this.initForm()
  }

  initForm() {
    return this.fb.group({
      employeeId: [this.employee?.id, []],
      name: [this.employee?.name, [Validators.required]],
      collage: [this.employee?.collage, []],
      tabCode: [this.employee?.tabCode, []],
      tegaraCode: [this.employee?.tegaraCode, []],
      section: [this.employee?.section, []],
      departmentId: [this.employee?.departmentId, []],
      email: [this.employee?.email, []]
    })
  }
  loadEmployee() {
    console.log(this.data.employeeId);

    this.employeeService.GetEmployee(this.employeeParam).subscribe({
      next: (res: any) => {
        this.employee = res;
        console.log(res);

        this.form = this.initForm();
      },
      error: (err) => console.log(err)
    })
  }
  loadDepartrments() {

    this.departmentService.getAllDepartments().subscribe({
      next: (res: any) => {
        console.log(res);

        this.departments = res;
      },
      error: (err) => console.log(err)
    })
  }
  onNoClick(): void {
    this.dialogRef.close();
  }
  onSubmit() {

    console.log(this.form.value);
    if (this.form.value.tegaraCode === '') {

      this.form.value.tegaraCode = null
    }
    if (this.form.value.tabCode === '') {

      this.form.value.tabCode = null
    }
    if (this.form.value.email === '') {

      this.form.value.email = null
    }
    this.employeeService.updateEmployee(this.form.value).subscribe(res => {
      this.dialogRef.close();
    })

  }
}

import { Component, OnInit, inject } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { IEmployee } from '../../shared/models/IEmployee';
import { EmployeeService } from '../../shared/service/employee.service';
import { ToasterService } from '../../shared/components/toaster/toaster.service';
import { DepartmentService } from '../../shared/service/department.service';


@Component({
  selector: 'app-add-employee',
  standalone: false,

  templateUrl: './add-employee.component.html',
  styleUrl: './add-employee.component.scss'
})
export class AddEmployeeComponent implements OnInit {
  fb = inject(FormBuilder)
  employeeService = inject(EmployeeService)
  departmentService = inject(DepartmentService)

  toaster = inject(ToasterService)

  employee: IEmployee = {
    name: null,
    tabCode: null,
    tegaraCode: null,
    id: null,
    collage: null,
    departmentId: null
  };
  form: FormGroup;
  departments: any[] = [];

  ngOnInit(): void {
    this.loadDepartrments();
    this.form = this.initForm();

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

  initForm() {
    return this.fb.group({
      name: [this.employee?.name, [Validators.required]],
      collage: [this.employee?.collage, []],
      tabCode: [this.employee?.tabCode, []],
      tegaraCode: [this.employee?.tegaraCode, []],
      id: [this.employee?.id, [Validators.required, Validators.maxLength(14), Validators.minLength(14)]],
      departmentId: [this.employee?.departmentId, []]
    })
  }

  onSubmit() {

    this.employee = { ...this.employee, ...this.form.value };
    // console.log(this.employee);

    this.employeeService.addEmployee(this.employee).subscribe({
      next: (res) => { this.toaster.openSuccessToaster('تم اضافة الموظف بنجاح', 'check'); this.form.reset(); },

    })
  }

}

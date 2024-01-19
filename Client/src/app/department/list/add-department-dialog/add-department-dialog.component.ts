import { Component, Inject, inject } from '@angular/core';
import { FormGroup, FormBuilder, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';

import { DepartmentService } from '../../../shared/service/department.service';
import { IDepartment } from '../../../shared/models/Department';

@Component({
  selector: 'app-add-department-dialog',
  standalone: false,
  templateUrl: './add-department-dialog.component.html',
  styleUrl: './add-department-dialog.component.scss'
})
export class AddDepartmentDialogComponent {
  form : FormGroup;
  fb =  inject(FormBuilder);
  departmentService =inject(DepartmentService);
  daily :IDepartment={
    id:0,
    name:'',

  };
  //@ViewChild("nameInput") nameInput :ElementRef;
  //@ViewChild("dailyDateInput") dailyDateInput :ElementRef;

  constructor(
    private dialogRef: MatDialogRef<AddDepartmentDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
    ) {

    }
  ngOnInit(): void {
      console.log(this.data);

    if(this.data?.daily !== null){
      console.log(this.data.daily);

      this.daily=Object.assign({...this.daily},this.data.daily);
    }

   this.form=this.initForm();
  }
    initForm(){
      return this.fb.group({
        id:[this.daily?.id,[]],
        name : [this.daily?.name,[Validators.required]],


      })
    }
    onSubmit(){
      if(this.daily.id===0)
      this.departmentService.addDepartment(this.form.value).subscribe({
        next:(res:any)=>{

          this.dialogRef.close(this.form.value);
        },
        error:(err)=>console.log(err)
      })
      else{

        this.departmentService.editDepartment(this.form.value).subscribe({
          next:(res:any)=>{

       this.dialogRef.close(this.form.value);
          },
          error:(err)=>console.log(err)
        })
      }

    }
    onNoClick(){

      this.dialogRef.close();
    }


}

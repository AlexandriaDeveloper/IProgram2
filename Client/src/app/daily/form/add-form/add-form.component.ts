import { Component, Inject, OnInit, inject } from '@angular/core';
import { FormGroup, FormBuilder, Validators } from '@angular/forms';
import { IDaily } from '../../../shared/models/IDaily';
import { IForm } from "../../../shared/models/IForm";
import { DailyService } from '../../../shared/service/daily.service';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { FormService } from '../../../shared/service/form.service';

@Component({
  selector: 'app-add-form',
  standalone: false,

  templateUrl: './add-form.component.html',
  styleUrl: './add-form.component.scss'
})
export class AddFormComponent  implements OnInit{
  form : FormGroup;
  fb =  inject(FormBuilder);
  formService =inject(FormService);
  formData :IForm={
    id:0,
    name:'',
    dailyId:null

  };

  constructor(
    private dialogRef: MatDialogRef<AddFormComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
    ) {

    }

    ngOnInit(): void {

console.log(this.data);


    if(this.data.form){
      this.formData=Object.assign({...this.formData},this.data.form);
    }

   this.form=this.initForm();
  }
    initForm(){
      return this.fb.group({
        id:[this.formData?.id,[]],
        name : [this.formData?.name,[Validators.required]],
       dailyId : [this.data?.dailyId,[]],
      })
    }
    onSubmit(){
      if(this.formData.id ===0)
     {
       this.formService.addForm(this.form.value).subscribe({
        next:(res:any)=>{

          this.dialogRef.close(this.form.value);
        },
        error:(err)=>console.log(err)
      })
    }
      else{

        this.formService.editForm(this.form.value).subscribe({
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

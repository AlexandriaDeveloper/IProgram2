import { DailyComponent } from './../daily/daily.component';
import { Component, ElementRef, Inject, OnInit, ViewChild, inject } from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { DailyService } from '../../shared/service/daily.service';
import { SharedModule } from '../../shared/shared.module';
import { MatMomentDateModule } from '@angular/material-moment-adapter';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { DateAdapter, MatNativeDateModule } from '@angular/material/core';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatCardModule } from '@angular/material/card';
import { IDaily } from '../../shared/models/IDaily';
import moment from 'moment';

@Component({
  selector: 'app-add-daily',
  standalone: true,
  imports: [
    MatDatepickerModule,
    SharedModule,

  ],

  templateUrl: './add-daily.component.html',
  styleUrl: './add-daily.component.scss'
})
export class AddDailyComponent implements OnInit {

  form : FormGroup;
  fb =  inject(FormBuilder);
  dailyService =inject(DailyService);
  daily :IDaily={
    id:0,
    name:'',
    dailyDate:new Date
  };
  //@ViewChild("nameInput") nameInput :ElementRef;
  //@ViewChild("dailyDateInput") dailyDateInput :ElementRef;

  constructor(
    private dialogRef: MatDialogRef<AddDailyComponent>,
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
        dailyDate : [moment(this.daily.dailyDate.toString()).format('YYYY-MM-DD'),[Validators.required]],

      })
    }
    onSubmit(){
      if(this.daily.id===0)
      this.dailyService.addDaily(this.form.value).subscribe({
        next:(res:any)=>{

          this.dialogRef.close(this.form.value);
        },
        error:(err)=>console.log(err)
      })
      else{

        this.dailyService.editDaily(this.form.value).subscribe({
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
    setDate(ev){
      console.log(ev.value);


      this.daily.dailyDate=ev.value;

    }

}

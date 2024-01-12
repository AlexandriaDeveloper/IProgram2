import { Param } from './../../../shared/models/Param';
import { DailyParam, IDaily } from './../../../shared/models/IDaily';
import { Component, Inject, inject } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import moment from 'moment';
import { AddDailyComponent } from '../../add-daily/add-daily.component';
import { DailyService } from '../../../shared/service/daily.service';

@Component({
  selector: 'app-move-to-daily-dialog',
  standalone: false,
  templateUrl: './move-to-daily-dialog.component.html',
  styleUrl: './move-to-daily-dialog.component.scss'
})
export class MoveToDailyDialogComponent {
  dalies : IDaily[]=[];
  param :DailyParam=new DailyParam();
  dailyService = inject(DailyService);
  selectedDaily:IDaily;
  constructor(
    private dialogRef: MatDialogRef<AddDailyComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
    ) {

    }
  ngOnInit(): void {
this.loadData();
  }
  loadData(){
    this.param.closed=false;
    this.param.pageIndex=0;
    this.param.pageSize=100;
    this.dailyService.GetDailies(this.param).subscribe({
      next:(x:any)=>{
        this.dalies=x.data
      }
    })

  }

    onSubmit(){
  this.dialogRef.close(this.selectedDaily);


    }
    onNoClick(){

      this.dialogRef.close();
    }
}

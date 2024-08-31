import { HttpEventType } from '@angular/common/http';
import { Component, ElementRef, inject, Inject, signal, ViewChild } from '@angular/core';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';
import { map } from 'rxjs';
import { UploadEmployeesBottomSheetComponent } from '../../../department/list/employees-department/upload-employees-bottom-sheet/upload-employees-bottom-sheet.component';
import { ToasterService } from '../../../shared/components/toaster/toaster.service';
import { DepartmentService } from '../../../shared/service/department.service';
import { DailyService } from '../../../shared/service/daily.service';

@Component({
  selector: 'app-upload-json-dialog',
  standalone: false,
  templateUrl: './upload-json-dialog.component.html',
  styleUrl: './upload-json-dialog.component.scss'
})
export class UploadJsonDialogComponent {
  @ViewChild('fileInput') fileInput : ElementRef
  dailyService = inject(DailyService)
  toaster = inject(ToasterService)
   fileName : string;

   progress = signal(0);
   onProgress=false;
   @ViewChild("fileDropRef", { static: false }) fileDropEl: ElementRef;
   constructor(private _bottomSheetRef: MatBottomSheetRef<UploadEmployeesBottomSheetComponent>,@Inject(MAT_BOTTOM_SHEET_DATA) public data) { }
   onFileSelected(ev){
     this.fileName=ev.target.files[0].name;

   }
   onDrop(ev){
     // console.log(ev);
   }
   onUpload(ev :Event){
    debugger
    console.log( this.fileInput.nativeElement.files[0]);

     this.onProgress=true;
     this.dailyService.uploadJsonFile({file :  this.fileInput.nativeElement.files[0]})
     .pipe(map(event=>{

       switch (event?.type) {
         case HttpEventType.Sent:
           // console.log('Request has been made!');
           break;
         case HttpEventType.UploadProgress:
           this.progress.update(val =>  Math.round(event.loaded / event.total * 100));
           // console.log(`Uploaded! ${this.progress()}%`);
           break;

         case HttpEventType.Response:
           // console.log('User successfully created!', event.body);
           setTimeout(() => {
             this.progress.update(val => val*0);
             this.onProgress=false;
           }, 1500);
           this._bottomSheetRef.dismiss(true);
           break;

         }
         return event;
     }))
     .subscribe({
       next:(event)=>{
           console.log(  event)
       },
       error:(err :any)=>{
        console.log(err);
        //this.toaster.openErrorToaster(err.error.message)
         this.onProgress=false;
       },
     })



   }
}

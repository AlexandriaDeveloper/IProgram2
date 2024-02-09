
import { HttpEventType } from '@angular/common/http';
import { Component, ElementRef, Inject, OnInit, ViewChild, inject, signal } from '@angular/core';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';
import { UploadEmployeesBottomSheetComponent } from '../../../../department/list/employees-department/upload-employees-bottom-sheet/upload-employees-bottom-sheet.component';
import { DepartmentService } from '../../../../shared/service/department.service';
import { FormService } from '../../../../shared/service/form.service';
import { ToasterService } from '../../../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-upload-excel-file-bottom',
  standalone: false,

  templateUrl: './upload-excel-file-bottom.component.html',
  styleUrl: './upload-excel-file-bottom.component.scss'
})
export class UploadExcelFileBottomComponent implements OnInit {
  @ViewChild('fileInput') fileInput : ElementRef
  formService = inject(FormService)
  toaster = Inject(ToasterService)
   fileName : string;

   progress = signal(0);
   onProgress=false;
   @ViewChild("fileDropRef", { static: false }) fileDropEl: ElementRef;
   constructor(private _bottomSheetRef: MatBottomSheetRef<UploadEmployeesBottomSheetComponent>,@Inject(MAT_BOTTOM_SHEET_DATA) public data) { }
  ngOnInit(): void {
   // throw new Error('Method not implemented.');
   // console.log(this.data);

  }
   onFileSelected(ev){
     this.fileName=ev.target.files[0].name;

   }
   onDrop(ev){
     // console.log(ev);

   }
   onUpload(ev :Event){
     this.onProgress=true;
     // console.log(this.fileInput.nativeElement.files);

     this.formService.uploadEmployeesExcelFile({formId : this.data.formId ,file :  this.fileInput.nativeElement.files[0]}).subscribe({
       next:(event)=>{
// console.log(event);


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

       },
       error:(err)=>{
         // console.log(err);
         this.onProgress=false;
         this.toaster.openErrorToaster('لم يتم رفع الملف بنجاح','error');
       //  this._bottomSheetRef.dismiss(false);


       },
     })
    }



}

import { Component, ElementRef, Inject, ViewChild, inject, signal } from '@angular/core';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { DepartmentService } from '../../../../shared/service/department.service';
import { HttpEventType } from '@angular/common/http';
import { ToasterService } from '../../../../shared/components/toaster/toaster.service';
import { map } from 'rxjs';

@Component({
  selector: 'app-upload-employees-bottom-sheet',
  standalone: false,
  templateUrl: './upload-employees-bottom-sheet.component.html',
  styleUrl: './upload-employees-bottom-sheet.component.scss'
})
export class UploadEmployeesBottomSheetComponent {
  @ViewChild('fileInput') fileInput : ElementRef
 departmentService = inject(DepartmentService)
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
    this.onProgress=true;
    this.departmentService.uploadEmployeesDepartmentFile({departmentId : this.data.departmentId ,file :  this.fileInput.nativeElement.files[0]})
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
         // console.log(  event)

      },
      error:(err :any)=>{
        this.onProgress=false;
      },
    })



  }
}

import { ToasterService } from './../../shared/components/toaster/toaster.service';
import { Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { EmployeeService } from '../../shared/service/employee.service';
import { UploadComponent } from '../../shared/components/upload/upload.component';
import { IUploadEmployee } from '../../shared/models/IEmployee';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { HttpEventType } from '@angular/common/http';


@Component({
  selector: 'app-upload-employee',
  standalone: false,

  templateUrl: './upload-employee.component.html',
  styleUrl: './upload-employee.component.scss'
})
export class UploadEmployeeComponent implements OnInit {


  employeeService = inject(EmployeeService);
  toaster = inject(ToasterService);
  fb =inject(FormBuilder);
progress;
  @ViewChild("fileDropRef", { static: false }) fileDropEl: UploadComponent;
  files: any[] = [];
  uploadForm :FormGroup;

  ngOnInit(): void {
    this.uploadForm = this.initUploadForm()
   }
  initUploadForm(){
    return this.fb.group({
      file:[]
    })
  }
  formatBytes(bytes, decimals = 2) {
    if (bytes === 0) {
      return "0 Bytes";
    }
    const k = 1024;
    const dm = decimals <= 0 ? 0 : decimals;
    const sizes = ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(dm)) + " " + sizes[i];
  }
  onFileSelected(ev){
console.log(ev.target.files);



console.log(this.uploadForm.value);

  }



  uploadFiles(){
    var files =  this.fileDropEl.files;
   // console.log(this.uploadForm.value);



    this.employeeService.uploadEmployeeFile(this.uploadForm.value).subscribe({
      next: (event) => {
        this.fileDropEl.onProgress=true;
        switch (event?.type) {
          case HttpEventType.Sent:
            console.log('Request has been made!');
            break;
          case HttpEventType.UploadProgress:
            this.progress = Math.round(event.loaded / event.total * 100);
            this.fileDropEl.progress.update(val => this.progress);
            console.log(`Uploaded! ${this.progress}%`);
            break;
          case HttpEventType.Response:
            console.log('User successfully created!', event.body);
            setTimeout(() => {
              this.fileDropEl.progress.update(val => val*0);
              this.fileDropEl.onProgress=false;
            }, 1500);
            this.toaster.openSuccessToaster('تم تحميل الملف بنجاح','check');
            this.fileDropEl.files = [];
          }
      },
      error: (err) => {

      },
      complete: () => {

      }
    })
  }
}

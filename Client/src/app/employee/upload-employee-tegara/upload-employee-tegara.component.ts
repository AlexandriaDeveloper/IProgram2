import { HttpEventType } from '@angular/common/http';
import { Component, OnInit, ViewChild, inject, signal } from '@angular/core';
import { FormBuilder, FormGroup } from '@angular/forms';
import { Observable, map, from, mergeMap, finalize } from 'rxjs';
import { ToasterService } from '../../shared/components/toaster/toaster.service';
import { UploadComponent } from '../../shared/components/upload/upload.component';
import { EmployeeService } from '../../shared/service/employee.service';

@Component({
  selector: 'app-upload-employee-tegara',
  standalone: false,

  templateUrl: './upload-employee-tegara.component.html',
  styleUrl: './upload-employee-tegara.component.scss'
})
export class UploadEmployeeTegaraComponent implements OnInit {



  employeeService = inject(EmployeeService);
  toaster = inject(ToasterService);
  fb = inject(FormBuilder);
  progress = signal<number[]>([]);
  @ViewChild("fileDropRef", { static: false }) fileDropEl: UploadComponent;
  files: any[] = [];
  uploadForm: FormGroup;

  ngOnInit(): void {
    this.uploadForm = this.initUploadForm()
  }
  initUploadForm() {
    return this.fb.group({
      file: []
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
  onFileSelected(ev) {
    // console.log(ev.target.files);



    // console.log(this.uploadForm.value);

  }



  uploadFiles() {
    var files = this.fileDropEl.files;
    // // console.log(this.uploadForm.value);

    // console.log(this.fileDropEl.files);
    let req: Observable<any>[] = []
    // console.log(this.fileDropEl.files);

    for (let index = 0; index < this.fileDropEl.files.length; index++) {
      req.push(this.employeeService.uploadEmployeeTegaraFile(this.fileDropEl.files[index]).pipe(
        map(event => {
          this.fileDropEl.onProgress[index] = true;
          switch (event?.type) {
            case HttpEventType.Sent:
              // console.log('Request has been made!');
              break;
            case HttpEventType.UploadProgress:
              this.progress[index] = Math.round(event.loaded / event.total * 100);
              // console.log(`Uploaded! ${this.progress[index]}%`);

              break;
            case HttpEventType.Response:
              // console.log('User successfully created!', event.body);
              this.progress[index] = 0;
              this.fileDropEl.onProgress[index] = false;

              this.fileDropEl.files[index] = null;



              break;
            default:
              // console.log(event);
              break;
          }
        })
      ))
    }
    return from(req).pipe(
      mergeMap((x, i) => req[i]),
      finalize(() => {
        // console.log('finalize');

        this.fileDropEl.files = [];
        // console.log(this.fileDropEl.files);
      })
    ).subscribe({

    });
  }
  downloadFile() {
    this.employeeService.downloadEmployeesFile().subscribe();
  }
  downloadFile2() {
    this.employeeService.downloadEmployeesFile2().subscribe();
  }
}

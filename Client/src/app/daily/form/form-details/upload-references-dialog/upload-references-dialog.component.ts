import { HttpEventType } from '@angular/common/http';
import { Component, Inject, OnInit, ViewChild, inject, signal } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { Observable, map, from, mergeMap, finalize } from 'rxjs';
import { UploadEmployeeReferncesDialogComponent } from '../../../../employee/list/employee-details/employee-references/upload-employee-refernces-dialog/upload-employee-refernces-dialog.component';
import { ToasterService } from '../../../../shared/components/toaster/toaster.service';
import { UploadComponent } from '../../../../shared/components/upload/upload.component';
import { EmployeeReferencesService } from '../../../../shared/service/employee-references.service';
import { FormReferencesService } from '../../../../shared/service/form-references.service';

@Component({
  selector: 'app-upload-references-dialog',
  standalone: false,

  templateUrl: './upload-references-dialog.component.html',
  styleUrl: './upload-references-dialog.component.scss'
})
export class UploadReferencesDialogComponent implements OnInit {

  fb =  inject(FormBuilder);
  formReferencesService =  inject(FormReferencesService);
  progress =signal<number[]>([]);
  toaster=inject(ToasterService)
  @ViewChild("fileDropRef", { static: false }) fileDropEl: UploadComponent;
  files: FileList;

  constructor(
    private dialogRef: MatDialogRef<UploadEmployeeReferncesDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
    ) {

    }
  ngOnInit(): void {
 this.uploadForm =this.initForm();
  }
  uploadForm:FormGroup;


initForm(){
  return this.fb.group({
    file:[,Validators.required]
  })
}
  uploadFiles(){

    let req : Observable<any>[]=[]
    console.log(this.fileDropEl.files);

    for (let index = 0; index < this.fileDropEl.files.length; index++) {
    req.push(  this.formReferencesService.UploadRefernceFile(this.data.formId, this.fileDropEl.files[index] ).pipe(
      map(event => {
        this.fileDropEl.onProgress[index]=true;
        switch (event?.type) {
          case HttpEventType.Sent:
            console.log('Request has been made!');
            break;
          case HttpEventType.UploadProgress:
            this.progress[index] = Math.round(event.loaded / event.total * 100);
            console.log(`Uploaded! ${this.progress[index]}%`);

            break;
          case HttpEventType.Response:
            console.log('User successfully created!', event.body);
            this.progress[index] = 0;
            this.fileDropEl.onProgress[index]=false;

            this.fileDropEl.files[index]=null;



            break;
          default:
            console.log(event);
            break;
        }
      })
      ))
    }
    return from(req).pipe(
    mergeMap((x,i)=> req[i]),
    finalize(()=>{
      console.log('finalize');

      this.fileDropEl.files=[];
      console.log(this.fileDropEl.files);
    })
    ).subscribe({

    });


  }
  onFileSelected(ev){
  // this.fileDropEl.onFileSelected(ev);

  }
  onNoClick(): void {
    this.dialogRef.close();
      }
}

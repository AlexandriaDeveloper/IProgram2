import { Observable, combineLatest, combineLatestWith, concat, concatMap, finalize, flatMap, forkJoin, from, map, mergeMap, zip } from 'rxjs';
import { Component, Inject, OnInit, ViewChild, inject, signal } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { UploadComponent } from '../../../../../shared/components/upload/upload.component';
import { EmployeeReferencesService } from '../../../../../shared/service/employee-references.service';
import { HttpEventType } from '@angular/common/http';
import { ToasterService } from '../../../../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-upload-employee-refernces-dialog',
  standalone: false,

  templateUrl: './upload-employee-refernces-dialog.component.html',
  styleUrl: './upload-employee-refernces-dialog.component.scss'
})
export class UploadEmployeeReferncesDialogComponent implements OnInit {

  fb =  inject(FormBuilder);
  employeeReferenceService =  inject(EmployeeReferencesService);
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
    req.push(  this.employeeReferenceService.UploadRefernceFile(this.data.employeeId, this.fileDropEl.files[index] ).pipe(
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

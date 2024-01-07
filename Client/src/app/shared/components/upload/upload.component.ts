import { Component, ElementRef, ViewChild, inject, EventEmitter, Output, OnInit, Self, signal } from '@angular/core';
import { ToasterService } from '../toaster/toaster.service';
import { MatIconModule } from '@angular/material/icon';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { DndDirective } from '../../directives/dnd.directive';
import { ControlValueAccessor, NgControl } from '@angular/forms';
import { MatProgressBarModule } from '@angular/material/progress-bar';

@Component({
  selector: 'app-upload',
  standalone: true,
  imports: [
    MatIconModule,
    MatButtonModule,
    MatProgressBarModule,
    CommonModule,
    DndDirective
  ],

  templateUrl: './upload.component.html',
  styleUrl: './upload.component.scss'
})
export class UploadComponent implements OnInit, ControlValueAccessor {

  progress = signal(0);
  onProgress=false;
  toaster = inject(ToasterService);
  @ViewChild("fileInput", { static: true }) fileInput: ElementRef;
  @Output() upload = new EventEmitter<any>();
  @Output() changeFile = new EventEmitter<any>();
  files: any[] = [];
  onChange(event) {}
  onTouched() {}
  constructor(@Self() public controlDir: NgControl) {
    this.controlDir.valueAccessor = this;
    console.log(this.controlDir);
  }
    ngOnInit(): void {
      const control = this.controlDir.control;
      const validators = control.validator ? [control.validator] : [];
      const asyncValidators = control.asyncValidator
        ? [control.asyncValidator]
        : [];

      control.setValidators(validators);
      control.setAsyncValidators(asyncValidators);
      control.updateValueAndValidity();
    }


  writeValue(obj: any): void {
   this.fileInput.nativeElement.value = obj || '';
  }
  registerOnChange(fn: any): void {


    this.onChange = fn;

  }

  registerOnTouched(fn: any): void {
    this.onTouched = fn;


  }
  // setDisabledState?(isDisabled: boolean): void {
  //   throw new Error('Method not implemented.');
  // }



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


    for (let index = 0; index < ev.target.files.length; index++) {
      this.checkFile(ev.target.files[index])

    }


  }
  removeFile(i){
    this.files.splice(i,1)
  }

  onDrop(ev ){
    for (let i = 0; i < ev.length; i++) {
     this.checkFile(ev[i])
    }
  }

  private checkFile(file) :boolean{

    var result =true
   let ext = file.name.split('.').pop();
      if(ext!=="xlsx"){
          this.toaster.openErrorToaster('يجب ان يكون الملف من نوع xlsx','warning')
          result=false
          return result;
        }


    this.files.filter((f)=>{
      if(f.name===file.name && f.size===file.size && ext==="xlsx"){
        this.toaster.openErrorToaster('هذا الملف موجود مسبقا','warning')
        result=false
      }
    })
    if(result){

    //  this.files.push(file);
      this.onChange(this.files)
      this.writeValue(this.files)
      this.files.push(file);
    }
    return result;
  }

  uploadFiles(){
    this.upload.emit(this.files)
  }
}

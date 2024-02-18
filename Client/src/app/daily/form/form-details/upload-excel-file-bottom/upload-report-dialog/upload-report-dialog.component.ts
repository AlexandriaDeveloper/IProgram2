import { Component, Inject } from '@angular/core';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';

@Component({
  selector: 'app-upload-report-dialog',
  standalone: false,

  templateUrl: './upload-report-dialog.component.html',
  styleUrl: './upload-report-dialog.component.scss'
})
export class UploadReportDialogComponent {
  constructor(
    private dialogRef: MatDialogRef<UploadReportDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any

  ) {

  }
  onNoClick(){
this.dialogRef.close();
  }
}

import { HttpEventType } from '@angular/common/http';
import { Component, Inject, OnInit, signal } from '@angular/core';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';

import { DailyReferencesService } from '../../../../shared/service/daily-references.service';
import { ToasterService } from '../../../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-upload-pdf-bottom',
  templateUrl: './upload-pdf-bottom.component.html',
  styleUrls: ['./upload-pdf-bottom.component.scss']
})
export class UploadPdfBottomComponent implements OnInit {

  description: string = '';
  fileName: string;
  selectedFile: File | null = null;

  progress = signal(0);
  onProgress = false;
  dailyId: number;

  constructor(
    private _bottomSheetRef: MatBottomSheetRef<UploadPdfBottomComponent>,
    @Inject(MAT_BOTTOM_SHEET_DATA) public data: any,
    private dailyRefService: DailyReferencesService,
    private toaster: ToasterService
  ) {
    this.dailyId = data.dailyId;
  }

  ngOnInit(): void {
    if (!this.dailyId) {
      this.toaster.openErrorToaster("DailyId is missing. Cannot upload.");
      this._bottomSheetRef.dismiss();
    }
  }

  onFileSelected(event: any): void {
    const file = event.target.files[0];
    if (file && file.type === 'application/pdf') {
      this.selectedFile = file;
      this.fileName = file.name;
    } else {
      this.selectedFile = null;
      this.fileName = '';
      this.toaster.openErrorToaster('Please select a PDF file only.');
    }
  }

  onUpload(): void {
    if (!this.selectedFile) {
      this.toaster.openErrorToaster('Please provide a description and select a file.');
      return;
    }

    this.onProgress = true;
    this.dailyRefService.upload(this.dailyId, this.description, this.selectedFile).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress) {
          this.progress.set(Math.round((100 * event.loaded) / event.total));
        } else if (event.type === HttpEventType.Response) {
          this.toaster.openSuccessToaster('File uploaded successfully!');
          setTimeout(() => {
            this._bottomSheetRef.dismiss(true); // Pass true to indicate success
          }, 1500);
        }
      },
      error: (err) => {
        this.onProgress = false;
        this.toaster.openErrorToaster('An error occurred during upload.', err.message);
        console.error(err);
      }
    });
  }

  closeSheet(): void {
    this._bottomSheetRef.dismiss();
  }
}

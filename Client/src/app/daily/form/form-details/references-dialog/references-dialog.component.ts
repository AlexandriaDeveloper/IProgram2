import { FormReferencesService } from './../../../../shared/service/form-references.service';
import { Component, Inject, Input, OnDestroy, OnInit, inject } from '@angular/core';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { AddDailyComponent } from '../../../add-daily/add-daily.component';
import { GalleryItem, ImageItem } from 'ng-gallery';

@Component({
  selector: 'app-references-dialog',
  standalone: false,
  templateUrl: './references-dialog.component.html',
  styleUrl: './references-dialog.component.scss'
})
export class ReferencesDialogComponent implements OnInit, OnDestroy {

  images: GalleryItem[] = null;
  private createdObjectUrls: string[] = [];
  @Input('employeeId') employeeId: number;
  formReferencesService = inject(FormReferencesService);

  constructor(
    private dialogRef: MatDialogRef<ReferencesDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any
  ) {
  }

  ngOnInit(): void {
    if (this.images == null) {
      this.loadRefernces();
    }
  }

  ngOnDestroy(): void {
    this.revokeAllObjectUrls();
  }

  private revokeAllObjectUrls(): void {
    if (this.createdObjectUrls && this.createdObjectUrls.length > 0) {
      this.createdObjectUrls.forEach(url => URL.revokeObjectURL(url));
      this.createdObjectUrls = [];
    }
  }

  loadRefernces(): void {
    this.formReferencesService.getFormReferences(this.data.formId).subscribe({
      next: (res: any) => {
        this.revokeAllObjectUrls();

        if (!res || res.length === 0) {
          this.images = [];
          return;
        }

        const items: GalleryItem[] = [];
        let pending = res.length;

        res.forEach((x: any) => {
          this.formReferencesService.getReferenceFile(x.id).subscribe({
            next: (blob: Blob) => {
              const objectUrl = URL.createObjectURL(blob);
              this.createdObjectUrls.push(objectUrl);
              items.push(new ImageItem({ src: objectUrl, thumb: objectUrl, args: { id: x.id } }));
              pending--;
              if (pending === 0) {
                this.images = items;
              }
            },
            error: () => {
              pending--;
              if (pending === 0) {
                this.images = items;
              }
            }
          });
        });
      }
    });
  }

  saveImage(imageSrc: string): void {
    window.open(imageSrc, '_blank');
  }

  deleteImage(imageItem: any): void {
    if (confirm('هل تريد حذف هذا المستند؟')) {
      this.formReferencesService.deleteFormReference(imageItem?.args?.id).subscribe({
        next: () => {
          this.loadRefernces();
        }
      });
    }
  }

  onNoClick(): void {
    this.dialogRef.close();
  }
}

import { Component, Inject, Input, OnDestroy, OnInit, ViewChild, inject } from '@angular/core';
import { GalleryItem, ImageItem } from 'ng-gallery';
import { EmployeeReferencesService } from '../../../../shared/service/employee-references.service';
import { FormBuilder, FormGroup } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { UploadComponent } from '../../../../shared/components/upload/upload.component';

@Component({
  selector: 'app-employee-references',
  standalone: false,
  templateUrl: './employee-references.component.html',
  styleUrl: './employee-references.component.scss'
})
export class EmployeeReferencesComponent implements OnInit, OnDestroy {

  images: GalleryItem[] = null;
  private createdObjectUrls: string[] = [];
  @Input('employeeId') employeeId: number;
  employeeReferenceServices = inject(EmployeeReferencesService);

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
    this.employeeReferenceServices.getEmployeeReferences(this.employeeId).subscribe({
      next: (res: any) => {
        this.revokeAllObjectUrls();

        if (!res || res.length === 0) {
          this.images = [];
          return;
        }

        const items: GalleryItem[] = [];
        let pending = res.length;

        res.forEach((x: any) => {
          this.employeeReferenceServices.getReferenceFile(x.id).subscribe({
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
      this.employeeReferenceServices.deleteEmployeeReference(imageItem?.args?.id).subscribe({
        next: () => {
          this.loadRefernces();
        }
      });
    }
  }
}

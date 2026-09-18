import { Component, Inject, Input, OnInit, ViewChild, inject } from '@angular/core';
import { GalleryItem, ImageItem } from 'ng-gallery';
import { EmployeeReferencesService } from '../../../../shared/service/employee-references.service';
import { FormBuilder, FormGroup } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { UploadComponent } from '../../../../shared/components/upload/upload.component';
import { environment } from '../../../../environment';

@Component({
  selector: 'app-employee-references',
  standalone: false,
  templateUrl: './employee-references.component.html',
  styleUrl: './employee-references.component.scss'
})
export class EmployeeReferencesComponent implements OnInit {

  images: GalleryItem[] = null;
  @Input('employeeId') employeeId: number
  employeeReferenceServices = inject(EmployeeReferencesService);


  ngOnInit(): void {
    // console.log('tab is hits ');
    if (this.images == null) {
      this.loadRefernces();
    }

  }

  loadRefernces() {
    this.employeeReferenceServices.getEmployeeReferences(this.employeeId).subscribe({
      next: (res: any) => {
        const token = localStorage.getItem('token');
        let base = environment.apiContent || 'http://localhost:5000/';
        if (!base.endsWith('/')) {
          base += '/';
        }

        this.images = res.map((x: any) => {
          let path = String(x.referencePath || '').trim();
          let fullUrl = path.startsWith('http') ? path : base + (path.startsWith('/') ? path.substring(1) : path);
          if (token && !fullUrl.includes('access_token')) {
            const sep = fullUrl.includes('?') ? '&' : '?';
            fullUrl = `${fullUrl}${sep}access_token=${token}`;
          }
          return new ImageItem({ src: fullUrl, thumb: fullUrl, args: { id: x.id } });
        });
      }
    })
  }

  saveImage(imageSrc) {

    window.open(imageSrc, '_blank');
  }
  deleteImage(imageItem) {
    if (confirm('هل تريد حذف هذا المستند؟')) {
      this.employeeReferenceServices.deleteEmployeeReference(imageItem?.args?.id).subscribe({
        next: (res) => {
          console.log(res);
          this.loadRefernces();
        }
      })
    }

  }

}

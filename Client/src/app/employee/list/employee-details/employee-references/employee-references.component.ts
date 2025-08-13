import { Component, Inject, Input, OnInit, ViewChild, inject } from '@angular/core';
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
        console.log(res);

        this.images = res.map(x => new ImageItem({ src: x.referencePath, thumb: x.referencePath, args: { id: x.id } }));
        //this.galleryRef.load(this.images);
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

import { FormReferencesService } from './../../../../shared/service/form-references.service';
import { Component, Inject, Input, inject } from '@angular/core';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { AddDailyComponent } from '../../../add-daily/add-daily.component';
import { GalleryItem, ImageItem } from 'ng-gallery';
import { environment } from '../../../../environment';

@Component({
  selector: 'app-references-dialog',
  standalone: false,

  templateUrl: './references-dialog.component.html',
  styleUrl: './references-dialog.component.scss'
})
export class ReferencesDialogComponent {

  images: GalleryItem[]=null;
  @Input('employeeId') employeeId:number
  formReferencesService=inject(FormReferencesService);
  constructor(
    private dialogRef: MatDialogRef<ReferencesDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: any

  ) {

  }


  ngOnInit(): void {
    // console.log(this.data);
    if(this.images==null)
    {
      this.loadRefernces();
    }

  }

  loadRefernces(){
    this.formReferencesService.getFormReferences(this.data.formId).subscribe({
      next:(res:any)=>{
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

  saveImage(imageSrc){

     window.open(imageSrc,'_blank');
  }
  deleteImage(imageItem){
if(confirm('هل تريد حذف هذا المستند؟')){
  this.formReferencesService.deleteFormReference(imageItem?.args?.id).subscribe({
    next:(res)=>{
      // console.log(res);
      this.loadRefernces();
    }
  })
}

  }

  onNoClick(){
this.dialogRef.close();
  }
}

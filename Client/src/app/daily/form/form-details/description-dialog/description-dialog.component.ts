import { FormService } from './../../../../shared/service/form.service';
import { Component, Inject, OnInit, inject } from '@angular/core';
import { FormBuilder, FormGroup } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

@Component({
  selector: 'app-description-dialog',
  standalone: false,

  templateUrl: './description-dialog.component.html',
  styleUrl: './description-dialog.component.scss'
})
export class DescriptionDialogComponent implements OnInit {
  formService=inject(FormService);
  ngOnInit(): void {
    this.form=this.initForm();
  }
/**
 *
 */
constructor(private dialogRef: MatDialogRef<DescriptionDialogComponent>,
  @Inject(MAT_DIALOG_DATA) public data: any) {

    console.log(this.data);
}
  form:FormGroup;
  fb = inject(FormBuilder);



  initForm(){


    return this.fb.group({
      description:[this.data?.form?.description,[]]
    })
  }
  onNoClick(){
    this.dialogRef.close();
  }
  onSubmit(){console.log('clicked');

    this.formService.updateDescription(this.data?.form?.id,this.form.value).subscribe(x =>{
      this.dialogRef.close();


    }

    );
  }


}

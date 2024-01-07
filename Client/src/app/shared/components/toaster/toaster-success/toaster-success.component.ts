import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MAT_SNACK_BAR_DATA, MatSnackBarRef } from '@angular/material/snack-bar';

@Component({
  selector: 'app-toaster-success',
  standalone: true,
  imports: [MatIconModule,MatButtonModule],
  templateUrl: './toaster-success.component.html',
  styleUrl: './toaster-success.component.scss'
})
export class ToasterSuccessComponent {
  dialog = inject(MatSnackBarRef);
  data =inject(MAT_SNACK_BAR_DATA)
  close(){
    console.log('close');

    this.dialog.dismiss();
  }

}

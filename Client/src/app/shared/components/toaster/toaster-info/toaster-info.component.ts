import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBarRef, MAT_SNACK_BAR_DATA } from '@angular/material/snack-bar';

@Component({
  selector: 'app-toaster-info',
  standalone: true,
  imports: [MatIconModule,MatButtonModule],
  templateUrl: './toaster-info.component.html',
  styleUrl: './toaster-info.component.scss'
})
export class ToasterInfoComponent {
  dialog = inject(MatSnackBarRef);
  data =inject(MAT_SNACK_BAR_DATA)
  close(){
    // console.log('close');

    this.dialog.dismiss();
  }

}

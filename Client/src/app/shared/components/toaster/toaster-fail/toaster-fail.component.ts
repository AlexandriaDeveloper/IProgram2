import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBarRef, MAT_SNACK_BAR_DATA } from '@angular/material/snack-bar';

@Component({
  selector: 'app-toaster-fail',
  standalone: true,
  imports: [MatIconModule,MatButtonModule],
  templateUrl: './toaster-fail.component.html',
  styleUrl: './toaster-fail.component.scss'
})
export class ToasterFailComponent {
  dialog = inject(MatSnackBarRef);
  data =inject(MAT_SNACK_BAR_DATA)
  close(){

    this.dialog.dismiss();
  }
}

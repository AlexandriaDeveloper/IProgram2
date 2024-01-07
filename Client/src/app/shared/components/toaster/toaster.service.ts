import { Injectable, inject } from '@angular/core';
import { ToasterComponent } from './toaster/toaster.component';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ToasterSuccessComponent } from './toaster-success/toaster-success.component';
import { ToasterFailComponent } from './toaster-fail/toaster-fail.component';

@Injectable({
  providedIn: 'root'
})
export class ToasterService {
  _snackBar=inject(MatSnackBar)
  constructor() { }

  openSuccessToaster(message: string,icon ?:string) {
    this._snackBar.openFromComponent(ToasterSuccessComponent, {
      duration: 300000,
      verticalPosition: 'top',
      horizontalPosition: 'right',
      panelClass: ['toaster-success'],
      politeness: 'assertive',
      data: {
        message,
        icon
      }

    })

  }
  openErrorToaster(message: string,icon ?:string) {
    this._snackBar.openFromComponent(ToasterFailComponent, {
      duration: 3000,
      verticalPosition: 'top',
      horizontalPosition: 'right',
      panelClass: ['toaster-error'],
      politeness: 'assertive',
      data: {
        message,
        icon
      }

    })
  }

  openInfoToaster(message: string) {
  }
}

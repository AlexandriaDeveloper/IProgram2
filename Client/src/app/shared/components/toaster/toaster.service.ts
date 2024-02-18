import { Injectable, inject } from '@angular/core';
import { ToasterComponent } from './toaster/toaster.component';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ToasterSuccessComponent } from './toaster-success/toaster-success.component';
import { ToasterFailComponent } from './toaster-fail/toaster-fail.component';
import { ToasterInfoComponent } from './toaster-info/toaster-info.component';

@Injectable({
  providedIn: 'root'
})
export class ToasterService {
  _snackBar=inject(MatSnackBar)
  constructor() { }

  openSuccessToaster(message: string,icon ?:string,duration : number=5000) {
    this._snackBar.openFromComponent(ToasterSuccessComponent, {
      duration: duration,
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
  openErrorToaster(message: string,icon ?:string,duration : number=5000) {
    this._snackBar.openFromComponent(ToasterFailComponent, {
      duration: duration,
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

  openInfoToaster(message: string,icon ?:string,duration : number=5000) {
    this._snackBar.openFromComponent(ToasterInfoComponent, {
      duration: duration,
      verticalPosition: 'top',
      horizontalPosition: 'right',
      panelClass: ['toaster-info'],
      politeness: 'assertive',
      data: {
        message,
        icon
      }

    })
  }
}

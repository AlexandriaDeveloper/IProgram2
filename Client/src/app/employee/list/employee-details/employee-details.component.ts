import { ToasterService } from './../../../shared/components/toaster/toaster.service';
import { MatDialog } from '@angular/material/dialog';
import { Component, OnInit, ViewChild, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';

import { EmployeeService } from '../../../shared/service/employee.service';
import { EmployeeParam, EmployeeReportRequest, IEmployee } from '../../../shared/models/IEmployee';
import { Gallery, GalleryItem, GalleryRef, IframeItem, ImageItem } from 'ng-gallery';
import { environment } from '../../../environment';
import { UploadEmployeeReferncesDialogComponent } from './employee-references/upload-employee-refernces-dialog/upload-employee-refernces-dialog.component';
import { ThemePalette } from '@angular/material/core';
import { AddBankDialogComponent } from './bank-info/add-bank-dialog/add-bank-dialog.component';
import { EditEmployeeDialogComponent } from './edit-employee-dialog/edit-employee-dialog.component';
import { MatMenu } from '@angular/material/menu';
import { MatTab } from '@angular/material/tabs';
import { fromEvent } from 'rxjs';


@Component({
  selector: 'app-employee-details',
  standalone: false,

  templateUrl: './employee-details.component.html',
  styleUrl: './employee-details.component.scss'
})
export class EmployeeDetailsComponent implements OnInit {





  router = inject(Router);
  gallery = inject(Gallery);
  toaster = inject(ToasterService);
  _dialog = inject(MatDialog)
  galleryRef: GalleryRef;
  employeeId = inject(ActivatedRoute).snapshot.params['id'];

  employeeService = inject(EmployeeService);
  param: EmployeeParam = new EmployeeParam();
  @ViewChild('mattab') matTab: MatTab;

  employee?: IEmployee;
  ngOnInit(): void {
    console.log("hello")
    console.log(this.employeeId);

    this.param.id = this.employeeId
    this.loadEmployee();

  }

  loadEmployee() {
    this.galleryRef = this.gallery.ref('myGallery');
    this.employeeService.GetEmployee(this.param).subscribe({
      next: (x) => {
        this.employee = x
        //  this.images = x.employeeRefernces.map(x=>new ImageItem({ src: x.referencePath, thumb: x.referencePath }));
        //this.galleryRef.load(this.images);

      },
      error: (err) => {
        // console.log(err);

        if (err.status == 404) {
          this.router.navigate(['/employee'])
        }
      },
      complete: () => {
        // console.log('ended');
      }
    })
  }


  openUploadDialog() {
    const dialogRef = this._dialog.open(UploadEmployeeReferncesDialogComponent, {
      // data: {name: this.name, animal: this.animal},

      disableClose: true,
      data: { employeeId: this.employeeId },
      panelClass: ['dialog-container'],
    });

    dialogRef.afterClosed().subscribe(result => {


      window.location.reload();

    });
  }
  openBankDialog() {
    const dialogRef = this._dialog.open(AddBankDialogComponent, {
      width: '400px',
      disableClose: true,
      data: { employeeId: this.employeeId },
      panelClass: ['dialog-container'],
    });

    dialogRef.afterClosed().subscribe(result => {
      this.loadEmployee();

    });
  }
  openEmployeeEditDialog() {
    const dialogRef = this._dialog.open(EditEmployeeDialogComponent, {
      width: '600px',
      disableClose: true,
      data: { employeeId: this.employeeId },
      panelClass: ['dialog-container'],
    });

    dialogRef.afterClosed().subscribe(result => {
      this.ngOnInit();
    });
  }

  onTabChange(ev) {
    console.log(ev);


  }
  stopEmployee() {
    if (confirm("انت على وشك ايقاف الموظف من المنظومه هل انت متأكد ؟!!"))
      this.employeeService.softDelete(this.employeeId).subscribe((res) => {
        this.toaster.openSuccessToaster('تم ايقاف الموظف بنجاح', 'check');

      });
  }
}

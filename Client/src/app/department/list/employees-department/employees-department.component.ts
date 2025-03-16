import { Ids } from './../../../shared/models/Department';
import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable } from '@angular/material/table';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import { fromEvent, debounceTime, distinctUntilChanged, map, filter } from 'rxjs';
import { EmployeeParam, IEmployee } from '../../../shared/models/IEmployee';
import { EmployeeService } from '../../../shared/service/employee.service';
import { AddEmployeeDialogComponent } from './add-employee-dialog/add-employee-dialog.component';
import { MatDialog } from '@angular/material/dialog';
import { DepartmentService } from '../../../shared/service/department.service';
import { ToasterService } from '../../../shared/components/toaster/toaster.service';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { UploadEmployeesBottomSheetComponent } from './upload-employees-bottom-sheet/upload-employees-bottom-sheet.component';

@Component({
  selector: 'app-employees-department',
  standalone: false,

  templateUrl: './employees-department.component.html',
  styleUrl: './employees-department.component.scss'
})
export class EmployeesDepartmentComponent implements AfterViewInit, OnInit {
  employeeService = inject(EmployeeService);
  departmentService = inject(DepartmentService);
  router = inject(ActivatedRoute);
  toaster = inject(ToasterService);
  router2 = inject(Router);
  dialog = inject(MatDialog)
  bottomSheet = inject(MatBottomSheet)
  title: '';
  mainCheck = false;
  public param: EmployeeParam = new EmployeeParam();
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;
  @ViewChild("tabCodeInput") tabCodeInput: ElementRef;
  @ViewChild("tegaraCodeInput") tegaraCodeInput: ElementRef;
  @ViewChild("nameInput") nameInput: ElementRef;
  @ViewChild("nationalIdInput") nationalIdInput: ElementRef;
  @ViewChild("collageInput") collageInput: ElementRef;

  dataSource;

  constructor(private cdref: ChangeDetectorRef) { }
  ngOnInit(): void {
    this.param.departmentId = this.router.snapshot.params['id']
    this.title = this.router.snapshot.params['title']
    this.loadData();
    this.cdref.detectChanges();
  }
  ngAfterViewInit(): void {

    this.search();
  }
  search() {
    this.initElement(this.tabCodeInput, 'tabCode');
    this.initElement(this.tegaraCodeInput, 'tegaraCode');
    this.initElement(this.nameInput, 'name');
    this.initElement(this.nationalIdInput, 'nationalId');
    this.initElement(this.collageInput, 'collage');
  }
  initElement(element: ElementRef, param) {


    fromEvent(element.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
      map((event: any) => {
        return event.target.value;
      })
    ).subscribe(x => {
      switch (param) {
        case 'tabCode':
          this.param.tabCode = x; break;
        case 'tegaraCode':
          this.param.tegaraCode = x; break;
        case 'name':
          this.param.name = x; break;
        case 'employeeId':
          this.param.id = x; break;
        case 'collage':
          this.param.collage = x; break;
      }
      this.loadData();
    })
  }
  onCheckMain() {
    this.mainCheck = !this.mainCheck;


    this.dataSource.forEach(x => x.checked = this.mainCheck);
    this.table.renderRows();
  }
  onCheck(row) {

    row.checked = !row.checked;
  }
  getChecked() {

    return this.dataSource.filter(x => x?.checked).map(x => x.id) ?? [];

  }
  removeEmployees() {

    let req = {} as Ids
    req.ids = [];
    req.ids = this.dataSource.filter(x => x.checked).map(x => x.id);
    if (confirm(`انت على وشك حذف عدد ${req.ids.length} موظف هل انت متأكد ؟!`)) {
      this.departmentService.deleteEmployeeFromDepartment(req).subscribe({
        next: (x) => {
          this.loadData();
          this.toaster.openSuccessToaster('تم الحذف بنجاح', 'check_circle');

        }
      })
    }
  }

  removeAll() {
    // console.log(this.dataSource);

    if (confirm(`انت على وشك حذف عدد ${this.paginator.length} موظف هل انت متأكد ؟!`))
      this.departmentService.deleteEmployeesByDepartmentId(this.param.departmentId).subscribe({
        next: (x) => {
          this.loadData();
          this.toaster.openSuccessToaster('تم الحذف بنجاح', 'check_circle');

        }
      })
  }
  /** Columns displayed in the table. Columns IDs can be added, removed, or reordered. */
  displayedColumns = ['action', 'tabCode', 'tegaraCode', 'name', 'nationalId', 'collage'];



  loadData(): void {
    this.mainCheck = false;
    this.employeeService.GetEmployees(this.param).subscribe((x: any) => {
      this.dataSource = x.data
      this.paginator.length = x.count;
      // this.paginator.pageIndex=x.pageIndex;
      // this.paginator.pageSize=x.pageSize;
    });
  }
  onChange(ev) {
    this.param.pageSize = ev.pageSize;
    this.param.pageIndex = ev.pageIndex;


    this.loadData();

  }
  addEmployeeDialog() {
    const dialogRef = this.dialog.open(AddEmployeeDialogComponent, {
      width: '50%',
      data: {
        title: 'إضافة موظف',
        departmentId: this.param.departmentId
      },

      disableClose: true,
    })
    dialogRef.afterClosed().subscribe(result => {
      this.loadData();
      // this.animal = result;
    });
  }

  clear(input: any) {


    if (input === 'tabCode') {
      this.tabCodeInput.nativeElement.value = '';
      this.param.tabCode = null;
    }
    if (input === 'tegaraCode') {
      this.tegaraCodeInput.nativeElement.value = '';
      this.param.tegaraCode = null;
    }
    if (input === 'name') {
      this.nameInput.nativeElement.value = '';
      this.param.name = null;
    }
    if (input === 'employeeId') {
      this.nationalIdInput.nativeElement.value = '';
      this.param.id = null;
    }
    if (input === 'collage') {
      this.collageInput.nativeElement.value = '';
      this.param.collage = null;
    }
    this.loadData();

  }
  downloadFile() {
    this.departmentService.downloadEmployeeDepartmentFile('employees-department').subscribe({
      next: (x) => {
        // console.log(x);

      }
    })
  }

  uploadFile() {
    this.bottomSheet.open(UploadEmployeesBottomSheetComponent, {
      panelClass: ['bottomSheet'],
      hasBackdrop: true,
      data: {
        departmentId: this.param.departmentId
      }


    });

    this.bottomSheet._openedBottomSheetRef.afterDismissed().subscribe(result => {
      if (result) {
        this.loadData();
        this.toaster.openSuccessToaster('تم رفع الملف  بنجاح', 'check_circle');
      }
    })
  }
}

import { EmployeeParam } from '../../shared/models/IEmployee';
import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, Inject, OnInit, ViewChild, inject, OnDestroy } from '@angular/core';
import { trigger, transition, style, animate } from '@angular/animations';
import { MatTableModule, MatTable } from '@angular/material/table';
import { MatPaginatorModule, MatPaginator } from '@angular/material/paginator';
import { MatSortModule, MatSort, Sort } from '@angular/material/sort';

import { EmployeeService } from '../../shared/service/employee.service';
import { IEmployee } from '../../shared/models/IEmployee';
import { debounceTime, distinctUntilChanged, fromEvent, map, of, tap, Subscription } from 'rxjs';
import { ActivatedRoute } from '@angular/router';
import { MatDialogRef, MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { AddBankDialogComponent } from './employee-details/bank-info/add-bank-dialog/add-bank-dialog.component';
import { EditEmployeeDialogComponent } from './employee-details/edit-employee-dialog/edit-employee-dialog.component';
import { ToasterService } from '../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-list',
  templateUrl: './list.component.html',
  styleUrls: ['./list.component.scss'],
  standalone: false,
  animations: [
    trigger('rowAnimation', [
      transition(':enter', [
        style({ opacity: 0, transform: 'translateY(10px)' }),
        animate('400ms cubic-bezier(0.35, 0, 0.25, 1)', style({ opacity: 1, transform: 'translateY(0)' }))
      ]),
      transition(':leave', [
        animate('300ms ease-in', style({ opacity: 0, transform: 'translateY(-10px)' }))
      ])
    ])
  ]
})
export class ListComponent implements AfterViewInit, OnInit, OnDestroy {
  employeeService = inject(EmployeeService);
  router = inject(ActivatedRoute);
  toaster = inject(ToasterService);
  public param: EmployeeParam = new EmployeeParam();
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;
  @ViewChild("tabCodeInput") tabCodeInput: ElementRef;
  @ViewChild("tegaraCodeInput") tegaraCodeInput: ElementRef;
  @ViewChild("nameInput") nameInput: ElementRef;
  @ViewChild("employeeIdInput") employeeIdInput: ElementRef;
  @ViewChild("collageInput") collageInput: ElementRef;
  @ViewChild("departmentInput") departmentInput: ElementRef;
  dataSource;
  _dialog = inject(MatDialog)
  // hold subscriptions created by initElement so we can unsubscribe on destroy
  private _subs: Subscription[] = [];

  constructor(private cdref: ChangeDetectorRef) { }
  ngOnInit(): void {
    // console.log('onInit');

    if (this.router.snapshot.queryParams['departmentId']) {

      this.param.departmentId = this.router.snapshot.queryParams['departmentId']
    }
    else {

      this.param = new EmployeeParam();
    }
    this.loadData();
    this.cdref.detectChanges();
  }
  ngAfterViewInit(): void {
    // create search subscriptions once after view init
    this.search();
    const sortSub = this.sort.sortChange.subscribe((sort: Sort) => this.onSortChange(sort));
    this._subs.push(sortSub);
  }

  ngOnDestroy(): void {
    // unsubscribe any subscriptions created by initElement
    this._subs.forEach(s => s.unsubscribe());
    this._subs = [];
  }
  search() {
    this.initElement(this.tabCodeInput, 'tabCode');
    this.initElement(this.tegaraCodeInput, 'tegaraCode');
    this.initElement(this.nameInput, 'name');
    this.initElement(this.employeeIdInput, 'employeeId');
    this.initElement(this.collageInput, 'collage');
    this.initElement(this.departmentInput, 'department');
  }
  initElement(element: ElementRef, param) {
    const sub = fromEvent(element.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
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
        case 'department':
          this.param.departmentName = x; break;
      }
      this.loadData();
    })
    this._subs.push(sub);
  }



  /** Columns displayed in the table. Columns IDs can be added, removed, or reordered. */
  displayedColumns = ['action', 'tabCode', 'tegaraCode', 'name', 'employeeId', 'department', 'collage'];



  loadData(): void {



    this.employeeService.GetEmployees(this.param).subscribe((x: any) => {
      this.dataSource = x.data
      if (this.paginator) {
        this.paginator.length = x.count;
      }

      //reset paramater
      if (this.paginator) {
        this.param.pageIndex = this.paginator.pageIndex;
        this.param.pageSize = this.paginator.pageSize;
      }
      //reset search
      // if (this.table) {
      //   this.table.dataSource = this.dataSource;
      //   this.table.renderRows();
      // }


    });


  }
  onSortChange(sort: Sort) {
    if (sort.direction === '') {
      this.param.sortBy = null;
      this.param.direction = null;
    } else {
      this.param.sortBy = sort.active;
      this.param.direction = sort.direction;
    }
    this.param.pageIndex = 0;
    if (this.paginator) {
      this.paginator.firstPage();
    }
    this.loadData();
  }
  onChange(ev) {
    this.param.pageSize = ev.pageSize;
    this.param.pageIndex = ev.pageIndex;


    this.loadData();

  }
  // editEmployee(id: number) {
  //   // console.log(id);
  // }
  deleteEmployee(row: IEmployee) {
    if (confirm(`هل تريد حذف الموظف ${row.name}؟`)) {
      this.employeeService.softDelete(row.id).pipe(
        tap(() => {
          this.toaster.openSuccessToaster('تم الحذف بنجاح', 'check_circle');
          this.resetParam();

        })
      ).subscribe();


    }
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
      this.employeeIdInput.nativeElement.value = '';
      this.param.id = null;
    }
    if (input === 'collage') {
      this.collageInput.nativeElement.value = '';
      this.param.collage = null;
    }
    if (input === 'department') {
      this.departmentInput.nativeElement.value = '';
      this.param.departmentName = null;
    }
    this.loadData();

  }
  clearAll() {
    this.resetParam();
    this.tabCodeInput.nativeElement.value = '';
    this.tegaraCodeInput.nativeElement.value = '';
    this.nameInput.nativeElement.value = '';
    this.employeeIdInput.nativeElement.value = '';
    this.collageInput.nativeElement.value = '';
    this.departmentInput.nativeElement.value = '';
    //  this.loadData();
  }
  openEmployeeEditDialog(row) {
    const dialogRef = this._dialog.open(EditEmployeeDialogComponent, {
      width: '600px',
      disableClose: true,
      data: { employeeId: row.id },
      panelClass: ['dialog-container'],


    });

    dialogRef.afterClosed().subscribe(result => {
      this.ngOnInit();

      this.clearAll();

      // this.animal = result;
    });
  }

  resetParam() {
    this.param.pageIndex = 0;
    this.param.pageSize = this.paginator.pageSize;
    this.param.name = null;
    this.param.tegaraCode = null;
    this.param.tabCode = null;
    this.param.id = null;
    this.param.collage = null;
    this.param.departmentName = null;

    this.loadData();
  }

  trackByKey(index: number, item: any): string {
    return item.id;
  }
}

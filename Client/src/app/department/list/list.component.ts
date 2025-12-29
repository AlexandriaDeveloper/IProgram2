import { routes } from './../../app.routes';
import { trigger, transition, style, animate } from '@angular/animations';
import { EmployeeParam } from './../../shared/models/IEmployee';
import { ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { DepartmentService } from '../../shared/service/department.service';
import { DepartmentParam } from '../../shared/models/Department';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable } from '@angular/material/table';
import { fromEvent, debounceTime, distinctUntilChanged, map } from 'rxjs';
import { AddDailyComponent } from '../../daily/add-daily/add-daily.component';
import { IEmployee } from '../../shared/models/IEmployee';
import { AddDepartmentDialogComponent } from './add-department-dialog/add-department-dialog.component';
import { Router } from '@angular/router';
import { EmployeeService } from '../../shared/service/employee.service';

@Component({
  selector: 'app-list',
  standalone: false,

  templateUrl: './list.component.html',
  styleUrl: './list.component.scss',
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
export class ListComponent implements OnInit {
  dialog = inject(MatDialog)
  displayedColumns = ['action', 'name', 'employeesCount'];
  departmentService = inject(DepartmentService);
  employeeService = inject(EmployeeService)
  employeeParam: EmployeeParam = new EmployeeParam()

  router: Router = inject(Router);
  public param: DepartmentParam = new DepartmentParam();
  dataSource;
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;
  @ViewChild("nameInput") nameInput: ElementRef;

  constructor(private cdref: ChangeDetectorRef) {
  }
  ngOnInit(): void {
    this.loadData();
    this.cdref.detectChanges();
  }

  ngAfterViewInit(): void {
    this.search();
  }
  search() {
    this.initElement(this.nameInput, 'name');
  }
  initElement(element: ElementRef, param) {


    fromEvent(element?.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
      map((event: any) => {
        // console.log(event.target.value);

        return event.target.value;
      })
    ).subscribe(x => {

      switch (param) {
        case 'name':
          this.param.name = x; break;
      }
      this.loadData();
    })
  }


  loadData() {
    this.departmentService.getDepartments(this.param).subscribe({
      next: (x: any) => {
        this.dataSource = x.data
        this.table.dataSource = x.data
        this.paginator.length = x.count;
        this.table.renderRows();
      }
    })
  }
  onChange(ev) {
    this.param.pageSize = ev.pageSize;
    this.param.pageIndex = ev.pageIndex;
    this.loadData();
  }
  openDialog(daily, edit): void {
    const dialogRef = this.dialog.open(AddDepartmentDialogComponent, {
      // data: {name: this.name, animal: this.animal},
      width: '40%',
      disableClose: true,
      data: { daily, edit }


    });

    dialogRef.afterClosed().subscribe(result => {
      this.loadData();
      // this.animal = result;
    });
  }
  clear(input) {
    if (input === 'name') {
      this.param.name = null;
      this.nameInput.nativeElement.value = null;
      this.loadData();
    }

  }

  deleteDaily(row) {
    if (confirm(`انت على وشك حذف يوميه " ${row.name}  "هل انت متأكد ؟؟!`)) {
      this.departmentService.deleteDepartment(row.id).subscribe({
        next: (x: any) => {
          this.loadData();
        }
      })
    }
  }

  getEmployeeByDepartment(id) {

    this.employeeService.GetEmployees(this.employeeParam).subscribe(x => {
      this.router.navigate(['/employee/list'], { queryParams: { departmentId: id } })
    })

  }

  trackByKey(index: number, item: any): string {
    return item.id;
  }
}

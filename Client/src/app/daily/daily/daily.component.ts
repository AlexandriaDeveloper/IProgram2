import { ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { trigger, transition, style, animate } from '@angular/animations';
import { DailyParam } from '../../shared/models/IDaily';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable } from '@angular/material/table';
import { IEmployee } from '../../shared/models/IEmployee';
import { DailyService } from '../../shared/service/daily.service';
import { debounceTime, distinctUntilChanged, fromEvent, map } from 'rxjs';
import { AddDailyComponent } from '../add-daily/add-daily.component';
import {
  MatDialog
} from '@angular/material/dialog';
import moment from 'moment';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { UploadJsonDialogComponent } from './upload-json-dialog/upload-json-dialog.component';
import { ToasterService } from '../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-daily',
  standalone: false,
  templateUrl: './daily.component.html',
  styleUrl: './daily.component.scss',
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
export class DailyComponent implements OnInit {
  dialog = inject(MatDialog)
  displayedColumns = ['action', 'name', 'dailyDate', 'closed'];
  dailyService = inject(DailyService);
  public param: DailyParam = new DailyParam();
  dataSource;
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;
  @ViewChild("nameInput") nameInput: ElementRef;
  @ViewChild("dateInput") dateInput;
  bottomSheet = inject(MatBottomSheet)
  toaster = inject(ToasterService);
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
    this.dailyService.GetDailies(this.param).subscribe({
      next: (x: any) => {
        this.dataSource = x.data
        this.table.dataSource = x.data
        this.paginator.length = x.count;
        this.table.renderRows();
      }
    })
  }
  onStartDateChange(ev) {

    if (ev == null) {
      this.param.startDate = null;
      return;
    }
    var date = moment(ev).format('MM/DD/YYYY')
    this.param.startDate = date;
  }
  onEndDateChange(ev) {
    if (ev == null) {
      this.param.endDate = null;
      return;
    }
    var date = moment(ev).format('MM/DD/YYYY')

    this.param.endDate = date;
    this.loadData();

  }
  onChange(ev) {
    this.param.pageSize = ev.pageSize;
    this.param.pageIndex = ev.pageIndex;
    this.loadData();
  }
  openDialog(daily, edit): void {
    const dialogRef = this.dialog.open(AddDailyComponent, {
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
    if (input === 'date') {
      // this.test();
      this.param.startDate = null;
      this.param.endDate = null;
      this.loadData();
    }

  }
  clearDateRange(start, end) {
    this.param.startDate = null;
    this.param.endDate = null;
    start.value = null;
    end.value = null;
    this.dateInput.value.start = null;
    this.dateInput.value.end = null;
    this.loadData();
  }
  deleteDaily(row) {
    if (confirm(`انت على وشك حذف يوميه " ${row.name}  "هل انت متأكد ؟؟!`)) {
      this.dailyService.deleteDaily(row.id).subscribe({
        next: (x: any) => {
          this.loadData();
        }
      })
    }
  }

  uploadJson() {
    this.bottomSheet.open(UploadJsonDialogComponent, {
      panelClass: ['bottomSheet'],
      hasBackdrop: true,
      data: {

      }


    });

    this.bottomSheet._openedBottomSheetRef.afterDismissed().subscribe(result => {
      if (result) {
        this.loadData();
        this.toaster.openSuccessToaster('تم رفع الملف  بنجاح', 'check_circle');
      }
    })
  }


  trackByKey(index: number, item: any): string {
    return item.id;
  }
}







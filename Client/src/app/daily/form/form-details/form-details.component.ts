import { ActivatedRoute, Router } from '@angular/router';
import { FormDetailsService } from './../../../shared/service/form-details.service';
import { AfterViewInit, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { DescriptionDialogComponent } from './description-dialog/description-dialog.component';
import { MatDialog } from '@angular/material/dialog';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable, MatTableDataSource } from '@angular/material/table';
import { IEmployee } from '../../../shared/models/IEmployee';
import { AddEmployeeDialogComponent } from './add-employee-dialog/add-employee-dialog.component';
import { moveItemInArray, CdkDragDrop } from '@angular/cdk/drag-drop';
import { fromEvent, debounceTime, distinctUntilChanged, map, Subscription } from 'rxjs';
import { MatCheckboxChange } from '@angular/material/checkbox';
import { FormService } from '../../../shared/service/form.service';
import { ToasterService } from '../../../shared/components/toaster/toaster.service';
import { ReferencesDialogComponent } from './references-dialog/references-dialog.component';
import { UploadReferencesDialogComponent } from './upload-references-dialog/upload-references-dialog.component';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { UploadExcelFileBottomComponent } from './upload-excel-file-bottom/upload-excel-file-bottom.component';
import { IDaily } from '../../../shared/models/IDaily';
import { DailyService } from '../../../shared/service/daily.service';
import { UploadPdfBottomComponent } from './upload-pdf-bottom/upload-pdf-bottom.component';



@Component({
  selector: 'app-form-details',
  standalone: false,
  templateUrl: './form-details.component.html',
  styleUrl: './form-details.component.scss'
})
export class FormDetailsComponent implements OnInit, AfterViewInit {

  dialog = inject(MatDialog)
  formDetailsService = inject(FormDetailsService);
  formService = inject(FormService);
  dailyService = inject(DailyService);
  router = inject(Router)
  id = inject(ActivatedRoute).snapshot.params['formid']
  dailyId = inject(ActivatedRoute).snapshot.params['id']
  daily: IDaily;
  toasterService = inject(ToasterService);
  bottomSheet = inject(MatBottomSheet)
  data: any;
  dataSource: MatTableDataSource<IEmployee>;
  filteredData: IEmployee[] = []
  // (multi-sort UI removed) keep single-column MatSort
  // active filter values per column
  filterValues: { [key: string]: string } = {};
  displayedColumns = ['action', 'tabCode', 'tegaraCode', 'name', 'department', 'employeeId', 'amount']
  isLoading = false;

  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;

  @ViewChild("tabCodeInput") tabCodeInput: ElementRef;
  @ViewChild("tegaraCodeInput") tegaraCodeInput: ElementRef;
  @ViewChild("nameInput") nameInput: ElementRef;
  @ViewChild("departmentInput") departmentInput: ElementRef;
  @ViewChild("employeeIdInput") employeeIdInput: ElementRef;
  @ViewChild("amountInput") amountInput: ElementRef;


  tabSub: Subscription;

  ngOnInit(): void {
    this.loadDaily();
    this.loadData();
  }
  loadDaily() {
    if (this.dailyId !== undefined) {
      this.dailyService.getDaily(this.dailyId).subscribe(x => {
        console.log(x);

        this.daily = x
      })
    }
    else {
      this.daily = {
        id: null,
        dailyDate: new Date(),
        name: 'ارشيف',

      }
    }

  }
  ngAfterViewInit(): void {
    // attach sort/paginator after view init
    if (this.dataSource) {
      this.dataSource.sort = this.sort;
      if (this.paginator) this.dataSource.paginator = this.paginator;
    }
    this.search();
  }

  // multi-sort removed: using single-column MatSort only
  search() {
    this.initElement(this.tabCodeInput, 'tabCode');
    this.initElement(this.tegaraCodeInput, 'tegaraCode');
    this.initElement(this.nameInput, 'name');
    this.initElement(this.employeeIdInput, 'employeeId');
    this.initElement(this.amountInput, 'amount');
    this.initElement(this.departmentInput, 'department');

  }
  initElement(element: ElementRef, param) {
    return fromEvent(element.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
      map((event: any) => {
        const value = (event.target.value || '').toString().trim();
        // update the filter value for this column and apply
        this.filterValues[param] = value;
        this.applyFilter();
        return event.target.value;
      })
    ).subscribe();
  }


  loadData() {

    this.formDetailsService.GetFormDetails(this.id).subscribe(x => {
      console.log(x);

      this.data = x;
      // set MatTableDataSource so sort/filter work
      this.dataSource = new MatTableDataSource<IEmployee>(x.formDetails);
      // setup filter predicate that checks each active filter value
      this.dataSource.filterPredicate = (data: IEmployee, filter: string) => {
        const fv = JSON.parse(filter || '{}');
        // iterate filters
        for (const key of Object.keys(fv)) {
          const filterVal = (fv[key] || '').toString().trim();
          if (!filterVal) continue;
          const cell = (data as any)[key];
          if (cell === null || cell === undefined) return false;
          const cellStr = cell.toString();
          // for name/employeeId/department do substring match, else startsWith
          if (key === 'name' || key === 'employeeId' || key === 'department') {
            if (!cellStr.includes(filterVal)) return false;
          } else {
            if (!cellStr.startsWith(filterVal)) return false;
          }
        }
        return true;
      };
      // initialize filters
      this.filterValues = {};
      this.dataSource.filter = JSON.stringify(this.filterValues);
      // attach sort/paginator if available
      if (this.sort) this.dataSource.sort = this.sort;
      if (this.paginator) this.dataSource.paginator = this.paginator;
    })
  }
  onChange(ev) {

  }


  openDescriptionDialog() {

    const dialogRef = this.dialog.open(DescriptionDialogComponent, {
      data: { form: this.data },
      width: '80%',
      height: '600px',
      disableClose: true,



    });

    dialogRef.afterClosed().subscribe(result => {
      this.loadData();
    });

  }

  openReferenceDialog() {

    const dialogRef = this.dialog.open(ReferencesDialogComponent, {
      data: { formId: this.data.id },
      width: '80%',
      height: '610px',
      disableClose: true,
      panelClass: ['dialog-container'],
    });

    dialogRef.afterClosed().subscribe(result => {
      this.loadData();
    });

  }
  uploadReferenceDialog() {
    const dialogRef = this.dialog.open(UploadReferencesDialogComponent, {
      // data: {name: this.name, animal: this.animal},


      disableClose: true,
      data: { formId: this.id },
      panelClass: ['dialog-container'],


    });

    dialogRef.afterClosed().subscribe(result => {
      this.ngOnInit();
      // this.animal = result;
    });
  }


  openUploadExcelBottomSheet() {
    this.bottomSheet.open(UploadExcelFileBottomComponent, {
      panelClass: ['bottomSheet'],
      hasBackdrop: true,
      data: {
        formId: this.id
        //  departmentId:this.param.departmentId
      }


    });

    this.bottomSheet._openedBottomSheetRef.afterDismissed().subscribe(result => {
      if (result) {
        this.loadData();
        // this.toaster.openSuccessToaster('تم رفع الملف  بنجاح','check_circle');
      }
    })
  }

  openPdfUploadSheet() {
    const bottomSheetRef = this.bottomSheet.open(UploadPdfBottomComponent, {
      panelClass: ['bottomSheet'],
      hasBackdrop: true,
      data: {
        dailyId: this.dailyId
      }
    });

    bottomSheetRef.afterDismissed().subscribe(result => {
      if (result) { // result is true on successful upload
        this.loadData(); // Or any other action needed
        this.toasterService.openSuccessToaster('تم رفع مرجع PDF بنجاح');
      }
    });
  }

  addEmployeeFormDialog(model) {
    const dialogRef = this.dialog.open(AddEmployeeDialogComponent, {
      width: '50%',
      data: { employeeDetails: model, formId: this.id },
      disableClose: true,
    })
    dialogRef.afterClosed().subscribe(result => {
      this.loadData();
      // this.animal = result;
    });
  }
  deleteEmployee(row) {
    if (confirm(`انت على وشك حذف الموظف ${row.name} هل انت متاكد ؟؟!`)) {
      this.formDetailsService.deleteFormDetails(row.id).subscribe(x => this.loadData())
    }
    // this.formDetailsService.deleteFormDetails(rowId).subscribe(x=>this.loadData())
  }
  exportPdf() {
    this.formDetailsService.exportForms(this.id).subscribe()
  }
  drop(event: CdkDragDrop<IEmployee[]>) {
    if (this.daily?.closed) return;

    // If no dataSource yet, nothing to do
    if (!this.dataSource) return;

    // Use previousIndex provided by the event when possible
    const previousIndex = event.previousIndex;
    const currentIndex = event.currentIndex;

    // Make a shallow copy of the data array, mutate it, then replace the data reference
    const newData = this.dataSource.data.slice();
    moveItemInArray(newData, previousIndex, currentIndex);

    // Replace dataSource.data with a new array reference so MatTable detects change
    this.dataSource.data = newData;

    // Persist the new order to the server
    this.reOrderRows();
  }
  reOrderRows() {
    const ids = this.dataSource.data.map(x => x.id);
    console.log('reOrderRows ids', ids);

    this.formDetailsService.reOrderRows(this.id, ids).subscribe({
      next: () => {
        // ensure the table renders with the updated data reference
        try { this.table.renderRows(); } catch { }
      },
      error: (err) => {
        // if server update failed, reload data to restore UI
        console.error('Failed to persist row order', err);
        this.loadData();
      }
    });
  }
  markAsReviewed(row: IEmployee, event: boolean) {
    if (this.daily?.closed) return;
    //confirm you uncheck 
    console.log(row);
    console.log(event);

    if (!event) {
      if (!confirm("انت على وشك الغاء مراجعة البيان هل انت متأكد ؟")) {
        this.loadData();
        return;

      }

    }
    this.formDetailsService.markAsReviewed(Number(row.id), event).subscribe(x => {
      this.loadData();
    })
  }
  clear(input) {
    if (input == 'tabCode') {
      this.tabCodeInput.nativeElement.value = ''
    }
    if (input == 'tegaraCode') {
      this.tegaraCodeInput.nativeElement.value = ''
    }
    if (input == 'name') {
      this.nameInput.nativeElement.value = ''
    }
    if (input == 'department') {
      this.departmentInput.nativeElement.value = ''
    }
    if (input == 'employeeId') {
      this.employeeIdInput.nativeElement.value = ''
    }
    if (input == 'amount') {
      this.amountInput.nativeElement.value = ''
    }
    // reset the input element value and the corresponding filter value
    this.filterValues[input] = '';
    if (this[input + 'Input'] && this[input + 'Input'].nativeElement) {
      try { this[input + 'Input'].nativeElement.value = ''; } catch { }
    }
    // reapply filters (this will show original data and keep multi-sort)
    this.applyFilter();
  }

  applyFilter() {
    if (!this.dataSource) return;
    // set the filter string to the serialized filter values
    this.dataSource.filter = JSON.stringify(this.filterValues || {});
  }
  moveFromDailyToArchive() {
    if (confirm(`هل تريد الغاء استمارة ${this.data.name} ؟من اليوميه!`)) {

      this.formService.moveFormDetailsDailyArchive({ formId: this.id, dailyId: null }).subscribe(x => {
        this.router.navigateByUrl(`/daily/${this.dailyId}/form`);
      });

    }
  }
  copyFormToArchive() {

    this.formService.CopyFormToArchive(this.id).subscribe({
      next: (x: any) => {
        this.toasterService.openSuccessToaster('تم نسخ النموذج بنجاح')
      }
    })
  }
  downloadExcel() {
    this.isLoading = true
    this.formService.downloadExcelForm({ formId: this.id, formTitle: this.data.name }).subscribe(
      {
        complete: () => {
          this.isLoading = false
        }
      }

    )
  }




}

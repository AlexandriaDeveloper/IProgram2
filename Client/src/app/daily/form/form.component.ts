
import { Param } from './../../shared/models/Param';
import { trigger, transition, style, animate } from '@angular/animations';
import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { FormArray, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ReportpdfService } from '../../shared/service/reportpdf.service';
import { EmployeeService } from '../../shared/service/employee.service';
import { MatDialog } from '@angular/material/dialog';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable } from '@angular/material/table';
import { DailyParam, IDaily } from '../../shared/models/IDaily';
import { IEmployee } from '../../shared/models/IEmployee';
import { DailyService } from '../../shared/service/daily.service';
import { FormService } from '../../shared/service/form.service';
import { ActivatedRoute, Router } from '@angular/router';
import { AddFormComponent } from './add-form/add-form.component';
import { FormParam } from '../../shared/models/IForm';
import { AddEmployeeDialogComponent } from './form-details/add-employee-dialog/add-employee-dialog.component';
import { debounceTime, distinctUntilChanged, fromEvent, map } from 'rxjs';
import { AuthService } from '../../shared/service/auth.service';
import { UploadPdfBottomComponent } from './form-details/upload-pdf-bottom/upload-pdf-bottom.component';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { environment } from '../../environment';
import { DailyReferencesService } from '../../shared/service/daily-references.service';

@Component({
  selector: 'app-form',
  standalone: false,

  templateUrl: './form.component.html',
  styleUrl: './form.component.scss',
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
export class FormComponent implements OnInit, AfterViewInit {

  //dailyId;
  dialog = inject(MatDialog)
  displayedColumns = ['action', 'index', 'name', 'createdBy', 'count', 'total', 'isReviewed'];
  formService = inject(FormService);
  dailyService = inject(DailyService);
  dailyRefService = inject(DailyReferencesService);
  authService = inject(AuthService);
  dailyId = inject(ActivatedRoute).snapshot.params['id'];
  router = inject(Router);
  daily: IDaily;
  public param: FormParam = new FormParam();
  dataSource;
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;
  @ViewChild("indexInput") indexInput: ElementRef;
  @ViewChild("nameInput") nameInput: ElementRef;
  @ViewChild("createdByInput") createdByInput: ElementRef;
  @ViewChild("countInput") countInput: ElementRef;
  @ViewChild("totalInput") totalInput: ElementRef;
  //@ViewChild("dateInput") dateInput ;

  pdfReportService = inject(ReportpdfService);
  employeeService = inject(EmployeeService)
  bottomSheet = inject(MatBottomSheet)
  form: FormGroup
  fb = inject(FormBuilder);
  constructor(private cdref: ChangeDetectorRef) {

  }
  ngAfterViewInit(): void {
    this.onSearch();
  }
  ngOnInit(): void {

    this.form = this.initilizeForm();
    this.loadData();
    console.log(this.authService.isUserAdmin());
    this.cdref.detectChanges();
  }


  initilizeForm() {
    return this.fb.group({
      description: [],
      formDetails: this.fb.array([])
    })
  }



  onSubmit() {

  }
  loadData() {
    console.log();
    this.dailyService.getDaily(this.dailyId).subscribe({
      next: (x: IDaily) => {
        this.daily = x

      }
    })

    this.formService.GetForms(this.dailyId, this.param).subscribe({
      next: (x: any) => {
        this.dataSource = x.data
        console.log(this.dataSource);

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
  openEditDialog(model): void {

    const dialogRef = this.dialog.open(AddFormComponent, {
      // data: {name: this.name, animal: this.animal},
      width: '40%',

      disableClose: true,
      data: { form: model, dailyId: this.dailyId }


    });

    dialogRef.afterClosed().subscribe(result => {
      //this.loadData();
      //redirect me to the form details http://localhost:4200/daily/81/form/2128
      console.log(result);
      this.router.navigate(['/daily/' + result.dailyId + '/form/' + result.id]);

      // this.animal = result;ud
    });
  }


  onDelete(row) {
    if (confirm(` أنت على وشك حذف ملف ${row.name} هل انت متاكد ؟؟!`)) {
      this.formService.deleteForm(row.id).subscribe({
        next: (x: any) => {
          this.loadData();
        }
      })
    }
  }
  exportPdf() {
    this.formService.exportFormsInsidDaily(this.dailyId).subscribe({
      next: (x: any) => {
        // console.log(x);
      }
    })
  }
  exportIndexPdf() {
    this.formService.exportDailyIndex(this.dailyId).subscribe({
      next: (x: any) => {
        // console.log(x);
      }
    })
  }

  onSearch() {
    fromEvent(this.indexInput.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
      map((event: any) => {
        return event.target.value;
      })
    ).subscribe(x => {

      this.param.index = x
      this.loadData()
    })

    fromEvent(this.nameInput.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
      map((event: any) => {
        return event.target.value;
      })
    ).subscribe(x => {

      this.param.name = x
      this.loadData()
    })

    fromEvent(this.createdByInput.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
      map((event: any) => {
        return event.target.value;
      })
    ).subscribe(x => {

      this.param.createdBy = x
      this.loadData()
    })


  }


  clear(input) {
    if (input === 'index') {
      this.indexInput.nativeElement.value = ''
      this.param.index = null
      this.loadData();

    }
    if (input === 'name') {
      this.nameInput.nativeElement.value = ''
      this.param.name = ''
      this.loadData();
    }
    if (input === 'createdBy') {
      this.createdByInput.nativeElement.value = ''
      this.param.createdBy = ''
      this.loadData();
    }

  }
  downloadExcel() {
    this.dailyService.downloadExcelDaily(this.dailyId).subscribe({

    })
  }
  downloadJSON() {
    this.dailyService.downloadJSONDaily(this.dailyId).subscribe({

    })
  }
  uploadPdf() {
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
        // this.toasterService.openSuccessToaster('تم رفع مرجع PDF بنجاح');
      }
    });

  }
  closeDaily() {

    this.dailyService.closeDaily(this.dailyId).subscribe({
      next: (x: any) => {
        this.loadData();
      }
    })
  }
  openReferenceDialog(dailyReference) {
    console.log(dailyReference);

    window.open(environment.apiContent + dailyReference.referencePath, '_blank');
  }
  restoreForm(row) {
    if (confirm(` أنت على وشك استرجاع ملف ${row.name} هل انت متاكد ؟؟!`)) {
      this.formService.restoreForm(row.id).subscribe({
        next: (x: any) => {
          this.loadData();
        }
      })
    }
  }

  deleteReference(dailyReference: any, event?: Event) {
    if (event) {
      event.stopPropagation(); // Prevent the click from bubbling up to parent
    }
    if (confirm(` أنت على وشك حذف مرجع ${dailyReference.name} هل انت متاكد ؟؟!`)) {
      // Add your delete logic here
      console.log('Delete reference:', dailyReference);
      this.dailyRefService.deleteDailyReference(dailyReference.id).subscribe({
        next: (x: any) => {
          this.loadData();
        }
      });
    }
  }

  trackByKey(index: number, item: any): string {
    return item.id;
  }
}

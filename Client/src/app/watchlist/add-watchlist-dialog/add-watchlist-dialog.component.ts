import { Component, Inject, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogRef, MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { FormBuilder, FormGroup, Validators, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatNativeDateModule } from '@angular/material/core';
import { MatIconModule } from '@angular/material/icon';
import { MatCardModule } from '@angular/material/card';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { DragDropModule } from '@angular/cdk/drag-drop';
import { WatchlistService } from '../../shared/service/watchlist.service';
import { EmployeeService } from '../../shared/service/employee.service';
import { EmployeeParam, IEmployee } from '../../shared/models/IEmployee';
import { MatSnackBar } from '@angular/material/snack-bar';
import { debounceTime, distinctUntilChanged } from 'rxjs/operators';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

@Component({
  selector: 'app-add-watchlist-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatDatepickerModule,
    MatNativeDateModule,
    MatIconModule,
    MatCardModule,
    MatButtonToggleModule,
    DragDropModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './add-watchlist-dialog.component.html',
  styleUrls: ['./add-watchlist-dialog.component.scss']
})
export class AddWatchlistDialogComponent {
  form: FormGroup;
  watchlistService = inject(WatchlistService);
  employeeService = inject(EmployeeService);
  snackBar = inject(MatSnackBar);
  isSubmitting = false;
  isSearching = false;
  foundEmployeeName: string | null = null;
  searchError: string | null = null;

  constructor(
    public dialogRef: MatDialogRef<AddWatchlistDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { searchType?: string, nationalId?: string, tabCode?: number, tegaraCode?: number },
    private fb: FormBuilder
  ) {
    const initialSearchType = data?.searchType || 'nationalId';
    
    this.form = this.fb.group({
      searchType: [initialSearchType, Validators.required],
      nationalId: [data?.nationalId || ''],
      tabCode: [data?.tabCode || null],
      tegaraCode: [data?.tegaraCode || null],
      reason: ['', Validators.required],
      expiresAt: [null]
    });

    // Set initial validators based on search type
    this.updateValidators(initialSearchType);

    // Watch for search type changes to toggle validators and clear search results
    this.form.get('searchType')?.valueChanges.subscribe((type: string) => {
      this.updateValidators(type);
      this.foundEmployeeName = null;
      this.searchError = null;
    });

    // Setup auto-search for all fields
    ['nationalId', 'tabCode', 'tegaraCode'].forEach(field => {
      this.form.get(field)?.valueChanges.pipe(
        debounceTime(600),
        distinctUntilChanged()
      ).subscribe(() => this.searchEmployee());
    });

    // Trigger initial search if data is passed
    if (data?.nationalId || data?.tabCode || data?.tegaraCode) {
      setTimeout(() => this.searchEmployee(), 100);
    }
  }

  searchEmployee() {
    const val = this.form.value;
    const param = new EmployeeParam();
    
    let identifier: any = '';
    if (val.searchType === 'nationalId') identifier = val.nationalId;
    else if (val.searchType === 'tabCode') identifier = val.tabCode;
    else if (val.searchType === 'tegaraCode') identifier = val.tegaraCode;

    // Reset results if identifier is too short
    if (!identifier || identifier.toString().length < 2) {
      this.foundEmployeeName = null;
      this.searchError = null;
      return;
    }

    this.isSearching = true;
    this.foundEmployeeName = null;
    this.searchError = null;

    if (val.searchType === 'nationalId') param.id = identifier;
    else if (val.searchType === 'tabCode') param.tabCode = identifier;
    else if (val.searchType === 'tegaraCode') param.tegaraCode = identifier;

    this.employeeService.GetEmployee(param).subscribe({
      next: (emp) => {
        this.isSearching = false;
        if (emp && emp.name) {
          this.foundEmployeeName = emp.name;
        } else {
          this.searchError = 'لم يتم العثور على الموظف';
        }
      },
      error: (err) => {
        this.isSearching = false;
        this.searchError = 'خطأ في البحث عن الموظف';
        console.error(err);
      }
    });
  }

  private updateValidators(searchType: string) {
    const nationalIdCtrl = this.form.get('nationalId');
    const tabCodeCtrl = this.form.get('tabCode');
    const tegaraCodeCtrl = this.form.get('tegaraCode');

    // Clear all
    nationalIdCtrl?.clearValidators();
    tabCodeCtrl?.clearValidators();
    tegaraCodeCtrl?.clearValidators();

    // Set required for active field
    switch (searchType) {
      case 'nationalId':
        nationalIdCtrl?.setValidators(Validators.required);
        break;
      case 'tabCode':
        tabCodeCtrl?.setValidators(Validators.required);
        break;
      case 'tegaraCode':
        tegaraCodeCtrl?.setValidators(Validators.required);
        break;
    }

    nationalIdCtrl?.updateValueAndValidity();
    tabCodeCtrl?.updateValueAndValidity();
    tegaraCodeCtrl?.updateValueAndValidity();
  }

  onSubmit() {
    if (this.form.invalid) return;

    this.isSubmitting = true;
    const val = this.form.value;

    const requestData: any = {
      searchType: val.searchType,
      reason: val.reason,
      expiresAt: val.expiresAt ? this.formatDate(val.expiresAt) : undefined
    };

    // Only send the relevant field
    switch (val.searchType) {
      case 'nationalId':
        requestData.nationalId = val.nationalId;
        break;
      case 'tabCode':
        requestData.tabCode = parseInt(val.tabCode);
        break;
      case 'tegaraCode':
        requestData.tegaraCode = parseInt(val.tegaraCode);
        break;
    }

    this.watchlistService.addToWatchList(requestData).subscribe({
      next: () => {
        this.snackBar.open('تم إضافة الموظف لقائمة الانتباه بنجاح', 'إغلاق', { duration: 3000 });
        this.dialogRef.close(true);
      },
      error: (err) => {
        console.error(err);
        const msg = err.error?.message || err.error || 'حدث خطأ أثناء الإضافة';
        this.snackBar.open(msg, 'إغلاق', { duration: 5000 });
        this.isSubmitting = false;
      }
    });
  }

  onCancel() {
    this.dialogRef.close();
  }

  private formatDate(date: Date): string {
    const d = new Date(date);
    let month = '' + (d.getMonth() + 1);
    let day = '' + d.getDate();
    const year = d.getFullYear();

    if (month.length < 2) month = '0' + month;
    if (day.length < 2) day = '0' + day;

    return [year, month, day].join('-');
  }
}

import { Component, Inject, OnInit, inject } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { EmployeeService } from '../../../../shared/service/employee.service';
import { ToasterService } from '../../../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-change-national-id-dialog',
  standalone: false,
  templateUrl: './change-national-id-dialog.component.html',
  styleUrls: ['./change-national-id-dialog.component.scss']
})
export class ChangeNationalIdDialogComponent implements OnInit {
  form: FormGroup;
  fb = inject(FormBuilder);
  toaster = inject(ToasterService);
  employeeService = inject(EmployeeService);

  constructor(
    private dialogRef: MatDialogRef<ChangeNationalIdDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { employeeId: string }
  ) {}

  ngOnInit(): void {
    this.form = this.fb.group({
      oldNationalId: [{ value: this.data.employeeId, disabled: true }, Validators.required],
      newNationalId: ['', [Validators.required, Validators.minLength(14), Validators.maxLength(14)]]
    });
  }

  onNoClick(): void {
    this.dialogRef.close();
  }

  onSubmit() {
    if (this.form.invalid) return;

    const oldId = this.data.employeeId;
    const newId = this.form.get('newNationalId').value;

    this.employeeService.changeNationalId(oldId, newId).subscribe({
      next: (res) => {
        this.toaster.openSuccessToaster('تم تعديل الرقم القومي بنجاح', 'check');
        this.dialogRef.close({ success: true, newId: newId });
      },
      error: (err) => {
        console.error(err);
        this.toaster.openErrorToaster(err?.error?.message || 'حدث خطأ ما', 'error');
      }
    });
  }
}

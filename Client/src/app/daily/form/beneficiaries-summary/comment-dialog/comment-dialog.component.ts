import { Component, Inject, OnInit, inject } from '@angular/core';
import { FormBuilder, FormGroup } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { DailyService } from '../../../../shared/service/daily.service';

@Component({
  selector: 'app-comment-dialog',
  templateUrl: './comment-dialog.component.html',
  styleUrls: ['./comment-dialog.component.scss'],
  standalone: false
})
export class CommentDialogComponent implements OnInit {
  dailyService = inject(DailyService);
  fb = inject(FormBuilder);

  form!: FormGroup;
  isSaving = false;

  constructor(
    private dialogRef: MatDialogRef<CommentDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { dailyId: number, employeeId: string, employeeName: string, comment: string }
  ) { }

  ngOnInit(): void {
    this.form = this.fb.group({
      comment: [this.data.comment || '']
    });
  }

  onNoClick(): void {
    this.dialogRef.close();
  }

  onSubmit(): void {
    if (this.form.invalid) return;

    this.isSaving = true;
    const body = {
      employeeId: this.data.employeeId,
      comment: this.form.value.comment
    };

    this.dailyService.updateBeneficiaryComment(this.data.dailyId, body).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.dialogRef.close({ saved: true, comment: this.form.value.comment });
      },
      error: () => {
        this.isSaving = false;
      }
    });
  }
}

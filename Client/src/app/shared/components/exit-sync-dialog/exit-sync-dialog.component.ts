import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogRef, MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { SyncService } from '../../service/sync.service';

export interface ExitSyncDialogData {
  pendingCount: number;
}

export type ExitSyncDialogResult = 'PUSH_AND_EXIT' | 'EXIT_WITHOUT_PUSH' | 'CANCEL';

@Component({
  selector: 'app-exit-sync-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './exit-sync-dialog.component.html',
  styleUrls: ['./exit-sync-dialog.component.scss']
})
export class ExitSyncDialogComponent {
  dialogRef = inject(MatDialogRef<ExitSyncDialogComponent>);
  data: ExitSyncDialogData = inject(MAT_DIALOG_DATA);
  syncService = inject(SyncService);

  isPushing = signal<boolean>(false);
  errorMessage = signal<string | null>(null);

  pushAndExit() {
    this.isPushing.set(true);
    this.errorMessage.set(null);

    this.syncService.pushNow().subscribe({
      next: () => {
        this.isPushing.set(false);
        this.dialogRef.close('PUSH_AND_EXIT' as ExitSyncDialogResult);
      },
      error: (err) => {
        this.isPushing.set(false);
        this.errorMessage.set(err.error?.message || err.message || 'فشل رفع التعديلات للسحابة');
      }
    });
  }

  exitWithoutPush() {
    this.dialogRef.close('EXIT_WITHOUT_PUSH' as ExitSyncDialogResult);
  }

  cancel() {
    this.dialogRef.close('CANCEL' as ExitSyncDialogResult);
  }
}

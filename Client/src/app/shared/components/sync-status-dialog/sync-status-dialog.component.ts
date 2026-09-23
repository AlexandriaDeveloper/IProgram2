import { Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SyncService, OnlineSyncStatus, ScopeSyncStatus } from '../../service/sync.service';

@Component({
  selector: 'app-sync-status-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule
  ],
  templateUrl: './sync-status-dialog.component.html',
  styleUrls: ['./sync-status-dialog.component.scss']
})
export class SyncStatusDialogComponent implements OnInit {
  dialogRef = inject(MatDialogRef<SyncStatusDialogComponent>);
  syncService = inject(SyncService);

  ngOnInit(): void {
    // P0-4: Opening status dialog is strictly LOCAL-ONLY (zero remote contact).
    // Zero automatic remote check on ngOnInit.
    this.syncService.fetchLocalStatus().subscribe();
  }

  refreshStatus(): void {
    this.syncService.checkOnlineStatus().subscribe();
  }

  pushNow(): void {
    this.syncService.pushNow().subscribe({
      next: () => {
        this.refreshStatus();
      },
      error: () => {
        // error handled in syncService
      }
    });
  }

  pullNow(): void {
    this.syncService.pullNow().subscribe({
      next: () => {
        this.refreshStatus();
      },
      error: () => {
        // error handled in syncService
      }
    });
  }

  close(): void {
    this.dialogRef.close();
  }

  getStatusBadgeClass(status: string): string {
    switch (status) {
      case 'UP_TO_DATE':
        return 'badge-up-to-date';
      case 'LOCAL_PENDING':
        return 'badge-local-pending';
      case 'REMOTE_NEWER':
        return 'badge-remote-newer';
      case 'BOTH_CHANGED':
        return 'badge-both-changed';
      case 'NOT_BASELINED':
        return 'badge-not-baselined';
      case 'SYNC_STATE_ERROR':
        return 'badge-sync-error';
      case 'UNKNOWN':
      default:
        return 'badge-unknown';
    }
  }

  getStatusText(status: string): string {
    switch (status) {
      case 'UP_TO_DATE':
        return 'متطابق مع السحابة (UP_TO_DATE)';
      case 'LOCAL_PENDING':
        return 'تعديلات محلية بانتظار الرفع (LOCAL_PENDING)';
      case 'REMOTE_NEWER':
        return 'تحديثات سحابية أحدث (REMOTE_NEWER)';
      case 'BOTH_CHANGED':
        return 'تعديلات محلية وسحابية متزامنة (BOTH_CHANGED)';
      case 'NOT_BASELINED':
        return 'غير مطابق الأساس (NOT_BASELINED)';
      case 'SYNC_STATE_ERROR':
        return 'خطأ في حالة المزامنة (SYNC_STATE_ERROR)';
      case 'UNKNOWN':
      default:
        return 'غير معروف / تعذر الاتصال (UNKNOWN)';
    }
  }
}

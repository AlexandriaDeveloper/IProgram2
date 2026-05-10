import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCardModule } from '@angular/material/card';
import { DragDropModule } from '@angular/cdk/drag-drop';

@Component({
  selector: 'app-watchlist-alert-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatCardModule,
    DragDropModule
  ],
  template: `
    <div cdkDrag cdkDragRootElement=".cdk-overlay-pane" class="custom-theme">
      <mat-card class="watchlist-alert-card">
        <mat-card-header class="dialog-header">
          <mat-icon mat-card-avatar class="alert-icon">notification_important</mat-icon>
          <mat-card-title>تنبيه: قائمة الانتباه</mat-card-title>
          <span class="spacer"></span>
          <button mat-icon-button (click)="onCancel()" class="close-button">
            <mat-icon>close</mat-icon>
          </button>
        </mat-card-header>

        <mat-card-content class="dialog-content">
          <div class="employee-info-box">
            <div class="info-row">
              <span class="label">اسم الموظف:</span>
              <span class="value">{{ data.employeeName }}</span>
            </div>
          </div>

          <div class="alert-details">
            <div class="reason-header">
              <mat-icon>warning</mat-icon>
              <span>سبب الإدراج في القائمة</span>
            </div>
            <div class="reason-text">
              {{ data.reason }}
            </div>
          </div>

          <div class="confirmation-text">
            هل تود تأكيد المراجعة المالية لهذا الموظف؟
          </div>
        </mat-card-content>

        <mat-card-actions class="dialog-actions">
          <button mat-button class="btn-cancel" (click)="onCancel()">
            إلغاء
          </button>
          <button mat-raised-button class="btn-confirm" (click)="onConfirm()">
            إتمام المراجعة
          </button>
        </mat-card-actions>
      </mat-card>
    </div>
  `,
  styles: [`
    .watchlist-alert-card {
      border-radius: 12px;
      overflow: hidden;
      box-shadow: 0 15px 35px rgba(0,0,0,0.4) !important;
      direction: rtl;
      font-family: 'Cairo', sans-serif;
      background-color: #1b282e; /* $dark-primary-color */
      border: 1px solid rgba(255,255,255,0.1);
    }

    .dialog-header {
      background-color: rgba(255,255,255,0.05);
      color: white;
      padding: 15px 20px !important;
      display: flex;
      align-items: center;
      border-bottom: 1px solid rgba(255,255,255,0.1);
      
      .alert-icon {
        color: #ff5a00 !important; /* $accent-color */
        margin-left: 15px;
      }

      .mat-card-title {
        font-size: 19px;
        margin: 0;
        font-weight: 700;
        letter-spacing: 0.5px;
      }

      .close-button {
        color: rgba(255,255,255,0.6);
        &:hover { color: white; }
      }
    }

    .dialog-content {
      padding: 25px !important;
      background-color: transparent;

      .employee-info-box {
        margin-bottom: 25px;
        padding: 15px;
        border-right: 4px solid #ff5a00;
        background: rgba(255,255,255,0.03);
        border-radius: 4px;

        .info-row {
          display: flex;
          gap: 15px;
          align-items: center;
          
          .label {
            color: rgba(255,255,255,0.6);
            font-size: 15px;
          }
          
          .value {
            color: #fff;
            font-size: 20px;
            font-weight: 700;
            text-shadow: 0 2px 4px rgba(0,0,0,0.2);
          }
        }
      }

      .alert-details {
        background: rgba(255, 90, 0, 0.05);
        border: 1px solid rgba(255, 90, 0, 0.2);
        border-radius: 8px;
        margin-bottom: 25px;
        overflow: hidden;

        .reason-header {
          display: flex;
          align-items: center;
          gap: 10px;
          padding: 10px 15px;
          background: rgba(255, 90, 0, 0.15);
          color: #fa9c69; /* $info-color */
          font-weight: 700;
          font-size: 15px;
          
          mat-icon { font-size: 20px; width: 20px; height: 20px; }
        }

        .reason-text {
          padding: 15px;
          color: #ff8a80; /* Light red/orange for visibility */
          font-size: 17px;
          line-height: 1.6;
          font-weight: 400;
        }
      }

      .confirmation-text {
        text-align: center;
        color: rgba(255,255,255,0.7);
        font-size: 16px;
        font-weight: 600;
      }
    }

    .dialog-actions {
      padding: 20px 25px !important;
      display: flex;
      justify-content: flex-end;
      gap: 15px;
      background-color: rgba(0,0,0,0.2);
      border-top: 1px solid rgba(255,255,255,0.1);

      .btn-cancel {
        color: rgba(255,255,255,0.6);
        font-weight: 600;
        &:hover { color: white; background: rgba(255,255,255,0.05); }
      }

      .btn-confirm {
        background-color: #ff5a00; /* $accent-color */
        color: white;
        padding: 0 25px;
        height: 45px;
        font-weight: 700;
        font-size: 16px;
        border-radius: 8px;
        box-shadow: 0 4px 15px rgba(255, 90, 0, 0.3);
        
        &:hover {
          background-color: #ff7830;
          transform: translateY(-1px);
          box-shadow: 0 6px 20px rgba(255, 90, 0, 0.4);
        }
      }
    }

    .spacer { flex: 1 1 auto; }
  `]
})
export class WatchlistAlertDialogComponent {
  constructor(
    public dialogRef: MatDialogRef<WatchlistAlertDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: { employeeName: string, reason: string }
  ) {}

  onConfirm(): void {
    this.dialogRef.close(true);
  }

  onCancel(): void {
    this.dialogRef.close(false);
  }
}

import { Component, Inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { DailyService } from '../../../../shared/service/daily.service';
import { ToasterService } from '../../../../shared/components/toaster/toaster.service';

@Component({
    selector: 'app-netpay-dialog',
    templateUrl: './netpay-dialog.component.html',
    styleUrl: './netpay-dialog.component.scss',
    standalone: false
})
export class NetPayDialogComponent {
    netPayValue: number | null = null;
    isSaving = false;

    constructor(
        public dialogRef: MatDialogRef<NetPayDialogComponent>,
        @Inject(MAT_DIALOG_DATA) public data: { dailyId: number, employeeId: string, employeeName: string, netPay: number | null },
        private dailyService: DailyService,
        private toaster: ToasterService
    ) {
        this.netPayValue = data.netPay;
    }

    onSave(): void {
        this.isSaving = true;
        this.dailyService.updateBeneficiaryNetPay(this.data.dailyId, {
            employeeId: this.data.employeeId,
            netPay: this.netPayValue
        }).subscribe({
            next: (res) => {
                this.isSaving = false;
                this.dialogRef.close({ saved: true, netPay: this.netPayValue });
            },
            error: (err) => {
                this.isSaving = false;
                this.toaster.openErrorToaster('حدث خطأ أثناء حفظ الصافي');
            }
        });
    }

    onCancel(): void {
        this.dialogRef.close();
    }
}

import { Component, OnInit, ViewChild, OnDestroy, inject, ChangeDetectorRef } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription, Subject } from 'rxjs';
import { debounceTime } from 'rxjs/operators';
import { MatTableDataSource } from '@angular/material/table';
import { MatSort } from '@angular/material/sort';
import { DailyService } from '../../../shared/service/daily.service';
import { FormDetailsService } from '../../../shared/service/form-details.service';
import { ToasterService } from '../../../shared/components/toaster/toaster.service';
import { trigger, state, style, transition, animate } from '@angular/animations';
import { MatDialog } from '@angular/material/dialog';
import { CommentDialogComponent } from './comment-dialog/comment-dialog.component';
import { NetPayDialogComponent } from './netpay-dialog/netpay-dialog.component';
import { environment } from '../../../environment';
import { WatchlistAlertDialogComponent } from './watchlist-alert-dialog/watchlist-alert-dialog.component';


@Component({
    selector: 'app-beneficiaries-summary',
    templateUrl: './beneficiaries-summary.component.html',
    styleUrl: './beneficiaries-summary.component.scss',
    standalone: false,
    animations: [
        trigger('detailExpand', [
            state('collapsed', style({ height: '0px', minHeight: '0' })),
            state('expanded', style({ height: '*' })),
            transition('expanded <=> collapsed', animate('225ms cubic-bezier(0.4, 0.0, 0.2, 1)')),
        ]),
    ]
})
export class BeneficiariesSummaryComponent implements OnInit, OnDestroy {

    dailyService = inject(DailyService);
    formDetailsService = inject(FormDetailsService);
    toaster = inject(ToasterService);
    route = inject(ActivatedRoute);
    router = inject(Router);
    dialog = inject(MatDialog);
    cdr = inject(ChangeDetectorRef);

    dailyId!: number;
    dailyData: any = null;
    isLoading = true;

    dataSource = new MatTableDataSource<any>([]);
    displayedColumns = ['expand', 'action', 'tabCode', 'tegaraCode', 'employeeName', 'department', 'employeeId', 'totalAmount', 'netPay'];
    expandedElement: any = null;

    filterValues: { [key: string]: string } = {};
    currentReviewFilter: string = 'all';

    @ViewChild(MatSort) set matSort(sort: MatSort) {
        if (sort) {
            this.sort = sort;
            if (this.dataSource) {
                this.dataSource.sort = this.sort;
            }
        }
    }
    sort!: MatSort;

    private subscriptions: Subscription[] = [];
    private searchSubject = new Subject<void>();

    ngOnInit(): void {
        this.initDataSource();

        const searchSub = this.searchSubject.pipe(
            debounceTime(300)
        ).subscribe(() => {
            this.applyFilter();
        });
        this.subscriptions.push(searchSub);

        this.route.paramMap.subscribe((params: any) => {
            this.dailyId = Number(params.get('dailyId'));
            if (this.dailyId) {
                this.loadDaily();
                this.loadSummary();
            }
        });
    }

    private initDataSource() {
        // Setup sorting accessor
        this.dataSource.sortingDataAccessor = (item, property) => {
            switch (property) {
                case 'employeeName': return item.employeeName;
                case 'totalAmount': return item.totalAmount;
                case 'action': return item.isFullyReviewed ? (item.reviewMethod === 'Auto' ? 2 : 1) : 0;
                case 'tabCode': return item.tabCode;
                case 'tegaraCode': return item.tegaraCode;
                case 'department': return item.department;
                case 'employeeId': return item.employeeId;
                default: return item[property];
            }
        };

        // Setup filter predicate for search - per-column contains with Arabic normalization
        this.dataSource.filterPredicate = (data: any, filter: string) => {
            let filterObj: { [key: string]: string } = {};
            try {
                filterObj = JSON.parse(filter);
            } catch (e) {
                filterObj = {};
            }

            const matchText = Object.keys(filterObj).every(key => {
                if (key.startsWith('_')) return true; // skip internal keys
                const val = this.normalizeArabic(filterObj[key] || '');
                if (!val) return true; // empty filter = match all
                const dataVal = this.normalizeArabic((data[key] || '').toString());
                return dataVal.includes(val);
            });

            const method = (data.reviewMethod || '').toLowerCase();
            const isAuto = method === 'auto' || (!method && data.isFullyReviewed && data.netPay !== null && data.netPay !== undefined);
            const isManual = method === 'manual' || (!method && data.isFullyReviewed && (data.netPay === null || data.netPay === undefined));

            const matchReview = this.currentReviewFilter === 'all' ||
                (this.currentReviewFilter === 'reviewed' && data.isFullyReviewed) ||
                (this.currentReviewFilter === 'auto' && data.isFullyReviewed && isAuto) ||
                (this.currentReviewFilter === 'manual' && data.isFullyReviewed && isManual) ||
                (this.currentReviewFilter === 'unreviewed' && !data.isFullyReviewed);

            return matchText && matchReview;
        };
    }

    normalizeArabic(text: string): string {
        if (!text) return '';
        return text
            .toString()
            .replace(/[أإآ]/g, 'ا')
            .replace(/ة/g, 'ه')
            .replace(/ى/g, 'ي')
            .replace(/[\u064B-\u065F]/g, '')
            .trim()
            .toLowerCase();
    }

    loadDaily() {
        this.dailyService.getDaily(this.dailyId).subscribe({
            next: (res: any) => {
                this.dailyData = res;
            }
        })
    }

    trackByKey(index: number, item: any): any {
        return item.employeeId || item.tabCode || item.tegaraCode || index;
    }

    loadSummary() {
        this.isLoading = true;
        this.dailyService.getBeneficiariesSummary(this.dailyId).subscribe({
            next: (result: any) => {
                requestAnimationFrame(() => {
                    this.dataSource.data = (result.beneficiaries || []).map((b: any) => {
                        if (!b.reviewMethod && b.isFullyReviewed) {
                            b.reviewMethod = (b.netPay !== null && b.netPay !== undefined) ? 'Auto' : 'Manual';
                        }
                        return b;
                    });
                    if (this.sort) {
                        this.dataSource.sort = this.sort;
                    }
                    this.applyFilter();
                    this.isLoading = false;
                    this.cdr.detectChanges();
                });
            },
            error: (err) => {
                this.isLoading = false;
                this.cdr.detectChanges();
                this.toaster.openErrorToaster('حدث خطأ في تحميل ملخص المستحقين');
            }
        });
    }

    onSearchInput(param: string, event: Event) {
        const value = (event.target as HTMLInputElement)?.value || '';
        this.filterValues[param] = value.trim();
        this.searchSubject.next();
    }

    applyFilter() {
        if (!this.dataSource) return;
        // Include review filter in JSON so changing review toggle always triggers re-filtering
        const filterData = { ...this.filterValues, _review: this.currentReviewFilter };
        this.dataSource.filter = JSON.stringify(filterData);
    }

    clear(input: string, inputElement?: HTMLInputElement) {
        if (inputElement) {
            inputElement.value = '';
        }
        this.filterValues[input] = '';
        this.applyFilter();
    }

    toggleRow(element: any) {
        this.expandedElement = this.expandedElement === element ? null : element;
    }

    openCommentDialog(element: any, event: Event) {
        event.stopPropagation();
        const dialogRef = this.dialog.open(CommentDialogComponent, {
            width: '600px',
            data: {
                dailyId: this.dailyId,
                employeeId: element.employeeId,
                employeeName: element.employeeName,
                comment: element.comment
            }
        });

        dialogRef.afterClosed().subscribe(result => {
            if (result && result.saved) {
                element.comment = result.comment;
                // Update specific details as well so that opening the details matches
                if (element.details) {
                    element.details.forEach((d: any) => d.summaryComment = result.comment);
                }
                this.toaster.openSuccessToaster('تم حفظ التعليق بنجاح');
            }
        });
    }

    openNetPayDialog(element: any, event: Event) {
        event.stopPropagation();
        const dialogRef = this.dialog.open(NetPayDialogComponent, {
            width: '400px',
            data: {
                dailyId: this.dailyId,
                employeeId: element.employeeId,
                employeeName: element.employeeName,
                netPay: element.netPay
            }
        });

        dialogRef.afterClosed().subscribe(result => {
            if (result && result.saved) {
                element.netPay = result.netPay;
                this.toaster.openSuccessToaster('تم حفظ الصافي بنجاح');
            }
        });
    }

    navigateToForm(formId: number) {
        if (formId && this.dailyId) {
            // Navigate to form details taking into account current routing hierarchy
            // Example: /daily/1/form/5
            this.router.navigate(['/daily', this.dailyId, 'form', formId]);
        }
    }

    navigateToEmployee(employeeId: string) {
        if (employeeId) {
            this.router.navigate(['/employee/details', employeeId]);
        }
    }

    applyReviewToggle(value: string) {
        this.currentReviewFilter = value;
        this.applyFilter();
    }

    markAllAsReviewed(element: any, isChecked: boolean) {
        if (!isChecked) {
            if (!confirm(`هل أنت متأكد من إلغاء مراجعة المستحق: ${element.employeeName}؟`)) {
                return;
            }
        }
        if (isChecked && element.watchListAlert) {
            const dialogRef = this.dialog.open(WatchlistAlertDialogComponent, {
                width: '500px',
                data: {
                    employeeName: element.employeeName,
                    reason: element.watchListAlert.reason
                },
                direction: 'rtl'
            });

            dialogRef.afterClosed().subscribe(result => {
                if (result === true) {
                    this.executeMarkAllAsReviewed(element, isChecked);
                }
            });
        } else {
            this.executeMarkAllAsReviewed(element, isChecked);
        }
    }

    private executeMarkAllAsReviewed(element: any, isChecked: boolean) {
        // Toggle review for all details within this beneficiary
        element.details.forEach((detail: any) => {
            if (detail.isSummaryReviewed !== isChecked) {
                this.executeMarkAsReviewed(detail, isChecked, element);
            }
        });
        element.isFullyReviewed = isChecked;
        element.reviewMethod = isChecked ? 'Manual' : null;
        this.dataSource.data = [...this.dataSource.data];
        this.applyFilter();
        this.cdr.detectChanges();
    }

    markAsReviewed(detail: any, isChecked: boolean) {
        // Find parent to check for watchlist alert
        const parent = this.dataSource.data.find(
            (b: any) => b.details.some((d: any) => d.formDetailId === detail.formDetailId)
        );

        if (isChecked && parent?.watchListAlert) {
            const dialogRef = this.dialog.open(WatchlistAlertDialogComponent, {
                width: '500px',
                data: {
                    employeeName: parent.employeeName,
                    reason: parent.watchListAlert.reason
                },
                direction: 'rtl'
            });

            dialogRef.afterClosed().subscribe(result => {
                if (result === true) {
                    this.executeMarkAsReviewed(detail, isChecked, parent);
                }
            });
        } else {
            this.executeMarkAsReviewed(detail, isChecked, parent);
        }
    }

    private executeMarkAsReviewed(detail: any, isChecked: boolean, parent: any) {
        this.formDetailsService.markAsSummaryReviewed(detail.formDetailId, isChecked).subscribe({
            next: () => {
                detail.isSummaryReviewed = isChecked;
                detail.summaryReviewMethod = isChecked ? 'Manual' : null;
                // Update the parent row's fully reviewed status
                if (parent) {
                    parent.isFullyReviewed = parent.details.every((d: any) => d.isSummaryReviewed);
                    if (parent.isFullyReviewed) {
                        const methods = parent.details.filter((d: any) => d.isSummaryReviewed && d.summaryReviewMethod).map((d: any) => d.summaryReviewMethod);
                        const distinct = [...new Set(methods)];
                        parent.reviewMethod = distinct.length === 1 ? distinct[0] : (distinct.length > 1 ? 'Mixed' : 'Manual');
                    } else if (parent.details.some((d: any) => d.isSummaryReviewed)) {
                        parent.reviewMethod = parent.details.find((d: any) => d.isSummaryReviewed)?.summaryReviewMethod;
                    } else {
                        parent.reviewMethod = null;
                    }
                }
                this.dataSource.data = [...this.dataSource.data];
                this.applyFilter();
                this.cdr.detectChanges();
            },
            error: () => {
                this.toaster.openErrorToaster('حدث خطأ في تحديث حالة المراجعة');
            }
        });
    }

    get totalReviewed(): number {
        return this.dataSource.data.filter((b: any) => b.isFullyReviewed).length;
    }

    get totalAutoReviewed(): number {
        return this.dataSource.data.filter((b: any) => {
            if (!b.isFullyReviewed) return false;
            const m = (b.reviewMethod || '').toLowerCase();
            return m === 'auto' || (!m && b.netPay !== null && b.netPay !== undefined);
        }).length;
    }

    get totalManualReviewed(): number {
        return this.dataSource.data.filter((b: any) => {
            if (!b.isFullyReviewed) return false;
            const m = (b.reviewMethod || '').toLowerCase();
            return m === 'manual' || (!m && (b.netPay === null || b.netPay === undefined));
        }).length;
    }

    get totalAmount(): number {
        return this.dataSource.data.reduce((sum, current) => sum + current.totalAmount, 0);
    }

    get totalNetPay(): number {
        return this.dataSource.data.reduce((sum, current) => sum + (current.netPay || 0), 0);
    }


    ngOnDestroy(): void {
        this.subscriptions.forEach(sub => sub.unsubscribe());
    }

    exportPdf() {
        if (!this.dailyId) return;
        this.dailyService.exportSummaryPdf(this.dailyId).subscribe({
            error: (err) => {
                console.error('Export error:', err);
                this.toaster.openErrorToaster('فشل في تصدير التقرير للطباعة');
            }
        });
    }

    downloadExcel() {
        if (!this.dailyId) return;
        this.dailyService.exportSummaryExcel(this.dailyId).subscribe({
            error: (err) => {
                console.error('Export error:', err);
                this.toaster.openErrorToaster('فشل في تحميل ملف الإكسيل');
            }
        });
    }

    onVerifyPdfSelected(event: any) {
        const file = event.target.files[0];
        if (!file) return;

        this.isLoading = true;
        this.dailyService.verifyPdf(this.dailyId, file).subscribe({
            next: (response: any) => {
                this.toaster.openSuccessToaster('تمت مراجعة الملف بنجاح وتوليد التقرير');

                const data = response.body;

                // Download the report
                if (data.reportFile) {
                    const pdfBytes = this.base64ToArrayBuffer(data.reportFile);
                    let blob = new Blob([pdfBytes], { type: 'application/pdf' });
                    const url = window.URL.createObjectURL(blob);
                    window.open(url);
                }

                // Download text report
                if (data.textReportFile) {
                    const txtBytes = this.base64ToArrayBuffer(data.textReportFile);
                    let txtBlob = new Blob([txtBytes], { type: 'text/plain;charset=utf-8' });
                    const txtUrl = window.URL.createObjectURL(txtBlob);
                    const a = document.createElement('a');
                    a.href = txtUrl;
                    a.download = `VerifyReport_${this.dailyId}.txt`;
                    a.click();
                }

                // Reload data to show updated net pay and reviewed status
                this.loadSummary();

                // Reset file input
                event.target.value = '';
            },
            error: (err) => {
                this.isLoading = false;
                console.error('Verify PDF error:', err);
                if (err.error && err.error.detail) {
                    this.toaster.openErrorToaster(err.error.detail);
                } else {
                    this.toaster.openErrorToaster('حدث خطأ أثناء مراجعة الملف');
                }
                event.target.value = '';
            }
        });
    }

    base64ToArrayBuffer(base64: string): ArrayBuffer {
        const binaryString = window.atob(base64);
        const len = binaryString.length;
        const bytes = new Uint8Array(len);
        for (let i = 0; i < len; i++) {
            bytes[i] = binaryString.charCodeAt(i);
        }
        return bytes.buffer;
    }

    resetReviews() {
        if (!confirm('هل أنت متأكد من إلغاء جميع المراجعات لهذه اليومية؟ لا يمكن التراجع عن هذا الإجراء.')) {
            return;
        }

        this.isLoading = true;
        this.dailyService.resetReviews(this.dailyId).subscribe({
            next: (res: any) => {
                this.toaster.openSuccessToaster('تم إلغاء جميع المراجعات بنجاح');
                this.loadSummary();
            },
            error: (err) => {
                this.isLoading = false;
                this.toaster.openErrorToaster('حدث خطأ أثناء إلغاء المراجعات');
            }
        });
    }
}

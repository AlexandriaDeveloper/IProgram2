import { Component, OnInit, ViewChild, ElementRef, OnDestroy, inject, AfterViewInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { fromEvent, Subscription } from 'rxjs';
import { debounceTime, distinctUntilChanged, map } from 'rxjs/operators';
import { MatTableDataSource } from '@angular/material/table';
import { MatSort } from '@angular/material/sort';
import { DailyService } from '../../../shared/service/daily.service';
import { FormDetailsService } from '../../../shared/service/form-details.service';
import { ToasterService } from '../../../shared/components/toaster/toaster.service';
import { trigger, state, style, transition, animate } from '@angular/animations';
import { MatDialog } from '@angular/material/dialog';
import { CommentDialogComponent } from './comment-dialog/comment-dialog.component';
import { environment } from '../../../environment';

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
export class BeneficiariesSummaryComponent implements OnInit, AfterViewInit, OnDestroy {

    dailyService = inject(DailyService);
    formDetailsService = inject(FormDetailsService);
    toaster = inject(ToasterService);
    route = inject(ActivatedRoute);
    dialog = inject(MatDialog);

    dailyId!: number;
    dailyData: any = null;
    isLoading = true;

    dataSource = new MatTableDataSource<any>([]);
    displayedColumns = ['expand', 'action', 'tabCode', 'tegaraCode', 'employeeName', 'department', 'employeeId', 'totalAmount'];
    expandedElement: any = null;

    filterValues: { [key: string]: string } = {};
    currentReviewFilter: string = 'all';

    @ViewChild(MatSort) sort!: MatSort;
    @ViewChild("tabCodeInput") tabCodeInput!: ElementRef;
    @ViewChild("tegaraCodeInput") tegaraCodeInput!: ElementRef;
    @ViewChild("nameInput") nameInput!: ElementRef;
    @ViewChild("departmentInput") departmentInput!: ElementRef;
    @ViewChild("employeeIdInput") employeeIdInput!: ElementRef;

    private subscriptions: Subscription[] = [];

    ngOnInit(): void {
        this.route.paramMap.subscribe((params: any) => {
            this.dailyId = Number(params.get('dailyId'));
            if (this.dailyId) {
                this.loadDaily();
                this.loadSummary();
            }
        });
    }

    ngAfterViewInit(): void {
        setTimeout(() => {
            this.setupSearch();
            if (this.sort && this.dataSource) {
                this.dataSource.sort = this.sort;
            }
        }, 500);
    }

    loadDaily() {
        this.dailyService.getDaily(this.dailyId).subscribe({
            next: (res: any) => {
                this.dailyData = res;
            }
        })
    }

    loadSummary() {
        this.isLoading = true;
        this.dailyService.getBeneficiariesSummary(this.dailyId).subscribe({
            next: (result: any) => {
                this.dataSource.data = result.beneficiaries;
                // set up sorting
                this.dataSource.sortingDataAccessor = (item, property) => {
                    switch (property) {
                        case 'employeeName': return item.employeeName;
                        case 'totalAmount': return item.totalAmount;
                        case 'action': return item.isFullyReviewed ? 1 : 0;
                        case 'tabCode': return item.tabCode;
                        case 'tegaraCode': return item.tegaraCode;
                        case 'department': return item.department;
                        case 'employeeId': return item.employeeId;
                        default: return item[property];
                    }
                };
                setTimeout(() => {
                    this.dataSource.sort = this.sort;
                });

                // Setup filter predicate for search - per-column contains
                this.dataSource.filterPredicate = (data: any, filter: string) => {
                    let filterObj: { [key: string]: string } = {};
                    try {
                        filterObj = JSON.parse(filter);
                    } catch (e) {
                        filterObj = {};
                    }

                    // Per-column contains matching (skip _review, it's for forcing re-trigger)
                    const matchText = Object.keys(filterObj).every(key => {
                        if (key.startsWith('_')) return true; // skip internal keys
                        const val = (filterObj[key] || '').trim().toLowerCase();
                        if (!val) return true; // empty filter = match all
                        const dataVal = (data[key] || '').toString().toLowerCase();
                        return dataVal.includes(val);
                    });

                    const matchReview = this.currentReviewFilter === 'all' ||
                        (this.currentReviewFilter === 'reviewed' && data.isFullyReviewed) ||
                        (this.currentReviewFilter === 'unreviewed' && !data.isFullyReviewed);

                    return matchText && matchReview;
                };

                // Trigger initial filter explicitly so review filter applies
                this.applyFilter();
                this.isLoading = false;
            },
            error: (err) => {
                this.isLoading = false;
                this.toaster.openErrorToaster('حدث خطأ في تحميل ملخص المستحقين');
            }
        });
    }

    setupSearch() {
        this.initElement(this.tabCodeInput, 'tabCode');
        this.initElement(this.tegaraCodeInput, 'tegaraCode');
        this.initElement(this.nameInput, 'employeeName');
        this.initElement(this.employeeIdInput, 'employeeId');
        this.initElement(this.departmentInput, 'department');
    }

    initElement(element: ElementRef, param: string): Subscription {
        if (!element || !element.nativeElement) return new Subscription();
        const sub = fromEvent(element.nativeElement, 'keyup').pipe(
            debounceTime(600),
            distinctUntilChanged(),
            map((event: any) => {
                const value = (event.target.value || '').toString().trim();
                this.filterValues[param] = value;
                this.applyFilter();
                return event.target.value;
            })
        ).subscribe();
        this.subscriptions.push(sub);
        return sub;
    }

    applyFilter() {
        if (!this.dataSource) return;
        // Include review filter in JSON so changing review toggle always triggers re-filtering
        const filterData = { ...this.filterValues, _review: this.currentReviewFilter };
        this.dataSource.filter = JSON.stringify(filterData);
    }

    clear(input: string) {
        if (input == 'tabCode' && this.tabCodeInput) this.tabCodeInput.nativeElement.value = '';
        if (input == 'tegaraCode' && this.tegaraCodeInput) this.tegaraCodeInput.nativeElement.value = '';
        if (input == 'employeeName' && this.nameInput) this.nameInput.nativeElement.value = '';
        if (input == 'department' && this.departmentInput) this.departmentInput.nativeElement.value = '';
        if (input == 'employeeId' && this.employeeIdInput) this.employeeIdInput.nativeElement.value = '';

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

    applyReviewToggle(value: string) {
        this.currentReviewFilter = value;
        this.applyFilter();
    }

    markAllAsReviewed(element: any, isChecked: boolean) {
        // Toggle review for all details within this beneficiary
        element.details.forEach((detail: any) => {
            if (detail.isSummaryReviewed !== isChecked) {
                this.markAsReviewed(detail, isChecked);
            }
        });
    }

    markAsReviewed(detail: any, isChecked: boolean) {
        this.formDetailsService.markAsSummaryReviewed(detail.formDetailId, isChecked).subscribe({
            next: () => {
                detail.isSummaryReviewed = isChecked;
                // Update the parent row's fully reviewed status
                const parent = this.dataSource.data.find(
                    (b: any) => b.details.some((d: any) => d.formDetailId === detail.formDetailId)
                );
                if (parent) {
                    parent.isFullyReviewed = parent.details.every((d: any) => d.isSummaryReviewed);
                }
                this.applyFilter();
            },
            error: () => {
                this.toaster.openErrorToaster('حدث خطأ في تحديث حالة المراجعة');
            }
        });
    }

    get totalReviewed(): number {
        return this.dataSource.data.filter((b: any) => b.isFullyReviewed).length;
    }

    get totalAmount(): number {
        return this.dataSource.data.reduce((sum, current) => sum + current.totalAmount, 0);
    }

    trackByKey(index: number, item: any): string {
        return item.employeeId;
    }

    ngOnDestroy() {
        this.subscriptions.forEach(s => s.unsubscribe());
    }
}

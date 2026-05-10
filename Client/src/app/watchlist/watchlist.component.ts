import { Component, inject, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatTableDataSource, MatTableModule } from '@angular/material/table';
import { MatPaginator, MatPaginatorModule } from '@angular/material/paginator';
import { MatSort, MatSortModule } from '@angular/material/sort';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { WatchlistService } from '../shared/service/watchlist.service';
import { IWatchList, WatchListParam } from '../shared/models/IWatchList';
import { AddWatchlistDialogComponent } from './add-watchlist-dialog/add-watchlist-dialog.component';
import { FormsModule } from '@angular/forms';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterModule } from '@angular/router';

@Component({
  selector: 'app-watchlist',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatPaginatorModule,
    MatSortModule,
    MatButtonModule,
    MatIconModule,
    MatInputModule,
    MatDialogModule,
    MatSnackBarModule,
    FormsModule,
    MatTooltipModule,
    RouterModule
  ],
  templateUrl: './watchlist.component.html',
  styleUrls: ['./watchlist.component.scss']
})
export class WatchlistComponent implements OnInit {
  displayedColumns: string[] = ['action', 'employeeId', 'tabCode', 'tegaraCode', 'employeeName', 'reason', 'expiresAt', 'createdAt'];
  dataSource: MatTableDataSource<IWatchList>;
  param = new WatchListParam();
  totalCount = 0;
  isLoading = true;

  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;

  watchlistService = inject(WatchlistService);
  dialog = inject(MatDialog);
  snackBar = inject(MatSnackBar);

  constructor() {
    this.dataSource = new MatTableDataSource<IWatchList>();
  }

  ngOnInit(): void {
    this.loadData();
  }

  loadData() {
    this.isLoading = true;
    this.watchlistService.getWatchList(this.param).subscribe({
      next: (response) => {
        this.dataSource.data = response.data;
        this.totalCount = response.count;
        this.isLoading = false;
      },
      error: (err) => {
        console.error(err);
        this.snackBar.open('حدث خطأ في تحميل البيانات', 'إغلاق', { duration: 3000 });
        this.isLoading = false;
      }
    });
  }

  onPageChange(event: any) {
    this.param.pageIndex = event.pageIndex + 1;
    this.param.pageSize = event.pageSize;
    this.loadData();
  }

  applySearch(event: Event) {
    const filterValue = (event.target as HTMLInputElement).value;
    this.param.search = filterValue.trim();
    this.param.pageIndex = 1;
    this.loadData();
  }

  applySpecificSearch(event: Event, field: 'nationalId' | 'tabCode' | 'tegaraCode') {
    const filterValue = (event.target as HTMLInputElement).value;
    if (field === 'nationalId') {
      this.param.nationalId = filterValue.trim();
    } else if (field === 'tabCode') {
      this.param.tabCode = filterValue ? parseInt(filterValue) : undefined;
    } else if (field === 'tegaraCode') {
      this.param.tegaraCode = filterValue ? parseInt(filterValue) : undefined;
    }
    this.param.pageIndex = 1;
    this.loadData();
  }

  clear(field: 'search' | 'nationalId' | 'tabCode' | 'tegaraCode') {
    if (field === 'search') this.param.search = '';
    if (field === 'nationalId') this.param.nationalId = '';
    if (field === 'tabCode') this.param.tabCode = undefined;
    if (field === 'tegaraCode') this.param.tegaraCode = undefined;
    
    this.param.pageIndex = 1;
    this.loadData();
  }

  openAddDialog() {
    const dialogRef = this.dialog.open(AddWatchlistDialogComponent, {
      width: '550px',
      direction: 'rtl',
      data: {}
    });

    dialogRef.afterClosed().subscribe(result => {
      if (result) {
        this.loadData();
      }
    });
  }

  deleteEntry(id: number) {
    if (confirm('هل أنت متأكد من حذف هذا الموظف من قائمة الانتباه؟')) {
      this.watchlistService.removeFromWatchList(id).subscribe({
        next: () => {
          this.snackBar.open('تم الحذف بنجاح', 'إغلاق', { duration: 3000 });
          this.loadData();
        },
        error: (err) => {
          console.error(err);
          this.snackBar.open('حدث خطأ أثناء الحذف', 'إغلاق', { duration: 3000 });
        }
      });
    }
  }
}

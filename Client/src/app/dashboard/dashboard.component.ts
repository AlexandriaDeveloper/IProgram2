import { CommonModule } from '@angular/common';
import { HttpClientModule } from '@angular/common/http';
import { Component, OnInit } from '@angular/core';
import { RouterModule } from '@angular/router';
import { trigger, style, animate, transition, query, stagger } from '@angular/animations';
import { MatCardModule } from '@angular/material/card';
import { MatTableModule } from '@angular/material/table';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatNativeDateModule } from '@angular/material/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { BaseChartDirective } from 'ng2-charts';
import { ChartOptions, ChartType } from 'chart.js';
import { DashboardDto, DashboardService, EmployeeSummaryDto, PieChartDto } from './dashboard.service';

@Component({
    selector: 'app-dashboard',
    standalone: true,
    imports: [
        RouterModule,
        CommonModule,
        HttpClientModule,
        MatCardModule,
        MatTableModule,
        MatProgressSpinnerModule,
        MatDatepickerModule,
        MatNativeDateModule,
        MatFormFieldModule,
        MatInputModule,
        MatButtonModule,
        MatIconModule,
        FormsModule,
        ReactiveFormsModule,
        BaseChartDirective
    ],
    templateUrl: './dashboard.component.html',
    styleUrls: ['./dashboard.component.scss'],
    animations: [
        trigger('fadeInStagger', [
            transition(':enter', [
                query('.col-xl-3, .col-lg-8, .col-lg-4, .col-lg-7, .col-lg-5', [
                    style({ opacity: 0, transform: 'translateY(20px)' }),
                    stagger('100ms', [
                        animate('500ms ease-out', style({ opacity: 1, transform: 'translateY(0)' }))
                    ])
                ], { optional: true })
            ])
        ])
    ]
})
export class DashboardComponent implements OnInit {
    dashboardData: DashboardDto | null = null;
    isLoading = false;

    // Date Filter
    range = new FormGroup({
        start: new FormControl<Date | null>(null),
        end: new FormControl<Date | null>(null),
    });

    // Bar Chart (Monthly Stats)
    public barChartOptions: ChartOptions = {
        responsive: true,
        plugins: {
            legend: { display: true },
        }
    };
    public barChartLabels: string[] = [];
    public barChartType: ChartType = 'bar';
    public barChartData: any[] = [
        { data: [], label: 'عدد الصرفيات', backgroundColor: '#4caf50' },
        { data: [], label: 'الموظفين المستفيدين', backgroundColor: '#ff9800' }
    ];

    // Pie Chart (Departments)
    public pieChartOptions: ChartOptions = {
        responsive: true,
        plugins: {
            legend: { position: 'right' },
        }
    };
    public pieChartLabels: string[] = [];
    public pieChartData: any[] = [{ data: [] }];
    public pieChartType: ChartType = 'pie';

    displayedColumns: string[] = ['description', 'date', 'employeeCount', 'totalAmount'];
    topEmpColumns: string[] = ['name', 'department', 'formCount', 'totalAmount'];

    constructor(private dashboardService: DashboardService) { }

    ngOnInit(): void {
        // Set default date range (Current Month)
        const date = new Date();
        const firstDay = new Date(date.getFullYear(), date.getMonth(), 1);
        const lastDay = new Date(date.getFullYear(), date.getMonth() + 1, 0);
        this.range.setValue({ start: firstDay, end: lastDay });

        this.loadDashboardData();
    }

    loadDashboardData() {
        this.isLoading = true;
        const start = this.range.value.start ? this.range.value.start.toISOString() : undefined;
        const end = this.range.value.end ? this.range.value.end.toISOString() : undefined;

        this.dashboardService.getDashboardStats(start, end).subscribe({
            next: (data) => {
                this.dashboardData = data;
                this.setupCharts(data);
                this.isLoading = false;
            },
            error: (err) => {
                console.error('Error loading dashboard data:', err);
                this.isLoading = false;
            }
        });
    }

    setupCharts(data: DashboardDto) {
        // Bar Chart Setup
        if (data.chartData) {
            this.barChartLabels = data.chartData.map(d => d.label);
            this.barChartData[0].data = data.chartData.map(d => d.formCount);
            this.barChartData[1].data = data.chartData.map(d => d.employeeCount);
        }

        // Pie Chart Setup
        if (data.formsByDepartment) {
            this.pieChartLabels = data.formsByDepartment.map(d => d.label);
            this.pieChartData[0].data = data.formsByDepartment.map(d => d.value);
            // Dynamic Colors could be added here
        }
    }
}

import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../environment';

export interface DashboardDto {
    totalEmployees: number;
    totalForms: number;
    activeForms: number;
    totalAmount: number;

    // Trends
    totalAmountChange: number;
    employeeCountChange: number;
    formCountChange: number;

    recentForms: FormSummaryDto[];
    chartData: ChartDataDto[];
    topEmployees: EmployeeSummaryDto[];
    formsByDepartment: PieChartDto[];
}

export interface EmployeeSummaryDto {
    id: string; // Changed to string based on backend update
    name: string;
    department: string;
    formCount: number;
    totalAmount: number;
}

export interface PieChartDto {
    label: string;
    value: number;
}

export interface FormSummaryDto {
    id: number;
    description: string;
    date: Date;
    employeeCount: number;
    totalAmount: number;
}

export interface ChartDataDto {
    label: string;
    formCount: number;
    employeeCount: number;
    totalAmount: number;
}

@Injectable({
    providedIn: 'root'
})
export class DashboardService {
    private baseUrl = environment.apiUrl;

    constructor(private http: HttpClient) { }

    getDashboardStats(startDate?: string, endDate?: string): Observable<DashboardDto> {
        let params: any = {};
        if (startDate) params.startDate = startDate;
        if (endDate) params.endDate = endDate;
        return this.http.get<DashboardDto>(this.baseUrl + 'dashboard', { params: params });
    }
}

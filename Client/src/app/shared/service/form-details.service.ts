import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { catchError, map, Observable, of } from 'rxjs';

// Define interfaces for type safety
export interface IFormDetailsResponse {
  id: number;
  name: string;
  description?: string;
  hasReferences: boolean;
  formDetails: IFormDetail[];
}

export interface IFormDetail {
  id: number | string;
  formId: number;
  name: string;
  tabCode?: string | number;
  tegaraCode?: string | number;
  amount: number;
  employeeId: number | string;
  department?: string;
  isReviewed: boolean;
  reviewComments?: string;
  collage?: string; // Added to satisfy IEmployee interface compatibility
}

export interface IFormDetailsRequest {
  amount: number;
  employeeId: number;
  formId: number;
  reviewComments?: string;
}

export interface IReorderRequest {
  id: number;
  rows: number[];
}

export interface IApiResponse<T = any> {
  success: boolean;
  data?: T;
  message?: string;
  error?: string;
}

@Injectable({
  providedIn: 'root'
})
export class FormDetailsService {

  apiUrl = environment.apiUrl;
  http = inject(HttpClient);
  constructor() { }


  GetFormDetails(id: number): Observable<IFormDetailsResponse> {
    return this.http.get<IFormDetailsResponse>(this.apiUrl + 'form/formDetails/' + id);
  }

  addEmployeeToFormDetails(model: IFormDetailsRequest): Observable<IApiResponse> {
    return this.http.post<IApiResponse>(this.apiUrl + 'formDetails/addEmployeeToFormDetails/', model);
  }

  editEmployeeToFormDetails(model: IFormDetailsRequest): Observable<IApiResponse> {
    return this.http.put<IApiResponse>(this.apiUrl + 'formDetails/editEmployeeToFormDetails/', model);
  }

  exportForms(formId: number): Observable<void> {
    return this.http.get(this.apiUrl + `reportPdf/PrintFormWithDetailsPdf/${formId}`, {
      observe: 'response',
      responseType: 'blob'
    }).pipe(
      map((response: HttpResponse<Blob>) => {
        if (response.body) {
          const blob = new Blob([response.body], { type: 'application/pdf' });
          const url = window.URL.createObjectURL(blob);
          window.open(url);
        }
      }),
      catchError(error => {
        console.error('Error exporting PDF:', error);
        throw error; // Re-throw to let component handle
      })
    );
  }

  deleteFormDetails(rowId: number): Observable<IApiResponse> {
    return this.http.delete<IApiResponse>(this.apiUrl + 'formDetails/' + rowId);
  }

  reOrderRows(id: number, model: number[]): Observable<IApiResponse> {
    return this.http.put<IApiResponse>(this.apiUrl + 'formDetails/reOrderRows/' + id, model);
  }

  markAsReviewed(id: number, isChecked: boolean): Observable<IApiResponse> {
    return this.http.put<IApiResponse>(this.apiUrl + 'formDetails/markAsReviewed/' + id, isChecked);
  }

}

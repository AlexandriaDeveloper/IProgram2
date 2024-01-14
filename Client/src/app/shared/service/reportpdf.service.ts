import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { EmployeeReportRequest } from '../models/IEmployee';
import { catchError, map, of } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class ReportpdfService {
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);
  constructor() { }
  formPdfReport(model){
    return this.http.get(this.apiUrl+'reportPdf/PrintFormPdf',model)
  }
  employeePdfReport(model :EmployeeReportRequest){
    var params = new HttpParams();
    if(model.startDate){
      params = params.append('startDate',model.startDate);
    }
    if(model.endDate){
      params = params.append('endDate',model.endDate);
    }
    if(model.id){
      params = params.append('id',model.id);
    }


    return this.http.get(this.apiUrl+`reportPdf/PrintEmployeeReportDetailsPdf/`,{ observe: 'response', responseType: 'blob' ,params}).pipe(
      map((x: HttpResponse<any>) => {
        let blob = new Blob([x.body], {
          type: 'application/pdf'
         });
        const url = window.URL.createObjectURL(blob);
        window.open(url);

      }),
      catchError(()=>of(null)));
     }

   // return this.http.get(this.apiUrl+'reportPdf/PrintEmployeeReportDetailsPdf',{params})
  }




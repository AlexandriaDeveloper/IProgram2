import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';

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
}



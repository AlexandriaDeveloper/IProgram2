import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { DailyParam, IDaily } from '../models/IDaily';
import { FormParam, IForm } from "../models/IForm";
import { map, catchError, of } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class FormService {
  restoreForm(id: number) {
    return this.http.put(this.apiUrl + 'form/restore-form/' + id, {});
  }

  apiUrl = environment.apiUrl;
  http = inject(HttpClient);
  constructor() { }
  GetForms(id, param: FormParam) {
    // console.log(param);

    let params = new HttpParams();
    param.pageSize !== null ? params = params.append('pageSize', param.pageSize) : params = params.append('pageSize', 30);
    param.pageIndex !== null ? params = params.append('pageIndex', param.pageIndex) : params = params.append('pageIndex', 0)

    if (param.sortBy) params = params.append('sortBy', param.sortBy);
    if (param.direction) params = params.append('direction', param.direction);


    if (param.name) params = params.append('name', param.name);
    if (param.index) params = params.append('index', param.index);

    if (param.createdBy) params = params.append('createdBy', param.createdBy);

    if (param.dailyId) params = params.append('dailyId', param.dailyId);


    return this.http.get<IForm[]>(this.apiUrl + 'form/' + id, { params: params })
  }


  GetFormByDetails(id) {

    return this.http.get<IForm[]>(this.apiUrl + 'form/formDetails/' + id)
  }

  addForm(model: IForm) {
    return this.http.post(this.apiUrl + 'form', model);
  }
  CopyFormToArchive(id) {

    return this.http.get(this.apiUrl + 'form/copyFormToArchive/' + id)
  }
  editForm(model: IForm) {
    return this.http.put(this.apiUrl + 'form/' + model.id, model);
  }
  moveFormDetailsDailyArchive(model) {
    return this.http.put(this.apiUrl + 'form/MoveFormDailyArchives/', model)
  }
  updateDescription(id, val) {
    return this.http.put<any>(this.apiUrl + 'form/updateDescription/' + id, val)
  }

  deleteForm(id) {
    return this.http.delete(this.apiUrl + 'form/' + id);
  }

  exportFormsInsidDaily(dailyId) {
    //exportIndexPdf
    return this.http.get(this.apiUrl + `daily/exportPdf/${dailyId}`, { observe: 'response', responseType: 'blob' }).pipe(
      map((x: HttpResponse<any>) => {
        let blob = new Blob([x.body], {
          type: 'application/pdf'
        });
        const url = window.URL.createObjectURL(blob);
        window.open(url);

      }),
      catchError(() => of(null)));
  }
  //exportIndexPdf
  exportDailyIndex(dailyId) {
    //exportIndexPdf
    return this.http.get(this.apiUrl + `daily/exportIndexPdf/${dailyId}`, { observe: 'response', responseType: 'blob' }).pipe(
      map((x: HttpResponse<any>) => {
        let blob = new Blob([x.body], {
          type: 'application/pdf'
        });
        const url = window.URL.createObjectURL(blob);
        window.open(url);

      }),
      catchError(() => of(null)));
  }

  downloadExcelForm(form) {
    return this.http.post(this.apiUrl + 'form/download-form', { ...form }, { observe: 'response', responseType: 'blob' }).pipe(
      map((x: HttpResponse<any>) => {
        console.log(form);

        let blob = new Blob([x.body], {
          type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
        });
        //download file with filename instead of random name
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = form.formTitle + '.xlsx';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
      })
    );
  }
  uploadEmployeesExcelFile(file) {
    // console.log(file);

    const formData = new FormData();

    formData.append("file", file.file as Blob, file.file.name);
    formData.append("formId", file.formId);

    return this.http.post(this.apiUrl + 'form/upload-excel-form', formData, {
      // responseType: "blob",
      // reportProgress: true,
      observe: "events"
    })
  }
  HideForm(id) {
    return this.http.put(this.apiUrl + 'form/hide-form/' + id, {});
  }

}

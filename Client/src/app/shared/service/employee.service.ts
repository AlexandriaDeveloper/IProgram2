import { EmployeeDownloadParam, EmployeeParam, EmployeeReportRequest } from '../models/IEmployee';
import { HttpClient, HttpEventType, HttpHeaders, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { IEmployee, IUploadEmployee } from '../models/IEmployee';
import { catchError, map, of, tap } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class EmployeeService {


  apiUrl = environment.apiUrl;
  http = inject(HttpClient);
  constructor() { }



  addEmployee(model: IEmployee) {
    return this.http.post(this.apiUrl + 'employee/add', model)
  }

  updateEmployee(model: IEmployee) {
    return this.http.put(this.apiUrl + 'employee', model)
  }

  uploadEmployeeFile(file) {
    // console.log(file);
    const formData = new FormData();
    formData.append("file", file as Blob, file.name);
    return this.http.post(this.apiUrl + 'employee/upload', formData
      , {
        // responseType: "blob",
        // reportProgress: true,
        observe: "events"
      }

    )

  }
  //UploadTegaraFile
  uploadEmployeeTegaraFile(file) {
    // console.log(file);
    const formData = new FormData();
    formData.append("file", file as Blob, file.name);
    return this.http.post(this.apiUrl + 'employee/uploadTegaraFile', formData
      , {
        // responseType: "blob",
        // reportProgress: true,
        observe: "events"
      }

    )

  }

  GetEmployees(param: EmployeeParam) {
    let params = new HttpParams();
    param.pageSize !== null ? params = params.append('pageSize', param.pageSize) : params = params.append('pageSize', 30);
    param.pageIndex !== null ? params = params.append('pageIndex', param.pageIndex) : params = params.append('pageIndex', 0)

    if (param.sortBy) params = params.append('sortBy', param.sortBy);
    if (param.direction) params = params.append('direction', param.direction);

    if (param.departmentId) params = params.append('departmentId', param.departmentId);
    if (param.tegaraCode) params = params.append('tegaraCode', param.tegaraCode);
    if (param.collage) params = params.append('collage', param.collage);
    if (param.name) params = params.append('name', param.name);
    if (param.id) params = params.append('id', param.id);
    if (param.departmentName) params = params.append('departmentName', param.departmentName);
    if (param.tabCode) params = params.append('tabCode', param.tabCode);


    return this.http.get<IEmployee[]>(this.apiUrl + 'employee/getEmployees', { params: params })
  }


  GetEmployee(param: EmployeeParam) {
    let params = new HttpParams();
    // param.pageSize!==null? params= params.append('pageSize',param.pageSize):params = params.append('pageSize',30);
    // param.pageIndex!==null?params= params.append('pageIndex',param.pageIndex):params = params.append('pageIndex',0)

    // if(param.sortBy) params = params.append('sortBy',param.sortBy);
    // if(param.direction) params = params.append('direction',param.direction);
    console.log(param.id);

    if (param.id) params = params.append('id', param.id);

    if (param.tegaraCode) params = params.append('tegaraCode', param.tegaraCode);
    // if(param.nationalId) params = params.append('nationalId',param.nationalId);
    if (param.tabCode) params = params.append('tabCode', param.tabCode);
    if (param.collage) params = params.append('collage', param.collage);
    if (param.departmentName) params = params.append('departmentName', param.departmentName);
    if (param.name) params = params.append('name', param.name);

    return this.http.get<IEmployee>(this.apiUrl + 'employee/getEmployee', { params: params })
  }

  employeeReport(model: EmployeeReportRequest) {



    return this.http.post(this.apiUrl + `employee/EmployeeReport/`, model)
    // return this.http.get(this.apiUrl+'reportPdf/PrintEmployeeReportDetailsPdf',{params})
  }

  downloadEmployeesFile() {
    let params = new HttpParams();
    params = params.append('fileName', "add-employees");
    return this.http.get(this.apiUrl + 'download/downloadFile', { observe: 'response', responseType: 'blob', params }).pipe(
      map((x: HttpResponse<any>) => {
        let blob = new Blob([x.body], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
        const url = window.URL.createObjectURL(blob);
        window.open(url);
      })
    );
  }

  downloadEmployeesFile2(employeeDownloadParam: EmployeeDownloadParam) {
    let params = new HttpParams();
    if (employeeDownloadParam.departmentId) params = params.append('departmentId', employeeDownloadParam.departmentId);
    if (employeeDownloadParam.collage) params = params.append('collage', employeeDownloadParam.collage);
    if (employeeDownloadParam.section) params = params.append('section', employeeDownloadParam.section);
    return this.http.get(this.apiUrl + 'employee/download-employees/', { observe: 'response', responseType: 'blob', params }).pipe(
      map((x: HttpResponse<any>) => {
        let blob = new Blob([x.body], {
          type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
        });
        const url = window.URL.createObjectURL(blob);
        window.open(url);
      }))
  }

  softDelete(id: string) {
    return this.http.delete(this.apiUrl + 'employee/softDelete/' + id)
  }
  Delete(id: string) {
    return this.http.delete(this.apiUrl + 'employee/' + id)
  }

  GetCollages() {
    return this.http.get(this.apiUrl + 'employee/getCollages')
  }
  GetSections() {
    return this.http.get(this.apiUrl + 'employee/getSections')
  }
  GetBanks() {
    return this.http.get(this.apiUrl + 'employeeBank/getBanks')
  }
  GetBankBranches(bankName: string) {
    return this.http.get(this.apiUrl + 'employeeBank/getBankBranches?bankName=' + bankName)
  }
  GetDepartments() {
    return this.http.get(this.apiUrl + 'department/getAllDepartments')
  }
}






import { EmployeeParam } from '../models/IEmployee';
import { HttpClient, HttpEventType, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { IEmployee, IUploadEmployee } from '../models/IEmployee';
import { map, tap } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class EmployeeService {
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);
  constructor() { }



  addEmployee(model: IEmployee){
    return this.http.post(this.apiUrl+'employee/add',model)
  }

  uploadEmployeeFile (file) {
    console.log(file);
    const formData  = new FormData();
    file.file.forEach(x => {
      console.log(x);
      formData.append("files", x as Blob,x.filename);
    });

  return this.http.post(this.apiUrl+'employee/upload',formData
 , {
  responseType: "blob",
  reportProgress: true,
  observe: "events"
}

  )

  }


  GetEmployees(param : EmployeeParam){
    let params = new HttpParams();
    param.pageSize!==null? params= params.append('pageSize',param.pageSize):params = params.append('pageSize',30);
    param.pageIndex!==null?params= params.append('pageIndex',param.pageIndex):params = params.append('pageIndex',0)

    if(param.sortBy) params = params.append('sortBy',param.sortBy);
    if(param.direction) params = params.append('direction',param.direction);

    if(param.tegaraCode) params = params.append('tegaraCode',param.tegaraCode);
    if(param.collage) params = params.append('collage',param.collage);
    if(param.name) params = params.append('name',param.name);
    if(param.nationalId) params = params.append('nationalId',param.nationalId);
    if(param.tabCode) params = params.append('tabCode',param.tabCode);


    return this.http.get<IEmployee[]>(this.apiUrl+'employee/getEmployees',{params:params})
  }


  GetEmployee(param : EmployeeParam){
    let params = new HttpParams();
    // param.pageSize!==null? params= params.append('pageSize',param.pageSize):params = params.append('pageSize',30);
    // param.pageIndex!==null?params= params.append('pageIndex',param.pageIndex):params = params.append('pageIndex',0)

    // if(param.sortBy) params = params.append('sortBy',param.sortBy);
    // if(param.direction) params = params.append('direction',param.direction);

    if(param.tegaraCode) params = params.append('tegaraCode',param.tegaraCode);
    if(param.nationalId) params = params.append('nationalId',param.nationalId);
    if(param.tabCode) params = params.append('tabCode',param.tabCode);
    if(param.collage) params = params.append('collage',param.collage);
    if(param.name) params = params.append('name',param.name);

    return this.http.get<IEmployee>(this.apiUrl+'employee/getEmployee',{params:params})
  }



}

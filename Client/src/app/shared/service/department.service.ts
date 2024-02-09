import { IDepartment } from './../models/Department';
import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { DepartmentParam } from '../models/Department';
import { map, retry } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class DepartmentService {

  constructor() { }
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);

   getDepartments(param :DepartmentParam){

    let params = new HttpParams();
    param.pageSize!==null? params= params.append('pageSize',param.pageSize):params = params.append('pageSize',30);
    param.pageIndex!==null?params= params.append('pageIndex',param.pageIndex):params = params.append('pageIndex',0)

    if(param.name) params = params.append('name',param.name);

    return this.http.get(this.apiUrl+'department',{params:params})

   }
   getAllDepartments(){

    return this.http.get(this.apiUrl+'department/getAllDepartments')
   }

    addDepartment( department :IDepartment){
      return this.http.post(this.apiUrl+'department',department)
    }

    editDepartment(department :IDepartment){
      return this.http.put(this.apiUrl+'department',department)
    }
    addEmployeesToDepartment(id,employeeIds){
      return this.http.put(this.apiUrl+'department/'+id+'/employees',employeeIds)
    }

    deleteDepartment(id){

      return this.http.delete(this.apiUrl+'department/'+id)
    }
    deleteEmployeeFromDepartment( ids){
      return this.http.put(this.apiUrl+'department/removeEmployees',ids)
    }
//removeEmployeesByDepartment
    deleteEmployeesByDepartmentId( departmentId){
      return this.http.put(this.apiUrl+'department/removeEmployeesByDepartment/'+departmentId,{})
    }
    downloadEmployeeDepartmentFile(fileName){
      let params = new HttpParams();
      params = params.append('fileName',fileName);
      return this.http.get( this.apiUrl+'download/downloadFile', {  observe: 'response', responseType: 'blob',params }).pipe(
        map((x: HttpResponse<any>) => {
          let blob = new Blob([x.body], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
          const url = window.URL.createObjectURL(blob);
          window.open(url);
        })
      );
    }

    uploadEmployeesDepartmentFile (file) {
      // console.log(file);

      const formData  = new FormData();

        formData.append("file", file.file as Blob,file.file.name);
        formData.append("departmentId",file.departmentId);

    return this.http.post(this.apiUrl+'department/upload-employees-department',formData,{
    // responseType: "blob",
    // reportProgress: true,
    observe: "events"
    })
  }


}

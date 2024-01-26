import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';

@Injectable({
  providedIn: 'root'
})
export class EmployeeReferencesService {
  constructor() { }
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);


  getEmployeeReferences(employeeId){
    return this.http.get(this.apiUrl+'employeeRefernces/GetEmployeeRefernces/'+employeeId)
  }
  deleteEmployeeReference(id){
    return this.http.delete(this.apiUrl+'employeeRefernces/DeleteEmployeeReference/'+id)
  }

  UploadRefernceFile(id,file){


      const formData  = new FormData();
      formData.append("file", file as Blob,file.name);
      formData.append("employeeId",id);
      return this.http.post(this.apiUrl+'employeeRefernces/UploadRefernce',formData,{
        responseType: "blob",
        reportProgress: true,
        observe: "events"
      })
    }





}

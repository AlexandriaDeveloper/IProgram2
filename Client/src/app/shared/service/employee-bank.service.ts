import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { EmployeeBank } from '../models/EmployeeBank';

@Injectable({
  providedIn: 'root'
})
export class EmployeeBankService {

  constructor() { }
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);


  addEmployeeBank(model:EmployeeBank){
    return this.http.post(this.apiUrl+'employeeBank',model)

  }
  deleteEmployeeBank(id:string){
    return this.http.delete(this.apiUrl+'employeeBank/'+id)
  }
  getEmployeeBankById(id:string){
    return this.http.get(this.apiUrl+'employeeBank/'+id)
  }
  getEmployeeBankByEmployeeId(id:string){
    return this.http.get(this.apiUrl+'employeeBank/'+id)
  }

}

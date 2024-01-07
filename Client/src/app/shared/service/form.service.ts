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

  apiUrl=environment.apiUrl;
  http =inject(HttpClient);
  constructor() { }
  GetForms(id,param : FormParam){
    console.log(param);

    let params = new HttpParams();
    param.pageSize!==null? params= params.append('pageSize',param.pageSize):params = params.append('pageSize',30);
    param.pageIndex!==null?params= params.append('pageIndex',param.pageIndex):params = params.append('pageIndex',0)

    if(param.sortBy) params = params.append('sortBy',param.sortBy);
    if(param.direction) params = params.append('direction',param.direction);


    if(param.name) params = params.append('name',param.name);

    if(param.dailyId) params = params.append('dailyId',param.dailyId);


    return this.http.get<IForm[]>(this.apiUrl+'form/'+id,{params:params})
  }

  GetFormByDetails(id){

    return this.http.get<IForm[]>(this.apiUrl+'form/formDetails/'+id)
  }

  addForm (model : IForm){
    return this.http.post(this.apiUrl+'form',model);
  }

  editForm (model : IForm){
    return this.http.put(this.apiUrl+'form/'+model.id,model);
  }
  updateDescription(id,val){
    return this.http.put<any>(this.apiUrl+'form/updateDescription/'+id,val)
  }

deleteForm(id){
  return this.http.delete(this.apiUrl+'form/'+id);
}
exportFormsInsidDaily(dailyId){

  return this.http.get(this.apiUrl+`daily/exportPdf/${dailyId}`,{ observe: 'response', responseType: 'blob' }).pipe(
   map((x: HttpResponse<any>) => {
     let blob = new Blob([x.body], {
       type: 'application/pdf'
      });
     const url = window.URL.createObjectURL(blob);
     window.open(url);

   }),
   catchError(()=>of(null)));
  }

}

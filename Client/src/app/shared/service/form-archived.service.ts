import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { FormParam, IForm } from '../models/IForm';
import { FormArchiveParam } from '../models/FormArchiveParam';
import { environment } from '../../environment';

@Injectable({
  providedIn: 'root'
})
export class FormArchivedService {
  http =inject(HttpClient);
  apiUrl=environment.apiUrl;
  constructor() { }
  GetArchivedForms(param : FormArchiveParam){
    // console.log(param);

    let params = new HttpParams();
    param.pageSize!==null? params= params.append('pageSize',param.pageSize):params = params.append('pageSize',30);
    param.pageIndex!==null?params= params.append('pageIndex',param.pageIndex):params = params.append('pageIndex',0)

    if(param.sortBy) params = params.append('sortBy',param.sortBy);
    if(param.direction) params = params.append('direction',param.direction);


    if(param.name) params = params.append('name',param.name);

   // if(param.dailyId) params = params.append('dailyId',param.dailyId);


    return this.http.get<IForm[]>(this.apiUrl+'formArchived/getArchivedForms/',{params:params})
  }

  moveFormArchiveToDaily(model){
    return this.http.put(this.apiUrl+'formArchived/moveFormArchiveToDaily',model)
  }
  deleteMultiForms(model){
    return this.http.post(this.apiUrl+'formArchived/deleteMultiForms',model)
  }
  deleteForm(id){
    return this.http.delete(this.apiUrl+'formArchived/'+id);
  }
}

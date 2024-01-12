import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { IDaily, DailyParam } from '../models/IDaily';

@Injectable({
  providedIn: 'root'
})
export class DailyService {
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);
  constructor() { }
  GetDailies(param : DailyParam){
    console.log(param);

    let params = new HttpParams();
    param.pageSize!==null? params= params.append('pageSize',param.pageSize):params = params.append('pageSize',30);
    param.pageIndex!==null?params= params.append('pageIndex',param.pageIndex):params = params.append('pageIndex',0)

    if(param.sortBy) params = params.append('sortBy',param.sortBy);
    if(param.direction) params = params.append('direction',param.direction);


    if(param.name) params = params.append('name',param.name);
    if(param.startDate) params = params.append('startDate',param.startDate.toString());
    if(param.endDate) params = params.append('endDate',param.endDate.toString());
    if(param.closed) params = params.append('closed',param.closed.toString());

    return this.http.get<IDaily[]>(this.apiUrl+'daily',{params:params})
  }

  addDaily(model : IDaily){

    return this.http.post(this.apiUrl+'daily',model)
  }
  editDaily(model : IDaily){

    return this.http.put(this.apiUrl+'daily',model)
  }
  deleteDaily(id : number){
     return this.http.delete(this.apiUrl+'daily/softdelete/'+id)
  }

}

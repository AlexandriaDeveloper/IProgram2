import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { IDaily, DailyParam } from '../models/IDaily';
import { map } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class DailyService {
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);
  constructor() { }
  GetDailies(param : DailyParam){
    // console.log(param);

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

  getDaily(id){
    return this.http.get<IDaily>(this.apiUrl+'daily/'+id)
  }

  addDaily(model : IDaily){

    return this.http.post(this.apiUrl+'daily',model)
  }
  editDaily(model : IDaily){

    return this.http.put(this.apiUrl+'daily',model)
  }
  softDeleteDaily(id : number){
     return this.http.delete(this.apiUrl+'daily/softdelete/'+id)
  }
  deleteDaily(id : number){
    return this.http.delete(this.apiUrl+'daily/'+id)
 }


  downloadExcelDaily(dailyId){
    return this.http.get(this.apiUrl+'daily/download-daily/'+dailyId,{ observe: 'response', responseType: 'blob' }).pipe(
     map((x: HttpResponse<any>) => {
       let blob = new Blob([x.body], {
         type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
        });
        const url = window.URL.createObjectURL(blob);
        window.open(url);
       }))
  }

  closeDaily(dailyId: any) {
    return this.http.put(this.apiUrl+'daily/CloseDaily/'+dailyId,{})
  }

}

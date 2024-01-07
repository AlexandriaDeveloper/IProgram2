import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { HttpClient, HttpResponse } from '@angular/common/http';
import { catchError, map, of } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class FormDetailsService {
  apiUrl=environment.apiUrl;
  http =inject(HttpClient);
  constructor() { }


  GetFormDetails(id){
    return this.http.get<any>(this.apiUrl+'form/formDetails/'+id)
  }

  addEmployeeToFormDetails(model){
    return this.http.post(this.apiUrl+'formDetails/addEmployeeToFormDetails/',model)
  }
  editEmployeeToFormDetails(model){
    return this.http.put(this.apiUrl+'formDetails/editEmployeeToFormDetails/',model)
  }
  exportForms(formId){

   return this.http.get(this.apiUrl+`reportPdf/PrintFormWithDetailsPdf/${formId}`,{ observe: 'response', responseType: 'blob' }).pipe(
    map((x: HttpResponse<any>) => {
      let blob = new Blob([x.body], {
        type: 'application/pdf'
       });
      const url = window.URL.createObjectURL(blob);
      window.open(url);

    }),
    catchError(()=>of(null)));
   }
   deleteFormDetails(rowId){
     return this.http.delete(this.apiUrl+'formDetails/'+rowId)
   }
   reOrderRows(id,model){
     return this.http.put(this.apiUrl+'formDetails/reOrderRows/'+id,model)
   }
}



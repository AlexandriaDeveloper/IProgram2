import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';

@Injectable({
  providedIn: 'root'
})
export class FormReferencesService {
  apiUrl = environment.apiUrl;
  http = inject(HttpClient);
  constructor() { }
  getFormReferences(formId: number) {
    return this.http.get(this.apiUrl + 'formReferences/GetFormReferences/' + formId)
  }
  deleteFormReference(id) {
    return this.http.delete(this.apiUrl + 'formReferences/DeleteFormReference/' + id)
  }

  UploadRefernceFile(id, file) {
    const formData = new FormData();
    formData.append("File", file as Blob, file.name);
    formData.append("FormId", id);
    console.log('FormReferencesService: Uploading file...', { id, fileName: file.name });
    return this.http.post(this.apiUrl + 'formReferences/UploadFormRefernce', formData, {
      responseType: "blob",
      reportProgress: true,
      observe: "events"
    })
  }


}

import { Injectable } from '@angular/core';
import { HttpClient, HttpEvent } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environment';

@Injectable({
  providedIn: 'root'
})
export class DailyReferencesService {
  private apiUrl = environment.apiUrl;

  constructor(private http: HttpClient) { }

  upload(dailyId: number, description: string, file: File): Observable<HttpEvent<any>> {
    const formData = new FormData();
    formData.append('DailyId', dailyId.toString());
    formData.append('Description', description);
    formData.append('File', file, file.name);

    return this.http.post(this.apiUrl + 'dailyReferences/uploadDailyReference', formData, {
      reportProgress: true,
      observe: 'events'
    });
  }

  deleteDailyReference(id: number): Observable<any> {
    return this.http.delete(this.apiUrl + 'dailyReferences/' + id);
  }
}

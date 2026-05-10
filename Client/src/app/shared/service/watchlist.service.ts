import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environment';
import { IAddToWatchListRequest, IWatchList, WatchListParam, IPagination } from '../models/IWatchList';

@Injectable({
  providedIn: 'root'
})
export class WatchlistService {
  apiUrl = environment.apiUrl;
  http = inject(HttpClient);

  constructor() { }

  getWatchList(param: WatchListParam) {
    let params = new HttpParams();
    if (param.pageIndex) params = params.append('pageIndex', param.pageIndex);
    if (param.pageSize) params = params.append('pageSize', param.pageSize);
    if (param.search) params = params.append('search', param.search);
    if (param.nationalId) params = params.append('nationalId', param.nationalId);
    if (param.tabCode) params = params.append('tabCode', param.tabCode);
    if (param.tegaraCode) params = params.append('tegaraCode', param.tegaraCode);

    return this.http.get<IPagination<IWatchList>>(this.apiUrl + 'watchlist', { params });
  }

  addToWatchList(model: IAddToWatchListRequest) {
    return this.http.post<IWatchList>(this.apiUrl + 'watchlist', model);
  }

  removeFromWatchList(id: number) {
    return this.http.delete(this.apiUrl + 'watchlist/' + id);
  }
}

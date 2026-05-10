export interface IWatchList {
  id: number;
  employeeId: string;
  employeeName: string;
  reason: string;
  expiresAt?: string;
  createdBy: string;
  createdAt: string;
  isActive: boolean;
  tabCode?: number;
  tegaraCode?: number;
}

export interface IWatchListAlert {
  reason: string;
}

export interface IAddToWatchListRequest {
  nationalId?: string;
  tabCode?: number;
  tegaraCode?: number;
  searchType: 'nationalId' | 'tabCode' | 'tegaraCode';
  reason: string;
  expiresAt?: string;
}

export class WatchListParam {
  pageIndex = 1;
  pageSize = 10;
  search?: string;
  nationalId?: string;
  tabCode?: number;
  tegaraCode?: number;
}

export interface IPagination<T> {
  pageIndex: number;
  pageSize: number;
  count: number;
  data: T[];
}

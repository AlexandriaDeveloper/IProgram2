import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { environment } from '../../environment';
import { IRole } from '../models/roles';

@Injectable({
  providedIn: 'root'
})
export class RoleService {
  http =inject(HttpClient);
  router = inject(Router);

  apiUrl=environment.apiUrl;
  constructor() { }
  getRoles(){
    return this.http.get<IRole[]>( this.apiUrl+'role/getRoles');
  }
}

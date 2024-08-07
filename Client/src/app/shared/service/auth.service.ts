import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../environment';
import { Router } from '@angular/router';
import { JwtHelperService } from '@auth0/angular-jwt';
import { IRole } from '../models/roles';
import { map } from 'rxjs';

@Injectable({
  providedIn: 'root',

})
export class AuthService {
  http =inject(HttpClient);
  router = inject(Router);
  jwtHelper : JwtHelperService = new JwtHelperService();
  apiUrl=environment.apiUrl;
  currentUserSig = signal<any | undefined | null>(undefined);
  userRoles =signal<string[]>([]);

  constructor() { }

  login(model) {


     this.http.post(environment.apiUrl+'account/login',model).
    subscribe({
      next:(res:any)=>{
        localStorage.setItem('token',res.token);
        localStorage.setItem('user',JSON.stringify(res));
        this.currentUserSig.set(res);
        this.userRoles.set(this.getUserRoles(res.token));

        this.router.navigateByUrl('/');
      },
      error:(err)=> console.log(err)
    });
  }
  signup(model){
    return this.http.post(environment.apiUrl+'account/register',model)
  }
  logout(){
    return this.http.get( environment.apiUrl+'account/logout').subscribe({
      next:(res:any)=>{
        localStorage.removeItem('token');
        localStorage.removeItem('user');
        this.currentUserSig.set(null);
        this.userRoles.set([]);
        this.router.navigateByUrl('/');
        location.reload();
      },
      error:(err)=> console.log(err)
    });
  }
  currentUser(){
    if(localStorage.getItem("user")){
      this.currentUserSig.set(JSON.parse(localStorage.getItem("user")));
      this.userRoles.set(this.getUserRoles( localStorage.getItem('token')));
    }
    return this.currentUserSig;
  }
  getUserRoles(token){
    return this.jwtHelper.decodeToken(token).role;
  }

  isUserAdmin(){

   return this.userRoles().includes('Admin')!==null||this.userRoles().includes('Admin')!==undefined;
  }

  isAuthenticated(){
    return  this.currentUserSig() && !this.jwtHelper.isTokenExpired(this.currentUserSig().token);
  }


  test()
  {
    return this.http.get( this.apiUrl+'secure/secure');
  }
}

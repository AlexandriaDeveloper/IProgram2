import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../environment';
import { Router } from '@angular/router';
import { JwtHelperService } from '@auth0/angular-jwt';
import { ChangePasswordRequest } from '../models/changePasswordRequest';
import { map } from 'rxjs';
@Injectable({
  providedIn: 'root',
})
export class AuthService {
  http = inject(HttpClient);
  router = inject(Router);
  jwtHelper: JwtHelperService = new JwtHelperService();
  apiUrl = environment.apiUrl;
  currentUserSig = signal<any | undefined | null>(undefined);
  userRoles = signal<string[]>([]);
  constructor() { }
  login(model) {
    this.http.post(environment.apiUrl + 'account/login', model).
      subscribe({
        next: (res: any) => {
          localStorage.setItem('token', res.token);
          localStorage.setItem('user', JSON.stringify(res));
          this.currentUserSig.set(res);
          this.userRoles.set(this.getUserRoles(res.token));
          this.router.navigateByUrl('/');
        },
        error: (err) => console.log(err)
      });
  }
  signup(model) {
    return this.http.post(environment.apiUrl + 'account/register', model)
  }
  logout() {
    return this.http.get(environment.apiUrl + 'account/logout').subscribe({
      next: (res: any) => {
        localStorage.removeItem('token');
        localStorage.removeItem('user');
        this.currentUserSig.set(null);
        this.userRoles.set([]);
        this.router.navigateByUrl('/');
        location.reload();
      },
      error: (err) => console.log(err)
    });
  }
  currentUser() {

    if (localStorage.getItem("user")) {
      this.currentUserSig.set(JSON.parse(localStorage.getItem("user")));
      this.userRoles.set(this.getUserRoles(localStorage.getItem('token')));
    }
    //check expiration
    if (this.currentUserSig() && this.jwtHelper.isTokenExpired(this.currentUserSig().token)) {
      this.logout();
      //navigate to login page
      this.router.navigate(['/account/login']);
    }



    return this.currentUserSig;
  }
  getUserRoles(token) {
    return this.jwtHelper.decodeToken(token).role;
  }
  isUserAdmin() {

    if (this.currentUserSig() === undefined) {
      return false;
    }
    let isAdmin = false;
    isAdmin = this.currentUserSig().roles.map(x => x === 'Admin')[0] as boolean
    //    console.log(this.currentUserSig().roles.);
    return isAdmin
  }
  isAuthenticated() {
    return this.currentUserSig() && !this.jwtHelper.isTokenExpired(this.currentUserSig().token);

  }
  test() {
    return this.http.get(this.apiUrl + 'secure/secure');
  }
  changePassword(model: ChangePasswordRequest) {
    return this.http.put(this.apiUrl + 'account/changePassword', model);
  }
  loadCurrentUser(token: string) {
    if (token === null) {
      this.currentUserSig.set(null);
      return this.currentUserSig();
    }

    let headers = new HttpHeaders();
    headers = headers.set('Authorization', `Bearer ${token}`);

    return this.http.get(this.apiUrl + 'account', { headers }).pipe(
      map((user: any) => {
        if (user) {
          localStorage.setItem('token', user.token);
          this.currentUserSig.set(user);
        }
      })
    );
  }
}

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
  currentUserSig = signal<any | undefined | null>(null);
  userRoles = signal<string[]>([]);
  runtimeStatusSig = signal<{ isReadOnly: boolean; isLocalFirst: boolean; runtimeMode: string; selectedDatabase: string } | null>(null);
  constructor() {
    const userString = localStorage.getItem('user');
    const token = localStorage.getItem('token');
    if (userString && token) {
      const user = JSON.parse(userString);
      this.currentUserSig.set(user);
      this.userRoles.set(this.getUserRoles(token));
    }
    this.loadRuntimeStatus();
  }
  loadRuntimeStatus() {
    this.http.get<any>(this.apiUrl + 'account/runtime-status').subscribe({
      next: (status) => this.runtimeStatusSig.set(status),
      error: (err) => console.log('Runtime status not available', err)
    });
  }
  login(model) {
    this.http.post(environment.apiUrl + 'account/login', model).
      subscribe({
        next: (res: any) => {
          localStorage.setItem('token', res.token);
          localStorage.setItem('user', JSON.stringify(res));
          this.currentUserSig.set(res);
          this.userRoles.set(this.getUserRoles(res.token));
          this.loadRuntimeStatus();
          this.router.navigateByUrl('/');
        },
        error: (err) => console.log(err)
      });
  }
  signup(model) {
    return this.http.post(environment.apiUrl + 'account/register', model)
  }
  getDatabases() {
    return this.http.get(environment.apiUrl + 'account/databases');
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
    const user = this.currentUserSig();
    if (!user || !user.roles || !Array.isArray(user.roles)) {
      return false;
    }
    return user.roles.some((x: any) => x === 'Admin');
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

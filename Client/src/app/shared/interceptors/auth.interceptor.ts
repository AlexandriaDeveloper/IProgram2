import { HttpInterceptorFn } from '@angular/common/http';

export const AuthInterceptor: HttpInterceptorFn = (req, next) => {

  console.log('AuthInterceptor: Processing request', req.url);

  const token = localStorage.getItem('token');
  const db = localStorage.getItem('db-selection');

  const headers: { [key: string]: string } = {};
  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }
  if (db) {
    headers['X-Db-Selection'] = db;
  }

  req = req.clone({
    setHeaders: headers
  });
  return next(req);
};

import { HttpInterceptorFn } from '@angular/common/http';

export const AuthInterceptor: HttpInterceptorFn = (req, next) => {

  console.log('AuthInterceptor: Processing request', req.url);

  const token = localStorage.getItem('token') ?? '';
  const db = localStorage.getItem('db-selection') ?? 'old';
  req = req.clone({
    setHeaders: {
      Authorization: token ? `Bearer ${token}` : '',
      'X-Db-Selection': db
    }
  })
  return next(req);
};

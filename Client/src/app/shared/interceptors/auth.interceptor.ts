import { HttpInterceptorFn } from '@angular/common/http';

export const AuthInterceptor: HttpInterceptorFn = (req, next) => {

console.log('auth interceptor');

  const token = localStorage.getItem('token')??'';
    req = req.clone({
      setHeaders: {
        Authorization: token? `Bearer ${token}`:''
      }
    })
  return next(req);
};

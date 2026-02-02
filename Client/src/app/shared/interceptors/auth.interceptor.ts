import { HttpInterceptorFn } from '@angular/common/http';

export const AuthInterceptor: HttpInterceptorFn = (req, next) => {

  console.log('AuthInterceptor: Processing request', req.url);

  const token = localStorage.getItem('token') ?? '';
  req = req.clone({
    setHeaders: {
      Authorization: token ? `Bearer ${token}` : ''
    }
  })
  return next(req);
};

import { HttpEvent, HttpHandlerFn, HttpRequest, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, map, throwError } from 'rxjs';
import { ToasterService } from '../components/toaster/toaster.service';
import { LoadingService } from '../service/loading.service';

export function ResponseInterceptor(req: HttpRequest<unknown>, next: HttpHandlerFn): Observable<HttpEvent<unknown>> {
  const toaster = inject(ToasterService);
  const loadingService = inject(LoadingService);
  const router = inject(Router);

  // Check for skip loading header
  if (req.headers.has('x-skip-loading')) {
    const copy = req.clone({ headers: req.headers.delete('x-skip-loading') });
    return next(copy);
  }

  loadingService.show();

  return next(req).pipe(
    map((event: HttpEvent<any>) => {
      if (event instanceof HttpResponse && event.status === 200) {
        if (req.url.includes('upload')) {
          req = req.clone({
            reportProgress: true,
            responseType: 'blob',
          });
        }
      }
      return event;
    }),
    catchError((error) => {
      return throwError(() => error);
    }),
    finalize(() => {
      loadingService.hide();
    })
  );
}

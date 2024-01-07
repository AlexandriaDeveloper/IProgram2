import { HttpErrorResponse, HttpEvent, HttpEventType, HttpHandler, HttpHandlerFn, HttpInterceptor, HttpInterceptorFn, HttpRequest, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Router, NavigationExtras } from '@angular/router';
import { Observable, catchError, delay, finalize, map, tap, throwError } from 'rxjs';
import { ToasterService } from '../components/toaster/toaster.service';
import { LoadingService } from '../service/loading.service';


export function ResponseInterceptor(req: HttpRequest<unknown>, next: HttpHandlerFn): Observable<HttpEvent<unknown>> {
  let  toaster = inject(ToasterService);
  let loadingService =inject(LoadingService);
 let router = inject(Router);
 console.log('intercept started');


 if(req.url.includes('upload')){
  req = req.clone({
    reportProgress: true,
    responseType: 'blob',
  });
}
loadingService.isLoading();

   return next(req).pipe(map((event:HttpEvent<any>) => {
    if (event instanceof HttpResponse
      && event.status === 200) {
        if(event.body?.isSuccess===false && event.body?.error){

          toaster.openErrorToaster(
            event.body?.error?.message
          )
          event = event.clone({ body:  null})
        }
        if(event.body?.isSuccess===false && event.body?.errors){

          toaster.openErrorToaster(
            event.body?.error?.message
          )
          event = event.clone({ body:  null})
        }
        else{
          if(event.body?.isSuccess===true){
            event = event.clone({ body:  event.body?.value });
            }
        }
    }

   return event
  }),finalize(() => {
    loadingService.loaded();
    console.log('intercept ended');

   }));


 }

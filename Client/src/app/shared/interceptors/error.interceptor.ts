import { HttpRequest, HttpHandlerFn, HttpEvent, HttpErrorResponse } from "@angular/common/http";
import { inject } from "@angular/core";
import { Router } from "@angular/router";
import { Observable, catchError, throwError } from "rxjs";
import { ToasterService } from "../components/toaster/toaster.service";
import { LoadingService } from "../service/loading.service";

export function ErrorInterceptor(req: HttpRequest<unknown>, next: HttpHandlerFn): Observable<HttpEvent<unknown>> {
  let  toaster = inject(ToasterService);
  let loadingService =inject(LoadingService);
 let router = inject(Router);
 // console.log('intercept started');
   return next(req).pipe(
    catchError((error: any) => {
      console.log(error);
      toaster.openErrorToaster(


       error.detail,"error"
      )
      if (error.status === 400) {
        // auto logout if 401 response returned from api
         //location.reload();
        // router.navigateByUrl('/account/login');
        toaster.openErrorToaster(
          error.error,"error"
          );
      }

    if (error.status === 401) {
      // auto logout if 401 response returned from api
       //location.reload();
      // router.navigateByUrl('/account/login');
      toaster.openErrorToaster(
        "عفوا يجب عليك دخول الحساب اولا ","error"
      );
      router.navigateByUrl('/account/login');
    }
    if (error.status === 403) {
      debugger
      // auto logout if 401 response returned from api
       //location.reload();
      // router.navigateByUrl('/account/login');
      toaster.openErrorToaster(
        "عفوا ليس لديك صلاحيه ","error"
      );
    //  router.navigateByUrl('/');
    }
    if (error.status === 404) {
      // auto logout if 401 response returned from api
       //location.reload();
      // router.navigateByUrl('/account/login');
      toaster.openErrorToaster(
        "عفوا الصفحة غير موجودة","error"
      );
    }
    if (error.status === 500) {
      console.log(error.error.error.message);

        toaster.openErrorToaster(
          error.error.error.message,"error"
        );



    }
    // if (error.error.code === "500") {
    //   console.log(error);

    //   toaster.openErrorToaster(
    //   error.error.message,"error"
    //   );
    // }

   //router.navigateByUrl('/account/login');
     return throwError(() =>  error
     ) ;

   }));


 }

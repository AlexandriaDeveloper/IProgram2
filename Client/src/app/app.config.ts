import {  ResponseInterceptor } from './shared/interceptors/response.interceptor';

import { ApplicationConfig } from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { HTTP_INTERCEPTORS, provideHttpClient, withInterceptors } from '@angular/common/http';
import { AuthInterceptor } from './shared/interceptors/auth.interceptor';
import { provideAnimations } from '@angular/platform-browser/animations';
import { ErrorInterceptor } from './shared/interceptors/error.interceptor';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { DateAdapter, MAT_DATE_FORMATS, MatNativeDateModule } from '@angular/material/core';
import { MatMomentDateModule } from '@angular/material-moment-adapter';


export const appConfig: ApplicationConfig = {

  providers: [
    provideRouter(routes),
     provideHttpClient(withInterceptors(
      [AuthInterceptor,ResponseInterceptor,ErrorInterceptor]
      ))
     , provideAnimations(),



  ]
};

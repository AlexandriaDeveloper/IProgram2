import {  ResponseInterceptor } from './shared/interceptors/response.interceptor';
import { ApplicationConfig, importProvidersFrom } from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import {  provideHttpClient, withInterceptors } from '@angular/common/http';
import { AuthInterceptor } from './shared/interceptors/auth.interceptor';
import { provideAnimations } from '@angular/platform-browser/animations';
import { ErrorInterceptor } from './shared/interceptors/error.interceptor';
import {  GALLERY_CONFIG as DEFAULT_GALLERY_CONFIG, GalleryConfig, GalleryModule } from 'ng-gallery';
export const appConfig: ApplicationConfig = {

  providers: [


    {
      provide: DEFAULT_GALLERY_CONFIG,
      useValue: {
        imageSize: 'contain',

        thumbPosition: 'left',


      } as GalleryConfig
    },
    provideAnimations() ,

    provideRouter(routes),
     provideHttpClient(withInterceptors(
      [AuthInterceptor,ResponseInterceptor,ErrorInterceptor]
      ))
     , provideAnimations()

  ]
};

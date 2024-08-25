import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../service/auth.service';
import { ToasterService } from '../components/toaster/toaster.service';



export const authGuard: CanActivateFn = (route, state) => {

  const auth = inject(AuthService);
  const router = inject(Router);
  const toaster = inject(ToasterService);
  if(!auth.isAuthenticated()){
    router.navigate(['/account/login']);
    return false;
  }
  if(auth.isUserAdmin()){
    return true;
  }
  else{
    toaster.openErrorToaster("عفوا ليس لديك الصلاحيه للدخول", 'info' )
  }
 //
  return false;

};

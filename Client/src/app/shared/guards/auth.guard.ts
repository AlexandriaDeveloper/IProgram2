import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../service/auth.service';



export const authGuard: CanActivateFn = (route, state) => {

  const auth = inject(AuthService);
  const router = inject(Router);
  if(auth.isUserAdmin()){
    return true;
  }
  router.navigate(['/account/login']);
  return false;

};

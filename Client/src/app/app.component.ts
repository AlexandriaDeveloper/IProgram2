import { Component, signal, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule, RouterOutlet } from '@angular/router';
import { AuthService } from './shared/service/auth.service';
import { LoadingService } from './shared/service/loading.service';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AngularComponentsModule } from './shared/angular-components.module';
import { SharedModule } from './shared/shared.module';
import { NavbarComponent } from './shared/components/navbar/navbar.component';
import { MatNativeDateModule } from '@angular/material/core';

//import { LoginComponent } from './account/login/login.component';
interface IUser {
  name: string
  role: string,
  id: string
}
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [

    RouterOutlet,
    RouterModule,
    SharedModule,
    NavbarComponent,
    MatProgressSpinnerModule
  ],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit {
  constructor(private router: Router) { }
  loadingService = inject(LoadingService);
  ngOnInit(): void {

    this.loadCurrentUser();
    this.roles = this.auth.userRoles();

  }
  loadCurrentUser() {
    const token = localStorage.getItem('token');
    if (!token) {
      this.router.navigate(['/account/login']);
      return;
    }
    //if token is expired
    if (this.auth.jwtHelper.isTokenExpired(token)) {
      this.router.navigate(['/account/login']);
      return;
    }

    this.auth.loadCurrentUser(token).subscribe((x) => {
      this.currentUser = x;
      console.log(x);
      console.log('loaded user');
    }, error => {
      console.log(error);
      //navigate to login page
      this.router.navigate(['/account/login']);
    });
  }
  logout() {
    this.auth.logout();
  }


  testAuth() {
    this.auth.test().subscribe({
      next: (res: any) => {
      },
    })
  }
  auth = inject(AuthService);
  currentUser;
  roles;
}


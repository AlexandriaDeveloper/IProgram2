import { Component, signal, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, RouterOutlet } from '@angular/router';
import { AuthService } from './shared/service/auth.service';
import { AngularComponentsModule } from './shared/angular-components.module';
import { SharedModule } from './shared/shared.module';
import { NavbarComponent } from './shared/components/navbar/navbar.component';
import { MatNativeDateModule } from '@angular/material/core';

//import { LoginComponent } from './account/login/login.component';
interface IUser{
  name: string
  role:string,
  id:string
}
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [

    RouterOutlet,
    RouterModule,
    SharedModule,
    NavbarComponent
  ],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit {
  ngOnInit(): void {

    this.currentUser= this.auth.currentUser();
    this.roles= this.auth.userRoles();

  }
  logout(){
    this.auth.logout();
  }
  testAuth(){
    this. auth.test().subscribe({
      next:(res:any)=>{
      },
    })
  }
  auth = inject(AuthService);
  currentUser ;
  roles;
}


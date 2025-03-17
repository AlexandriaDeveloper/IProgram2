import { AuthService } from './../../service/auth.service';

import { Component, ViewChild, NgModule, inject } from '@angular/core';
import { AngularComponentsModule } from '../../angular-components.module';
import { SharedModule } from '../../shared.module';
import { Router, RouterModule } from '@angular/router';
import { MatSidenav, MatSidenavModule, } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatIconModule } from '@angular/material/icon';
import { MatDividerModule } from '@angular/material/divider';
import { MatListModule } from '@angular/material/list';
import { Observable, map, shareReplay, window } from 'rxjs';

import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';

import { CommonModule } from '@angular/common';


@Component({
  selector: 'app-navbar',
  standalone: true,
  imports: [RouterModule,
    SharedModule,
    MatDividerModule,
    MatListModule,
    MatToolbarModule,
    CommonModule
  ],
  templateUrl: './navbar.component.html',
  styleUrl: './navbar.component.scss'
})
export class NavbarComponent {
  auth = inject(AuthService);
  router = inject(Router);
  panelOpenState = false;
  @ViewChild('drawer', { static: true }) drawer: MatSidenav;
  isHandset$: Observable<boolean> = this.breakpointObserver.observe(Breakpoints.Handset)
    .pipe(
      map(result => result.matches),
      shareReplay()
    );

  constructor(
    private breakpointObserver: BreakpointObserver,

  ) {
    // if (!this.auth.isAuthenticated()) {
    //   //navigate to login page
    //   this.router.navigate(['/account/login']);
    // }

  }

  logout() {
    this.auth.logout();
  }

  navigateTo(url) {
    //window reload
    this.router.navigateByUrl(url);
    // this.router.navigate([url]);
  }

}

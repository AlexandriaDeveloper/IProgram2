import { AuthService } from './../../service/auth.service';
import { SyncService } from '../../service/sync.service';

import { Component, ViewChild, NgModule, inject, AfterViewInit } from '@angular/core';
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
import { VideoService } from '../../service/video.service';


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
export class NavbarComponent implements AfterViewInit {
  auth = inject(AuthService);
  syncService = inject(SyncService);
  router = inject(Router);
  videoService = inject(VideoService);
  panelOpenState = false;
  syncMessage = '';
  @ViewChild('drawer', { static: true }) drawer: MatSidenav;
  @ViewChild('videoBg') videoBg: any;
  isHandset$: Observable<boolean> = this.breakpointObserver.observe(Breakpoints.Handset)
    .pipe(
      map(result => result.matches),
      shareReplay()
    );
  private videoCheckInterval: any;
  constructor(
    private breakpointObserver: BreakpointObserver,

  ) {
    // if (!this.auth.isAuthenticated()) {
    //   //navigate to login page
    //   this.router.navigate(['/account/login']);
    // }

  }
  async ngAfterViewInit(): Promise<void> {
    try {
      const url = await this.videoService.getVideoUrl();
      if (this.videoBg && this.videoBg.nativeElement) {
        const v = this.videoBg.nativeElement;
        v.src = url;
        v.load();
        this.setupBackgroundVideo();
        v.play().catch(() => { /* autoplay may be blocked; keep muted */ });
      }
    } catch (err) {
      console.error('Failed to load background video', err);
    }
  }

  private setupBackgroundVideo(): void {
    if (!this.videoBg) return;

    // Function to ensure video plays and loops
    const ensureVideoPlays = () => {
      if (this.videoBg.nativeElement.paused) {
        const playPromise = this.videoBg.nativeElement.play();

        if (playPromise !== undefined) {
          playPromise.catch(error => {
            console.log("Auto-play was prevented. Muting video to enable playback.");
            this.videoBg.nativeElement.muted = true;
            this.videoBg.nativeElement.play();
          });
        }
      }

      // Ensure loop attribute is set
      this.videoBg.nativeElement.loop = true;

      // Listen for video end event to restart if needed
      this.videoBg.nativeElement.addEventListener('ended', () => {
        this.videoBg.nativeElement.currentTime = 0;
        this.videoBg.nativeElement.play();
      });
    };

    // Initial call
    ensureVideoPlays();

    // Set up a periodic check to ensure video is still playing
    this.videoCheckInterval = setInterval(ensureVideoPlays, 1000);

    // Handle page visibility changes
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden) {
        ensureVideoPlays();
      }
    });
  }

  ngOnDestroy(): void {
    // Clean up the interval when component is destroyed
    if (this.videoCheckInterval) {
      clearInterval(this.videoCheckInterval);
    }
    this.videoService.revoke();
  }

  logout() {
    this.auth.logout();
  }

  syncToCloud(force: boolean = false) {
    if (this.syncService.isSyncing()) return;

    this.syncMessage = '';
    this.syncService.syncToCloud(force).subscribe({
      next: (result) => {
        this.syncService.isSyncing.set(false);
        if (result.success) {
          this.syncMessage = `✅ تم الرفع في ${result.duration}`;
          setTimeout(() => this.syncMessage = '', 5000);
        } else {
          this.syncMessage = `❌ ${result.message}`;
        }
      },
      error: (err) => {
        this.syncService.isSyncing.set(false);
        // Handle Version Conflict
        if (err.status === 409) {
          if (confirm('⚠️ تنبيه: يوجد بيانات أحدث على السحابة! \nهل أنت متأكد من الاستبدال؟ (سيتم حذف بيانات السحابة)')) {
            this.syncToCloud(true); // Retry with force=true
          } else {
            this.syncMessage = '❌ تم إلغاء العملية';
          }
        } else {
          this.syncMessage = `❌ فشل الرفع: ${err.error?.message || err.message}`;
        }
      }
    });
    this.syncService.isSyncing.set(true);
  }

  pullFromCloud() {
    if (this.syncService.isSyncing()) return;

    this.syncMessage = '';
    this.syncService.pullFromCloud().subscribe({
      next: (result) => {
        this.syncService.isSyncing.set(false);
        if (result.success) {
          this.syncMessage = `✅ تم التنزيل في ${result.duration}`;
          setTimeout(() => this.syncMessage = '', 5000);
        } else {
          this.syncMessage = `❌ ${result.message}`;
        }
      },
      error: (err) => {
        this.syncService.isSyncing.set(false);
        this.syncMessage = `❌ فشل التنزيل: ${err.error?.message || err.message}`;
      }
    });
    this.syncService.isSyncing.set(true);
  }

  navigateTo(url) {
    //window reload
    this.router.navigateByUrl(url);
    // this.router.navigate([url]);
  }

}

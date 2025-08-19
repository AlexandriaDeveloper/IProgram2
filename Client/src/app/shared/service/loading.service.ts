import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class LoadingService {

  private loadingCount = 0;
  private _loading$ = new BehaviorSubject<boolean>(false);
  readonly isLoading$ = this._loading$.asObservable();

  constructor() { }

  show() {
    this.loadingCount++;
    if (this.loadingCount > 0) {
      // defer emission to next microtask to avoid ExpressionChangedAfterItHasBeenCheckedError
      Promise.resolve().then(() => this._loading$.next(true));
    }
  }

  hide() {
    this.loadingCount = Math.max(0, this.loadingCount - 1);
    if (this.loadingCount === 0) {
      // defer emission to avoid changing bindings during change detection
      Promise.resolve().then(() => this._loading$.next(false));
    }
  }

  reset() {
    this.loadingCount = 0;
    this._loading$.next(false);
  }
}

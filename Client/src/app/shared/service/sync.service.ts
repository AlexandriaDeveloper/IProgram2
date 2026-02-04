import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../environment';
import { Observable, catchError, of } from 'rxjs';

export interface SyncResult {
  success: boolean;
  message: string;
  duration: string;
  tables: TableSyncResult[];
}

export interface TableSyncResult {
  table: string;
  source: number;
  upserted: number;
  deleted: number;
  success: boolean;
  error?: string;
}

@Injectable({
  providedIn: 'root',
})
export class SyncService {
  private http = inject(HttpClient);
  private apiUrl = environment.apiUrl;

  // Signals for reactive state
  isSyncing = signal<boolean>(false);
  lastSyncResult = signal<SyncResult | null>(null);
  syncError = signal<string | null>(null);

  /**
   * Full sync from SQL Server to Supabase
   * @returns Observable with sync result
   */
  syncToCloud(): Observable<SyncResult> {
    this.isSyncing.set(true);
    this.syncError.set(null);

    return this.http.post<SyncResult>(this.apiUrl + 'migration/sync', {}).pipe(
      catchError(error => {
        this.isSyncing.set(false);
        this.syncError.set(error.error?.message || error.message || 'Sync failed');
        throw error;
      })
    );
  }

  /**
   * Perform sync and update signals
   */
  performSync(): void {
    this.syncToCloud().subscribe({
      next: (result) => {
        this.isSyncing.set(false);
        this.lastSyncResult.set(result);
        if (!result.success) {
          this.syncError.set(result.message || 'Sync completed with errors');
        }
      },
      error: (err) => {
        this.isSyncing.set(false);
        console.error('Sync failed:', err);
      }
    });
  }
}

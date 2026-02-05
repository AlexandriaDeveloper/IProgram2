import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../environment';
import { Observable, catchError, of } from 'rxjs';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';

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

export type SyncDirection = 'push' | 'pull';

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
  syncDirection = signal<SyncDirection | null>(null);
  syncLog = signal<string>(''); // For Streamed Logs

  private hubConnection: HubConnection | null = null;

  constructor() {
    this.startConnection();
  }

  private startConnection() {
    this.hubConnection = new HubConnectionBuilder()
      .withUrl(environment.apiUrl.replace('/api/', '/migrationHub'))
      .withAutomaticReconnect()
      .build();

    this.hubConnection
      .start()
      .then(() => console.log('SignalR Connection started'))
      .catch(err => console.log('Error while starting connection: ' + err));

    this.hubConnection.on('ReceiveProgress', (message: string) => {
      this.syncLog.set(message);
    });
  }

  /**
   * Full sync from SQL Server to Supabase
   * @param force Force sync even if version conflict exists
   * @returns Observable with sync result
   */
  syncToCloud(force: boolean = false): Observable<SyncResult> {
    this.isSyncing.set(true);
    this.syncError.set(null);
    this.syncLog.set('Starting Sync...'); // Reset Log
    this.syncDirection.set('push');

    return this.http.post<SyncResult>(`${this.apiUrl}migration/sync?force=${force}`, {}).pipe(
      catchError(error => {
        this.isSyncing.set(false);
        // Pass the error object to handle 409 Conflict in component
        if (error.status === 409) {
          throw error;
        }
        this.syncError.set(error.error?.message || error.message || 'Sync failed');
        throw error;
      })
    );
  }

  /**
   * Pull data from Supabase to SQL Server
   * @returns Observable with sync result
   */
  pullFromCloud(): Observable<SyncResult> {
    this.isSyncing.set(true);
    this.syncError.set(null);
    this.syncLog.set('Starting Pull...'); // Reset Log
    this.syncDirection.set('pull');

    return this.http.post<SyncResult>(this.apiUrl + 'migration/pull', {}).pipe(
      catchError(error => {
        this.isSyncing.set(false);
        this.syncError.set(error.error?.message || error.message || 'Pull failed');
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

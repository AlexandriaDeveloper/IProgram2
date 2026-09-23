import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal, computed } from '@angular/core';
import { environment } from '../../environment';
import { Observable, catchError, tap, of } from 'rxjs';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';

export interface LocalSyncStatus {
  databaseId: string;
  pendingCount: number;
  inProgressCount: number;
  failedCount: number;
  totalCount: number;
  lastServerVersion: number;
  lastSuccessfulPushUtc: string | null;
  lastSuccessfulPullUtc: string | null;
  lastSyncAttemptUtc: string | null;
  lastSyncError: string | null;
  runtimeMode: string;
  isReadOnly: boolean;
  isLocalFirst: boolean;
}

export interface ScopeSyncStatus {
  scope: string; // 'Daily' | 'Forms'
  status: string; // 'UP_TO_DATE' | 'REMOTE_NEWER' | 'BOTH_CHANGED' | 'UNKNOWN' | 'SYNC_STATE_ERROR' | 'NOT_BASELINED'
  isBaselined: boolean;
  localVersion: number;
  serverVersion: number;
  pendingCount: number;
  message: string | null;
}

export interface OnlineSyncStatus {
  databaseId: string;
  isOnline: boolean;
  overallStatus: string;
  serverVersion: number;
  localVersion: number;
  totalPendingCount: number;
  scopes: ScopeSyncStatus[];
  checkedAtUtc: string;
  errorMessage: string | null;
}

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

  // Phase 4 Canonical State Signals
  localStatus = signal<LocalSyncStatus | null>(null);
  onlineStatus = signal<OnlineSyncStatus | null>(null);
  isCheckingOnline = signal<boolean>(false);
  isPushing = signal<boolean>(false);
  isPulling = signal<boolean>(false);
  pendingCount = computed(() => this.localStatus()?.pendingCount ?? 0);

  // Legacy Signals for reactive state
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
    try {
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
    } catch {
      // Ignore hub connection failures in offline/local environments
    }
  }

  /**
   * Phase 4: Fetch local sync status (Local-only, zero Azure/remote query)
   */
  fetchLocalStatus(): Observable<LocalSyncStatus | null> {
    return this.http.get<LocalSyncStatus>(`${this.apiUrl}sync/status/local`).pipe(
      tap(status => this.localStatus.set(status)),
      catchError(err => {
        console.warn('Failed to fetch local sync status:', err);
        return of(null);
      })
    );
  }

  /**
   * Phase 4: Check Online Status (Explicit user trigger, read-only remote check)
   */
  checkOnlineStatus(): Observable<OnlineSyncStatus> {
    this.isCheckingOnline.set(true);
    return this.http.post<OnlineSyncStatus>(`${this.apiUrl}sync/status/check-online`, {}).pipe(
      tap(status => {
        this.isCheckingOnline.set(false);
        this.onlineStatus.set(status);
      }),
      catchError(err => {
        this.isCheckingOnline.set(false);
        const fallback: OnlineSyncStatus = {
          databaseId: this.localStatus()?.databaseId || '',
          isOnline: false,
          overallStatus: 'UNKNOWN',
          serverVersion: 0,
          localVersion: this.localStatus()?.lastServerVersion || 0,
          totalPendingCount: this.pendingCount(),
          scopes: [
            {
              scope: 'Daily',
              status: 'UNKNOWN',
              isBaselined: true,
              localVersion: this.localStatus()?.lastServerVersion || 0,
              serverVersion: 0,
              pendingCount: 0,
              message: 'تعذر الاتصال بالخادم السحابي'
            },
            {
              scope: 'Forms',
              status: 'NOT_BASELINED',
              isBaselined: false,
              localVersion: this.localStatus()?.lastServerVersion || 0,
              serverVersion: 0,
              pendingCount: 0,
              message: 'نطاق النماذج بانتظار إجراء مطابقة الأساس المعتمدة (NOT_BASELINED)'
            }
          ],
          checkedAtUtc: new Date().toISOString(),
          errorMessage: err.error?.message || err.message || 'تعذر الاتصال بالسحابة'
        };
        this.onlineStatus.set(fallback);
        return of(fallback);
      })
    );
  }

  /**
   * Phase 4: Push Now (Explicit user action to push local outbox to Azure)
   */
  pushNow(): Observable<any> {
    this.isPushing.set(true);
    return this.http.post<any>(`${this.apiUrl}sync/push`, {}).pipe(
      tap(res => {
        this.isPushing.set(false);
        this.fetchLocalStatus().subscribe();
      }),
      catchError(err => {
        this.isPushing.set(false);
        this.fetchLocalStatus().subscribe();
        throw err;
      })
    );
  }

  /**
   * Phase 4: Pull Now (Explicit user action to pull updates from Azure)
   */
  pullNow(): Observable<any> {
    this.isPulling.set(true);
    return this.http.post<any>(`${this.apiUrl}sync/pull`, {}).pipe(
      tap(res => {
        this.isPulling.set(false);
        this.fetchLocalStatus().subscribe();
      }),
      catchError(err => {
        this.isPulling.set(false);
        this.fetchLocalStatus().subscribe();
        throw err;
      })
    );
  }

  /**
   * Legacy Full sync from SQL Server to Supabase
   */
  syncToCloud(force: boolean = false): Observable<SyncResult> {
    this.isSyncing.set(true);
    this.syncError.set(null);
    this.syncLog.set('Starting Sync...');
    this.syncDirection.set('push');

    return this.http.post<SyncResult>(`${this.apiUrl}migration/sync?force=${force}`, {}).pipe(
      catchError(error => {
        this.isSyncing.set(false);
        if (error.status === 409) {
          throw error;
        }
        this.syncError.set(error.error?.message || error.message || 'Sync failed');
        throw error;
      })
    );
  }

  /**
   * Legacy Pull data from Supabase to SQL Server
   */
  pullFromCloud(): Observable<SyncResult> {
    this.isSyncing.set(true);
    this.syncError.set(null);
    this.syncLog.set('Starting Pull...');
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
   * Legacy Perform sync
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

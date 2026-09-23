import { HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { SyncService } from '../service/sync.service';
import { tap } from 'rxjs';

/**
 * Phase 4 Centralized Mutation Interceptor:
 * Refreshes local sync status (strictly local SQL query, zero Azure contact)
 * after every successful business mutation (POST, PUT, DELETE, or CopyFormToArchive GET).
 * Excludes /api/sync/ requests to avoid recursion.
 */
export const syncMutationInterceptor: HttpInterceptorFn = (req, next) => {
  const syncService = inject(SyncService);

  const url = req.url.toLowerCase();
  const isSyncUrl = url.includes('/api/sync/') || url.includes('api/sync/');

  const method = req.method.toUpperCase();
  const isMutatingMethod = method === 'POST' || method === 'PUT' || method === 'DELETE';
  const isMutatingGet = method === 'GET' && url.includes('copyformtoarchive');
  const isBusinessMutation = !isSyncUrl && (isMutatingMethod || isMutatingGet);

  return next(req).pipe(
    tap(event => {
      if (event instanceof HttpResponse && isBusinessMutation && event.status >= 200 && event.status < 300) {
        syncService.fetchLocalStatus().subscribe({
          error: (err) => console.warn('Local status refresh failed after business mutation:', err)
        });
      }
    })
  );
};

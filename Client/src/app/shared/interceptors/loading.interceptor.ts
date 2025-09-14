import { inject } from '@angular/core';
import { HttpRequest, HttpHandlerFn, HttpInterceptorFn } from '@angular/common/http';
import { finalize } from 'rxjs/operators';
import { LoadingService } from '../service/loading.service';

export const loadingInterceptor: HttpInterceptorFn = (req: HttpRequest<any>, next: HttpHandlerFn) => {
    const loading = inject(LoadingService);


    // allow skipping the loader by adding header 'x-skip-loading'
    if (req.headers.has('x-skip-loading')) {
        const copy = req.clone({ headers: req.headers.delete('x-skip-loading') });
        return next(copy);
    }

    // loading.show();

    return next(req).pipe(
        //finalize(() => loading.hide()

        // )
    );
};

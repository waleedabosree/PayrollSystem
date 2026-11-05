import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { ApiError } from './models';

/**
 * Converts every failure into an ApiError (the API always answers application/problem+json).
 * The correlation id is read from the problem body first and the X-Correlation-Id header as a
 * fallback -- the header survives even when the body isn't JSON (e.g. a proxy 502).
 * This is also the single place a later auth phase would hook in 401 handling.
 */
export const problemInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse)) return throwError(() => err);

      const body = (typeof err.error === 'object' && err.error) || {};
      const apiError: ApiError = {
        status: err.status,
        title: body.title ?? (err.status === 0 ? 'API unreachable' : err.statusText),
        detail:
          body.detail ??
          (err.status === 0
            ? 'Could not reach the API. Is it running and is CORS configured for this origin?'
            : `Request failed with status ${err.status}.`),
        code: body.code,
        correlationId: body.correlationId ?? err.headers?.get('X-Correlation-Id') ?? undefined,
        errors: Array.isArray(body.errors) ? body.errors : [],
      };
      return throwError(() => apiError);
    }),
  );

import { HttpClient, HttpEvent, HttpEventType, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, filter, map } from 'rxjs';
import { API_BASE_URL } from './config';
import {
  BatchQuery,
  PagedResponse,
  PayrollBatchDetail,
  PayrollBatchSummary,
  UploadAccepted,
} from './models';

export type UploadEvent =
  | { kind: 'progress'; percent: number }
  | { kind: 'done'; result: UploadAccepted };

@Injectable({ providedIn: 'root' })
export class BatchApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(API_BASE_URL)}/api/batches`;

  // Bumped after an upload so the list reloads immediately instead of waiting for its next poll.
  readonly changed = signal(0);

  list(query: BatchQuery): Observable<PagedResponse<PayrollBatchSummary>> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);
    if (query.status) params = params.set('status', query.status);
    if (query.search?.trim()) params = params.set('search', query.search.trim());
    if (query.entityId?.trim()) params = params.set('entityId', query.entityId.trim());
    return this.http.get<PagedResponse<PayrollBatchSummary>>(this.base, { params });
  }

  get(id: string): Observable<PayrollBatchDetail> {
    return this.http.get<PayrollBatchDetail>(`${this.base}/${id}`);
  }

  upload(entityId: string, file: File): Observable<UploadEvent> {
    const form = new FormData();
    form.append('entityId', entityId);
    form.append('file', file, file.name);

    return this.http
      .post<UploadAccepted>(this.base, form, { reportProgress: true, observe: 'events' })
      .pipe(
        map((event: HttpEvent<UploadAccepted>): UploadEvent | null => {
          if (event.type === HttpEventType.UploadProgress) {
            const percent = event.total ? Math.round((100 * event.loaded) / event.total) : 0;
            return { kind: 'progress', percent };
          }
          if (event.type === HttpEventType.Response && event.body) {
            return { kind: 'done', result: event.body };
          }
          return null;
        }),
        filter((e): e is UploadEvent => e !== null),
      );
  }
}

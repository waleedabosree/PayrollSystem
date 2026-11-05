import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, Signal, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { EMPTY, catchError, debounceTime, defer, repeat, switchMap, tap, timer } from 'rxjs';
import { BatchApi } from '../../core/batch-api';
import { POLL_INTERVAL_MS } from '../../core/config';
import { ACTIVE_STATUSES, ApiError, BATCH_STATUSES, BatchQuery, PagedResponse, PayrollBatchSummary } from '../../core/models';
import { CorrelationId } from '../../shared/correlation-id';
import { StatusBadge } from '../../shared/status-badge';

const IDLE_POLL_MS = 15000;

@Component({
  selector: 'app-batch-list',
  imports: [DatePipe, FormsModule, RouterLink, StatusBadge, CorrelationId],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card">
      <div class="head">
        <h2>Processing history</h2>
        <small class="live">
          @if (error()) { <span class="err">⚠ API unreachable — retrying</span> }
          @else if (hasActive()) { ● live (every {{ pollMs / 1000 }}s) }
          @else { idle (every {{ idleMs / 1000 }}s) }
        </small>
      </div>

      <div class="filters">
        <select [ngModel]="status()" (ngModelChange)="setStatus($event)" aria-label="Status filter">
          <option value="">All statuses</option>
          @for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }
        </select>
        <input type="search" placeholder="Search file name…" [ngModel]="search()" (ngModelChange)="setSearch($event)" aria-label="Search" />
        <input type="search" placeholder="Entity ID" [ngModel]="entityId()" (ngModelChange)="setEntity($event)" aria-label="Entity filter" />
      </div>

      <div class="scroll">
        <table>
          <thead>
            <tr><th>File</th><th>Entity</th><th>Status</th><th>Rows</th><th>Duration</th><th>Uploaded</th><th>Correlation ID</th></tr>
          </thead>
          <tbody>
            @for (b of items(); track b.id) {
              <tr>
                <td>
                  <a [routerLink]="['/batches', b.id]">{{ b.originalFileName }}</a>
                  <div class="sub">{{ (b.fileSizeBytes / 1024).toFixed(1) }} KB</div>
                </td>
                <td>{{ b.entityId }}</td>
                <td>
                  <app-status-badge [status]="b.status" />
                  @if (b.failureStage) { <div class="sub">at {{ b.failureStage }}</div> }
                </td>
                <td>{{ b.processedRows ?? '—' }}</td>
                <td>{{ b.processingDurationMs == null ? '—' : (b.processingDurationMs / 1000).toFixed(2) + ' s' }}</td>
                <td>{{ b.createdAtUtc | date: 'medium' }}</td>
                <td><app-correlation-id [value]="b.correlationId" /></td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="empty">{{ loaded() ? 'No batches match.' : 'Loading…' }}</td></tr>
            }
          </tbody>
        </table>
      </div>

      <div class="pager">
        <span>{{ total() }} batch(es) · page {{ page() }} of {{ pageCount() }}</span>
        <span>
          <button type="button" (click)="go(page() - 1)" [disabled]="page() <= 1">Previous</button>
          <button type="button" (click)="go(page() + 1)" [disabled]="page() >= pageCount()">Next</button>
        </span>
      </div>
    </section>
  `,
  styles: `
    .head { display: flex; justify-content: space-between; align-items: baseline; }
    .live { color: #475569; } .err { color: #b91c1c; }
    .filters { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0 12px; }
    select, input { padding: 8px; border: 1px solid #cbd5e1; border-radius: 6px; font: inherit; }
    .scroll { overflow-x: auto; }
    table { width: 100%; border-collapse: collapse; font-size: 14px; }
    th, td { text-align: left; padding: 8px; border-bottom: 1px solid #e2e8f0; vertical-align: top; }
    th { font-size: 12px; text-transform: uppercase; color: #64748b; }
    .sub { font-size: 12px; color: #64748b; }
    .empty { text-align: center; color: #64748b; padding: 24px; }
    .pager { display: flex; justify-content: space-between; align-items: center; margin-top: 12px; font-size: 14px; }
    .pager button { padding: 6px 14px; margin-left: 6px; border: 1px solid #cbd5e1; background: #fff; border-radius: 6px; cursor: pointer; }
    .pager button:disabled { opacity: 0.5; cursor: not-allowed; }
  `,
})
export class BatchList {
  private readonly api = inject(BatchApi);
  protected readonly pollMs = inject(POLL_INTERVAL_MS);
  protected readonly idleMs = IDLE_POLL_MS;
  protected readonly statuses = BATCH_STATUSES;

  protected readonly status = signal<BatchQuery['status']>('');
  protected readonly search = signal('');
  protected readonly entityId = signal('');
  protected readonly page = signal(1);
  private readonly pageSize = 10;

  protected readonly error = signal<ApiError | null>(null);
  protected readonly loaded = signal(false);

  private readonly query = computed<BatchQuery>(() => ({
    status: this.status(),
    search: this.search(),
    entityId: this.entityId(),
    page: this.page(),
    pageSize: this.pageSize,
  }));

  private readonly result: Signal<PagedResponse<PayrollBatchSummary> | null> = toSignal(
    toObservable(computed(() => ({ q: this.query(), tick: this.api.changed() }))).pipe(
      // Debounce keystrokes; also coalesces filter + page reset into one request.
      debounceTime(250),
      switchMap(({ q }) =>
        // Poll the same query until it changes (switchMap cancels this loop). The delay adapts:
        // fast while something is in flight, slow when everything is terminal.
        defer(() => this.api.list(q)).pipe(
          tap(() => this.error.set(null)),
          catchError((err: ApiError) => {
            this.error.set(err);
            return EMPTY;
          }),
          repeat({ delay: () => timer(this.hasActive() ? this.pollMs : IDLE_POLL_MS) }),
        ),
      ),
      tap(() => this.loaded.set(true)),
    ),
    { initialValue: null },
  );

  protected readonly items: Signal<PayrollBatchSummary[]> = computed(() => this.result()?.items ?? []);
  protected readonly total = computed(() => this.result()?.total ?? 0);
  protected readonly pageCount = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize)));
  protected readonly hasActive: Signal<boolean> = computed(() =>
    this.items().some((b) => ACTIVE_STATUSES.includes(b.status)),
  );

  protected setStatus(v: BatchQuery['status']) { this.status.set(v); this.page.set(1); }
  protected setSearch(v: string) { this.search.set(v); this.page.set(1); }
  protected setEntity(v: string) { this.entityId.set(v); this.page.set(1); }
  protected go(p: number) { this.page.set(Math.min(Math.max(1, p), this.pageCount())); }
}

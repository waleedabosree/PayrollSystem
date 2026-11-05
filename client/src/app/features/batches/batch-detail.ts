import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { EMPTY, catchError, defer, repeat, switchMap, takeWhile, tap, timer } from 'rxjs';
import { BatchApi } from '../../core/batch-api';
import { POLL_INTERVAL_MS } from '../../core/config';
import { ACTIVE_STATUSES, ApiError, PayrollBatchDetail } from '../../core/models';
import { CorrelationId } from '../../shared/correlation-id';
import { StatusBadge } from '../../shared/status-badge';

@Component({
  selector: 'app-batch-detail',
  imports: [DatePipe, RouterLink, StatusBadge, CorrelationId],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a routerLink="/">← Back to dashboard</a></p>

    @if (notFound()) {
      <section class="card"><h2>Batch not found</h2><p>No batch with id <code>{{ id() }}</code>.</p></section>
    } @else if (batch(); as b) {
      <section class="card">
        <div class="head">
          <h2>{{ b.originalFileName }}</h2>
          <app-status-badge [status]="b.status" />
        </div>

        @if (b.failureReason) {
          <div class="problem" role="alert">
            <strong>Failed{{ b.failureStage ? ' at ' + b.failureStage : '' }}</strong>
            <pre>{{ b.failureReason }}</pre>
          </div>
        }

        <dl>
          <dt>Batch ID</dt><dd><code>{{ b.id }}</code></dd>
          <dt>Correlation ID</dt><dd><app-correlation-id [value]="b.correlationId" /></dd>
          <dt>Entity</dt><dd>{{ b.entityId }}</dd>
          <dt>File size</dt><dd>{{ (b.fileSizeBytes / 1024).toFixed(1) }} KB</dd>
          <dt>Rows</dt><dd>{{ b.processedRows ?? '—' }} processed / {{ b.expectedRows ?? '—' }} expected</dd>
          <dt>Delivery attempts</dt><dd>{{ b.deliveryCount }}</dd>
          <dt>Processing time</dt><dd>{{ b.processingDurationMs == null ? '—' : (b.processingDurationMs / 1000).toFixed(2) + ' s' }}</dd>
          <dt>Blob</dt><dd><code>{{ b.blobContainer }}/{{ b.blobName }}</code></dd>
        </dl>

        <h3>Timeline</h3>
        <ol class="timeline">
          <li><span>Uploaded</span>{{ b.createdAtUtc | date: 'medium' }}</li>
          <li [class.todo]="!b.queuedAtUtc"><span>Queued</span>{{ b.queuedAtUtc ? (b.queuedAtUtc | date: 'medium') : 'waiting' }}</li>
          <li [class.todo]="!b.startedAtUtc"><span>Processing started</span>{{ b.startedAtUtc ? (b.startedAtUtc | date: 'medium') : 'waiting' }}</li>
          <li [class.todo]="!b.completedAtUtc"><span>Finished</span>{{ b.completedAtUtc ? (b.completedAtUtc | date: 'medium') : 'waiting' }}</li>
        </ol>
      </section>
    } @else if (error(); as e) {
      <section class="card"><h2>Could not load batch</h2><p>{{ e.detail }}</p></section>
    } @else {
      <section class="card">Loading…</section>
    }
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; }
    dt { color: #64748b; } dd { margin: 0; word-break: break-all; }
    .problem { background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; padding: 12px; border-radius: 6px; margin: 12px 0; }
    pre { margin: 6px 0 0; white-space: pre-wrap; font-family: inherit; }
    .timeline { list-style: none; padding: 0; border-left: 2px solid #2563eb; margin-left: 6px; }
    .timeline li { padding: 4px 0 4px 14px; }
    .timeline span { display: inline-block; min-width: 160px; font-weight: 600; }
    .timeline li.todo { color: #94a3b8; border-color: #cbd5e1; }
  `,
})
export class BatchDetail {
  // Bound from the :id route param (withComponentInputBinding).
  readonly id = input.required<string>();

  private readonly api = inject(BatchApi);
  private readonly pollMs = inject(POLL_INTERVAL_MS);

  protected readonly error = signal<ApiError | null>(null);
  protected readonly notFound = computed(() => this.error()?.status === 404);

  protected readonly batch = toSignal<PayrollBatchDetail | null>(
    toObservable(this.id).pipe(
      switchMap((id) =>
        defer(() => this.api.get(id)).pipe(
          tap(() => this.error.set(null)),
          catchError((err: ApiError) => {
            this.error.set(err);
            return EMPTY;
          }),
          // A completed notifier ends the loop: no point retrying a batch that doesn't exist.
          repeat({ delay: () => (this.notFound() ? EMPTY : timer(this.pollMs)) }),
          // Inclusive: emit the terminal state, then stop polling.
          takeWhile((b) => ACTIVE_STATUSES.includes(b.status), true),
        ),
      ),
    ),
    { initialValue: null },
  );
}

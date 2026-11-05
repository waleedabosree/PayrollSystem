import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { BatchApi } from '../../core/batch-api';
import { ApiError, ValidationIssue } from '../../core/models';
import { Notifications } from '../../core/notifications';

// Client-side checks are a courtesy for fast feedback; the API re-validates everything.
const MAX_BYTES = 10 * 1024 * 1024;

@Component({
  selector: 'app-upload',
  imports: [FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card">
      <h2>Upload payroll file</h2>

      <form (ngSubmit)="submit()" #f="ngForm">
        <label>
          Entity ID
          <input name="entityId" [(ngModel)]="entityId" required maxlength="64" [disabled]="uploading()" />
        </label>

        <div
          class="drop"
          [class.over]="dragging()"
          (dragover)="$event.preventDefault(); dragging.set(true)"
          (dragleave)="dragging.set(false)"
          (drop)="onDrop($event)"
        >
          <input id="file" type="file" accept=".csv,text/csv" (change)="onPick($event)" [disabled]="uploading()" />
          <p>
            @if (file(); as f) {
              <strong>{{ f.name }}</strong> ({{ (f.size / 1024).toFixed(1) }} KB)
            } @else {
              Drop a CSV here or choose a file (max 10 MB, synthetic data only)
            }
          </p>
        </div>

        @if (uploading()) {
          <div class="bar" role="progressbar" [attr.aria-valuenow]="progress()" aria-valuemin="0" aria-valuemax="100">
            <div [style.width.%]="progress()"></div>
          </div>
          <small>{{ progress() < 100 ? 'Uploading… ' + progress() + '%' : 'Validating and queuing…' }}</small>
        }

        <button type="submit" [disabled]="uploading() || !file() || !entityId.trim()">Upload</button>
      </form>

      @if (problem(); as p) {
        <div class="problem" role="alert">
          <strong>{{ p.detail }}</strong>
          @if (p.errors.length) {
            <ul>
              @for (e of issues(p); track $index) {
                <li>{{ describe(e) }}</li>
              }
            </ul>
          }
          @if (p.correlationId) {
            <small>Correlation ID: <code>{{ p.correlationId }}</code></small>
          }
        </div>
      }

      @if (lastBatchId(); as id) {
        <p class="ok">Queued. <a [routerLink]="['/batches', id]">Track batch {{ id.slice(0, 8) }}…</a></p>
      }
    </section>
  `,
  styles: `
    form { display: grid; gap: 12px; }
    label { display: grid; gap: 4px; font-size: 14px; font-weight: 600; }
    input[name=entityId] { padding: 8px; border: 1px solid #cbd5e1; border-radius: 6px; font: inherit; }
    .drop { border: 2px dashed #94a3b8; border-radius: 8px; padding: 16px; text-align: center; }
    .drop.over { background: #eff6ff; border-color: #2563eb; }
    .drop p { margin: 8px 0 0; color: #475569; }
    .bar { height: 8px; background: #e2e8f0; border-radius: 4px; overflow: hidden; }
    .bar div { height: 100%; background: #2563eb; transition: width 0.15s; }
    button { justify-self: start; padding: 8px 20px; border: 0; border-radius: 6px; background: #2563eb; color: #fff; font-weight: 600; cursor: pointer; }
    button:disabled { background: #94a3b8; cursor: not-allowed; }
    .problem { margin-top: 12px; padding: 12px; background: #fef2f2; border: 1px solid #fecaca; border-radius: 6px; color: #991b1b; display: grid; gap: 6px; }
    .problem ul { margin: 0; padding-left: 18px; }
    .ok { color: #166534; }
  `,
})
export class Upload {
  private readonly api = inject(BatchApi);
  private readonly notifications = inject(Notifications);

  protected entityId = 'ACME-01';
  protected readonly file = signal<File | null>(null);
  protected readonly dragging = signal(false);
  protected readonly uploading = signal(false);
  protected readonly progress = signal(0);
  protected readonly problem = signal<ApiError | null>(null);
  protected readonly lastBatchId = signal<string | null>(null);

  protected onPick(event: Event) {
    this.setFile((event.target as HTMLInputElement).files?.[0] ?? null);
  }

  protected onDrop(event: DragEvent) {
    event.preventDefault();
    this.dragging.set(false);
    this.setFile(event.dataTransfer?.files?.[0] ?? null);
  }

  protected issues(p: ApiError): ValidationIssue[] {
    // A 200-row file with every row bad would otherwise render 200 list items.
    return p.errors.slice(0, 10);
  }

  protected describe(e: ValidationIssue): string {
    const where = e.rowNumber ? `Row ${e.rowNumber}${e.field ? ` / ${e.field}` : ''}: ` : '';
    return `${where}${e.message}`;
  }

  protected submit() {
    const file = this.file();
    if (!file || this.uploading()) return;

    this.problem.set(null);
    this.lastBatchId.set(null);
    this.uploading.set(true);
    this.progress.set(0);

    this.api.upload(this.entityId.trim(), file).subscribe({
      next: (event) => {
        if (event.kind === 'progress') {
          this.progress.set(event.percent);
          return;
        }
        this.uploading.set(false);
        this.lastBatchId.set(event.result.batchId);
        this.file.set(null);
        this.api.changed.update((n) => n + 1);
        this.notifications.success(
          'File accepted and queued',
          `${event.result.validatedRows} rows passed structure checks.`,
        );
      },
      error: (err: ApiError) => {
        this.uploading.set(false);
        this.problem.set(err);
        this.notifications.error('Upload failed', err.correlationId ? `Correlation ID: ${err.correlationId}` : undefined);
      },
    });
  }

  private setFile(file: File | null) {
    this.problem.set(null);
    if (!file) return;

    if (!file.name.toLowerCase().endsWith('.csv')) {
      this.file.set(null);
      this.notifications.error('Only .csv files are accepted');
      return;
    }
    if (file.size > MAX_BYTES) {
      this.file.set(null);
      this.notifications.error('File is larger than 10 MB');
      return;
    }
    this.file.set(file);
  }
}

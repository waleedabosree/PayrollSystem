import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Notifications } from '../core/notifications';

@Component({
  selector: 'app-toasts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toasts" aria-live="polite">
      @for (t of notifications.toasts(); track t.id) {
        <div class="toast" [class]="t.kind" role="status">
          <div>
            <strong>{{ t.message }}</strong>
            @if (t.detail) {
              <div class="detail">{{ t.detail }}</div>
            }
          </div>
          <button type="button" aria-label="Dismiss" (click)="notifications.dismiss(t.id)">×</button>
        </div>
      }
    </div>
  `,
  styles: `
    .toasts { position: fixed; top: 16px; right: 16px; display: flex; flex-direction: column; gap: 8px; z-index: 1000; max-width: min(420px, calc(100vw - 32px)); }
    .toast { display: flex; justify-content: space-between; gap: 12px; padding: 12px 14px; border-radius: 8px; color: #fff; box-shadow: 0 4px 14px rgb(0 0 0 / 0.2); }
    .toast.success { background: #15803d; }
    .toast.error { background: #b91c1c; }
    .toast.info { background: #1d4ed8; }
    .detail { font-size: 13px; margin-top: 4px; opacity: 0.95; word-break: break-word; }
    button { background: none; border: 0; color: inherit; font-size: 20px; cursor: pointer; line-height: 1; }
  `,
})
export class Toasts {
  protected readonly notifications = inject(Notifications);
}

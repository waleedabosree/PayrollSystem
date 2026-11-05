import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Notifications } from '../core/notifications';

/** The id support needs to find this request across API, Function and Application Insights logs. */
@Component({
  selector: 'app-correlation-id',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <code>{{ value() }}</code>
    <button type="button" class="copy" (click)="copy()" [attr.aria-label]="'Copy correlation id'">
      {{ copied() ? 'Copied' : 'Copy' }}
    </button>
  `,
  styles: `
    code { font-size: 12px; background: #f1f5f9; padding: 2px 6px; border-radius: 4px; word-break: break-all; }
    .copy { margin-left: 6px; font-size: 12px; border: 1px solid #cbd5e1; background: #fff; border-radius: 4px; cursor: pointer; padding: 1px 8px; }
  `,
})
export class CorrelationId {
  readonly value = input.required<string>();
  protected readonly copied = signal(false);
  private readonly notifications = inject(Notifications);

  protected async copy() {
    try {
      await navigator.clipboard.writeText(this.value());
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    } catch {
      this.notifications.error('Could not copy to clipboard', this.value());
    }
  }
}

import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { BatchStatus } from '../core/models';

@Component({
  selector: 'app-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class]="'s-' + status().toLowerCase()">{{ label() }}</span>`,
  styles: `
    .badge { display: inline-block; padding: 2px 10px; border-radius: 999px; font-size: 12px; font-weight: 600; }
    .s-completed { background: #dcfce7; color: #166534; }
    .s-failed, .s-deadlettered { background: #fee2e2; color: #991b1b; }
    .s-processing { background: #dbeafe; color: #1e40af; }
    .s-queued, .s-pending { background: #f1f5f9; color: #475569; }
    .s-retryable { background: #fef3c7; color: #92400e; }
  `,
})
export class StatusBadge {
  readonly status = input.required<BatchStatus>();
  protected label = () => (this.status() === 'DeadLettered' ? 'Dead-lettered' : this.status());
}

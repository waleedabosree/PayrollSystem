import { Injectable, signal } from '@angular/core';

export type ToastKind = 'success' | 'error' | 'info';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
  detail?: string;
}

@Injectable({ providedIn: 'root' })
export class Notifications {
  private nextId = 1;
  readonly toasts = signal<Toast[]>([]);

  success(message: string, detail?: string) {
    this.push('success', message, detail, 5000);
  }

  info(message: string, detail?: string) {
    this.push('info', message, detail, 5000);
  }

  // Errors stay until dismissed: they usually carry a correlation id the user needs to copy.
  error(message: string, detail?: string) {
    this.push('error', message, detail, 0);
  }

  dismiss(id: number) {
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }

  private push(kind: ToastKind, message: string, detail: string | undefined, ttlMs: number) {
    const id = this.nextId++;
    this.toasts.update((list) => [...list, { id, kind, message, detail }]);
    if (ttlMs > 0) setTimeout(() => this.dismiss(id), ttlMs);
  }
}

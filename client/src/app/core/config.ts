import { InjectionToken } from '@angular/core';

// Environment-specific values go through DI so tests (and a later environment.ts / runtime
// config.json for deployed hosting) can swap them without touching components.
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => 'http://localhost:5088',
});

export const POLL_INTERVAL_MS = new InjectionToken<number>('POLL_INTERVAL_MS', {
  providedIn: 'root',
  factory: () => 3000,
});

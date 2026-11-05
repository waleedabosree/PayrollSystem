// Mirrors src/Payroll.Api/Contracts/PayrollBatchDtos.cs. The API serialises enums by name.
export const BATCH_STATUSES = [
  'Pending',
  'Queued',
  'Processing',
  'Completed',
  'Failed',
  'Retryable',
  'DeadLettered',
] as const;

export type BatchStatus = (typeof BATCH_STATUSES)[number];

// Statuses that will still change on their own: the dashboard keeps polling while any are visible.
export const ACTIVE_STATUSES: readonly BatchStatus[] = ['Pending', 'Queued', 'Processing', 'Retryable'];

export interface PayrollBatchSummary {
  id: string;
  entityId: string;
  originalFileName: string;
  fileSizeBytes: number;
  status: BatchStatus;
  correlationId: string;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  processingDurationMs: number | null;
  processedRows: number | null;
  failureStage: string | null;
}

export interface PayrollBatchDetail {
  id: string;
  entityId: string;
  originalFileName: string;
  fileSizeBytes: number;
  blobContainer: string | null;
  blobName: string | null;
  correlationId: string;
  status: BatchStatus;
  expectedRows: number | null;
  processedRows: number | null;
  deliveryCount: number;
  failureStage: string | null;
  failureReason: string | null;
  createdAtUtc: string;
  queuedAtUtc: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  updatedAtUtc: string | null;
  processingDurationMs: number | null;
}

export interface PagedResponse<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
  hasNext: boolean;
  hasPrevious: boolean;
}

export interface BatchQuery {
  status?: BatchStatus | '';
  search?: string;
  entityId?: string;
  page: number;
  pageSize: number;
}

export interface UploadAccepted {
  batchId: string;
  correlationId: string;
  status: BatchStatus;
  validatedRows: number;
  acceptedAtUtc: string;
}

export interface ValidationIssue {
  code: string;
  message: string;
  field?: string | null;
  rowNumber?: number | null;
}

/** Normalised application/problem+json, so components never parse HTTP errors themselves. */
export interface ApiError {
  status: number;
  title: string;
  detail: string;
  code?: string;
  correlationId?: string;
  errors: ValidationIssue[];
}

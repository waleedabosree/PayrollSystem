import { ChangeDetectionStrategy, Component } from '@angular/core';
import { BatchList } from './batches/batch-list';
import { Upload } from './upload/upload';

@Component({
  selector: 'app-dashboard',
  imports: [Upload, BatchList],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<app-upload /><app-batch-list />`,
  styles: `:host { display: grid; gap: 16px; }`,
})
export class Dashboard {}

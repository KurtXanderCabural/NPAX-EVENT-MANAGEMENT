import { Component, DestroyRef, computed, inject, input, model, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { concatMap, finalize, from } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { MailAttachmentDto, MailConversationTarget } from '../../core/models';

const MAX_ATTACHMENT_BYTES = 25_000_000;
export const ATTACHMENT_LIMIT_MESSAGE = 'Attached files and inline images must total 25 MB or less per email.';

export function attachmentLimitExceeded(files: MailAttachmentDto[], inlineBytes: number): boolean {
  return files.reduce((sum, file) => sum + file.size, inlineBytes) > MAX_ATTACHMENT_BYTES;
}

@Component({
  selector: 'app-mail-attachments',
  imports: [MatButtonModule, MatIconModule],
  template: `
    @if (!readonly()) {
      <div class="attachment-actions">
        <button mat-stroked-button type="button" [disabled]="disabled() || uploading()" (click)="picker.click()">
          <mat-icon>attach_file</mat-icon>{{ uploading() ? 'Uploading…' : 'Attach files' }}
        </button>
        <input #picker type="file" multiple hidden (change)="pick($event)" />
        <span class="attachment-size">{{ sizeLabel(totalBytes()) }} / 25 MB total</span>
      </div>
    }
    @if (files().length) {
      <ul class="attachment-list" aria-label="Attached files">
        @for (file of files(); track file.id) {
          <li>
            <button mat-button type="button" class="attachment-name" (click)="download(file)" [title]="file.fileName">
              <mat-icon>description</mat-icon>{{ file.fileName }}
            </button>
            <span class="attachment-size">{{ sizeLabel(file.size) }}</span>
            @if (!readonly()) {
              <button mat-icon-button type="button" [disabled]="disabled() || uploading()"
                      [attr.aria-label]="'Remove ' + file.fileName" (click)="remove(file.id)">
                <mat-icon>close</mat-icon>
              </button>
            }
          </li>
        }
      </ul>
    }
    @if (error() || (!readonly() && overLimit())) {
      <p class="attachment-error" role="alert">{{ error() || limitMessage }}</p>
    }
  `,
  styles: `
    :host { display: block; }
    .attachment-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .attachment-list { display: flex; flex-wrap: wrap; gap: 6px; margin: 8px 0 0; padding: 0; list-style: none; }
    .attachment-list li { display: flex; align-items: center; min-width: 0; max-width: 100%;
      border: 1px solid var(--mat-sys-outline-variant); border-radius: 8px; padding: 0 6px; }
    .attachment-name { min-width: 0; height: auto; overflow-wrap: anywhere; text-align: start; }
    .attachment-size { flex-shrink: 0; color: var(--mat-sys-on-surface-variant); font-size: .8rem; }
    .attachment-error { color: var(--mat-sys-error); margin: 6px 0 0; }
  `,
})
export class MailAttachmentsComponent {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly files = model<MailAttachmentDto[]>([]);
  readonly uploading = model(false);
  readonly inlineBytes = input(0);
  readonly disabled = input(false);
  readonly readonly = input(false);
  readonly conversation = input<MailConversationTarget>();
  readonly error = signal('');
  readonly totalBytes = computed(() => this.files().reduce((sum, file) => sum + file.size, this.inlineBytes()));
  readonly overLimit = computed(() => this.totalBytes() > MAX_ATTACHMENT_BYTES);
  readonly limitMessage = ATTACHMENT_LIMIT_MESSAGE;

  pick(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    if (!files.length || this.uploading() || this.disabled()) return;
    this.error.set('');
    if (files.some((file) => file.size === 0)) {
      this.error.set('Empty files cannot be attached.');
      return;
    }
    if (files.reduce((total, file) => total + file.size, this.totalBytes()) > MAX_ATTACHMENT_BYTES) {
      this.error.set(this.limitMessage);
      return;
    }

    this.uploading.set(true);
    from(files).pipe(
      concatMap((file) => this.api.uploadMailAttachment(file, this.conversation())),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.uploading.set(false)),
    ).subscribe({
      next: (file) => this.files.update((files) => [...files, file]),
      error: (response) => this.error.set(response?.error?.message || 'Could not attach a file. Try again.'),
    });
  }

  remove(id: string): void {
    this.files.update((files) => files.filter((file) => file.id !== id));
    this.error.set('');
  }

  download(file: MailAttachmentDto): void {
    this.api.downloadMailAttachment(file.id, this.conversation()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe((data) => {
      const url = URL.createObjectURL(data);
      const link = document.createElement('a');
      link.href = url;
      link.download = file.fileName;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    });
  }

  sizeLabel(bytes: number): string {
    if (bytes < 1000) return `${bytes} B`;
    if (bytes < 1_000_000) return `${(bytes / 1000).toFixed(1)} KB`;
    return `${(bytes / 1_000_000).toFixed(2)} MB`;
  }
}

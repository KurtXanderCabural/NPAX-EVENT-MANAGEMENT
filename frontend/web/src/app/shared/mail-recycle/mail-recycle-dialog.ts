import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { finalize } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ConfirmService } from '../../core/confirm.service';
import { RecycledMail } from '../../core/models';
import { ToastService } from '../../core/toast.service';
import { mailRecycleError } from './mail-recycle-error';

export interface MailRecycleDialogData {
  eventId?: number;
}

@Component({
  selector: 'app-mail-recycle-dialog',
  imports: [DatePipe, MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>{{ data.eventId === undefined ? 'Mail Planner Recycle' : 'Invitation Recycle' }}</h2>
    <mat-dialog-content>
      @if (loading()) {
        <p>Loading deleted emails…</p>
      } @else if (loadFailed()) {
        <p role="alert">{{ loadFailed() }}</p>
        <button mat-button type="button" (click)="load()">Try again</button>
      } @else {
        @for (item of items(); track item.id) {
          <article class="recycle-row">
            <div>
              <strong>{{ item.subject || '(No subject)' }}</strong>
              <small>{{ item.messageAt | date: 'MMM d, y, h:mm a' : '+0800' }} PHT
                @if (item.eventName) { · {{ item.eventName }} }
              </small>
              <small>Deleted {{ item.recycledAt | date: 'MMM d, y, h:mm a' : '+0800' }} PHT</small>
            </div>
            <button mat-stroked-button type="button" (click)="restore(item)" [disabled]="busy()"
                    [attr.aria-label]="'Restore ' + (item.subject || 'email')">Restore</button>
          </article>
        } @empty {
          <p>Recycle is empty.</p>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-stroked-button type="button" (click)="empty()" [disabled]="busy() || loading() || loadFailed() || !items().length">
        Delete all permanently
      </button>
      <button mat-button mat-dialog-close [disabled]="busy()">Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .recycle-row { display: flex; align-items: center; justify-content: space-between; gap: 12px;
      padding: 10px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .recycle-row div { overflow-wrap: anywhere; }
    small { display: block; color: var(--mat-sys-on-surface-variant); }
  `,
})
export class MailRecycleDialogComponent {
  readonly data = inject<MailRecycleDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialogRef = inject(MatDialogRef<MailRecycleDialogComponent>);
  readonly items = signal<RecycledMail[]>([]);
  readonly loading = signal(false);
  readonly loadFailed = signal('');
  readonly busy = signal(false);

  constructor() { this.load(); }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set('');
    this.api.recycledEmails(this.data.eventId).pipe(
      takeUntilDestroyed(this.destroyRef), finalize(() => this.loading.set(false)),
    ).subscribe({
      next: (items) => this.items.set(items),
      error: (error) => this.loadFailed.set(mailRecycleError(error, 'Could not load Recycle.')),
    });
  }

  restore(item: RecycledMail): void {
    if (this.busy()) return;
    this.setBusy(true);
    this.api.restoreRecycledEmail(item.id, this.data.eventId).pipe(
      takeUntilDestroyed(this.destroyRef), finalize(() => this.setBusy(false)),
    ).subscribe({
      next: () => {
        this.items.update((items) => items.filter((entry) => entry.id !== item.id));
        this.toast.success('Email restored.');
      },
      error: (error) => {
        this.toast.error(mailRecycleError(error, 'Could not restore this email.'));
        this.load();
      },
    });
  }

  async empty(): Promise<void> {
    if (this.busy() || !this.items().length) return;
    const confirmed = await this.confirm.confirm({
      title: 'Empty Recycle?',
      message: 'This permanently deletes all emails currently in this Recycle, including their conversations. They cannot be restored.',
      confirmText: 'Delete permanently',
      cancelText: 'Keep emails',
      tone: 'error',
    });
    if (!confirmed || this.busy() || this.destroyRef.destroyed) return;
    this.setBusy(true);
    this.api.emptyMailRecycle(this.data.eventId).pipe(
      takeUntilDestroyed(this.destroyRef), finalize(() => this.setBusy(false)),
    ).subscribe({
      next: ({ count }) => {
        this.toast.success(`${count} email(s) permanently deleted.`);
        this.load();
      },
      error: (error) => {
        this.toast.error(mailRecycleError(error, 'Could not empty Recycle.'));
        this.load();
      },
    });
  }

  private setBusy(busy: boolean): void {
    this.busy.set(busy);
    this.dialogRef.disableClose = busy;
  }
}

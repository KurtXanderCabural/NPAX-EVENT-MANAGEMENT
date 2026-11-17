import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatAutocompleteModule, MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { finalize, interval, of, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ALL_USERS, loadSavedRecipients, recipientAddress } from '../../core/invitation.util';
import { ContactDto, MailAttachmentDto, MailConversationDto, MailConversationReplyDto, MailConversationTarget } from '../../core/models';
import { ATTACHMENT_LIMIT_MESSAGE, MailAttachmentsComponent, attachmentLimitExceeded } from '../mail-attachments/mail-attachments';
import { RichTextEditorComponent } from '../rich-text/rich-text-editor';
import { RichTextViewComponent } from '../rich-text/rich-text-view';
import { richTextInfo, toXhtml } from '../rich-text/rich-text';

export type MailConversationAction = 'reply' | 'forward' | 'react';

export interface MailConversationDialogData extends MailConversationTarget {
  subject: string;
  action?: MailConversationAction;
}

@Component({
  selector: 'app-mail-conversation-dialog',
  imports: [DatePipe, NgTemplateOutlet, MatAutocompleteModule, MatButtonModule, MatChipsModule,
    MatDialogModule, MatFormFieldModule, MatIconModule, MatMenuModule,
    RichTextViewComponent, RichTextEditorComponent, MailAttachmentsComponent],
  template: `
    <h2 mat-dialog-title>{{ data.subject }}</h2>
    <mat-dialog-content>
      @if (thread(); as conversation) {
        @if (!conversation.supportsRichActions) {
          <p role="alert" class="error">{{ apiUpdateMessage }}</p>
        }
        <ng-template #actions let-message>
          <div class="mail-actions">
            <button type="button" mat-stroked-button (click)="choose('reply', message)" [disabled]="working()">
              <mat-icon>reply</mat-icon>Reply
            </button>
            @if (adminActions) {
              <button type="button" mat-stroked-button (click)="choose('forward', message)" [disabled]="working()">
                <mat-icon>forward</mat-icon>Forward
              </button>
              <button type="button" mat-icon-button aria-label="React with emoji"
                      [matMenuTriggerFor]="emojiMenu" [disabled]="working() || !conversation.supportsRichActions">
                <mat-icon>sentiment_satisfied</mat-icon>
              </button>
              <mat-menu #emojiMenu="matMenu">
                @for (emoji of emojis; track emoji.value) {
                  <button mat-menu-item (click)="react(emoji.value, message)">{{ emoji.value }} {{ emoji.label }}</button>
                }
              </mat-menu>
            }
          </div>
        </ng-template>
        @if (conversation.bodyHtml) {
          <details class="original">
            <summary>Original message</summary>
            <app-rich-text-view [html]="conversation.bodyHtml" />
          </details>
        }
        <ng-container [ngTemplateOutlet]="actions" [ngTemplateOutletContext]="{ $implicit: null }" />
        <section aria-label="Conversation replies" class="replies">
          @for (reply of conversation.replies; track reply.id) {
            <article>
              <div class="reply-meta">
                <strong>{{ reply.authorName }}</strong>
                <span>{{ reply.createdAt | date: 'MMM d, h:mm a' : '+0800' }}</span>
                @if (reply.isPrivate) { <span>Private</span> }
                @if (reply.kind === 'Forward') { <span>Forwarded</span> }
                @if (reply.kind === 'Reaction') { <span>Reaction</span> }
                @if (reply.status !== 'Sent') { <span>{{ reply.status }}</span> }
              </div>
              @if (reply.bodyHtml) {
                <app-rich-text-view [html]="reply.bodyHtml" />
              } @else {
                <p>{{ reply.body }}</p>
              }
              <app-mail-attachments [files]="reply.attachments ?? []" [readonly]="true" [conversation]="data" />
              @if (reply.status === 'Sent' && reply.kind !== 'Forward') {
                <ng-container [ngTemplateOutlet]="actions" [ngTemplateOutletContext]="{ $implicit: reply }" />
              }
            </article>
          } @empty {
            <p>No replies yet.</p>
          }
        </section>
        @if (mode() === 'react') {
          <div class="mail-actions" aria-label="Choose a reaction for the original message">
            @for (emoji of emojis; track emoji.value) {
              <button type="button" mat-stroked-button [attr.aria-label]="emoji.label"
                      [disabled]="working() || !conversation.supportsRichActions"
                      (click)="react(emoji.value, null)">{{ emoji.value }}</button>
            }
          </div>
          <p class="recipient-hint">Your reaction will be emailed to the original recipients.</p>
        } @else {
        <form (submit)="$event.preventDefault(); send()">
          @if (mode() === 'forward') {
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>To</mat-label>
              <mat-chip-grid #recipientGrid aria-label="Forward recipients" [disabled]="sending()">
                @for (address of forwardRecipients(); track address) {
                  <mat-chip-row (removed)="removeForwardRecipient(address)">
                    {{ forwardLabel(address) }}
                    <button matChipRemove type="button" [attr.aria-label]="'Remove ' + address"><mat-icon>cancel</mat-icon></button>
                  </mat-chip-row>
                }
                <input #recipientInput #recipientTrigger="matAutocompleteTrigger" [matChipInputFor]="recipientGrid"
                       aria-label="Forward to" [disabled]="sending()"
                       [matAutocomplete]="recipientOptions" inputmode="email" autocomplete="off" [value]="forwardQuery()"
                       placeholder="Type an address or pick a saved one"
                       (input)="forwardQuery.set(recipientInput.value)"
                       (keydown)="forwardKey($event, recipientTrigger)"
                       (paste)="pasteForwardRecipients($event)" />
              </mat-chip-grid>
              <mat-autocomplete #recipientOptions="matAutocomplete" (optionSelected)="addForwardRecipients($event.option.value)">
                @for (address of forwardSuggestions(); track address) {
                  <mat-option [value]="address">{{ forwardLabel(address) }}</mat-option>
                }
              </mat-autocomplete>
            </mat-form-field>
            <p class="recipient-hint">The {{ parent() ? 'selected reply' : 'original message' }} and its attached files will be included.</p>
          } @else {
            <p class="recipient-hint">{{ recipientLabel(conversation) }}</p>
          }
          <div [attr.inert]="sending() ? '' : null">
            <app-rich-text-editor [value]="body()" (valueChange)="body.set($event)"
                                  [label]="mode() === 'forward' ? 'Forward message' : 'Reply'"
                                  [placeholder]="mode() === 'forward' ? 'Add an optional message.' : 'Write your reply here.'" />
            <app-mail-attachments [files]="attachments()" (filesChange)="attachments.set($event)"
                                  (uploadingChange)="uploading.set($event)" [inlineBytes]="bodyInfo().imageBytes"
                                  [disabled]="sending()" [conversation]="data" />
          </div>
          @if (parent() && mode() === 'reply') {
            <p class="recipient-hint">Replying to {{ parent()?.authorName }} · {{ parent()?.createdAt | date: 'MMM d, h:mm a' : '+0800' }}</p>
            <button type="button" mat-button (click)="choose('reply', null)" [disabled]="working()">Reply to original</button>
          }
          <button type="submit" mat-flat-button [disabled]="!canSend()">
            <mat-icon>send</mat-icon>{{ sending() ? 'Queuing…' : mode() === 'forward' ? 'Send forward' : 'Send reply' }}
          </button>
        </form>
        }
      } @else if (!loadError()) {
        <p>Loading conversation…</p>
      }
      @if (loadError()) {
        <p role="alert" class="error">{{ loadError() }}</p>
        <button type="button" mat-button (click)="load()">Try again</button>
      }
      @if (sendError()) { <p role="alert" class="error">{{ sendError() }}</p> }
      @if (sendStatus()) { <p role="status">{{ sendStatus() }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .replies { display: grid; gap: 10px; margin-bottom: 14px; }
    .original { margin-bottom: 12px; }
    article { border-bottom: 1px solid var(--mat-sys-outline-variant); padding-block: 8px; }
    article p { white-space: pre-wrap; overflow-wrap: anywhere; }
    .reply-meta { display: flex; flex-wrap: wrap; gap: 6px 12px; }
    .reply-meta span { color: var(--mat-sys-on-surface-variant); }
    form { display: grid; gap: 8px; }
    form button { justify-self: start; }
    .recipient-hint { margin: 0; color: var(--mat-sys-on-surface-variant); }
    .mail-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-block: 8px; }
    mat-chip-row { max-width: 100%; }
    .error { color: var(--mat-sys-error); }
  `,
})
export class MailConversationDialogComponent {
  readonly data = inject<MailConversationDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  readonly thread = signal<MailConversationDto | null>(null);
  readonly apiUpdateMessage = 'Update the mail server to send replies, forwards, and emoji reactions.';
  readonly parent = signal<MailConversationReplyDto | null>(null);
  readonly adminActions = this.data.source !== 'inbox';
  readonly mode = signal<MailConversationAction>(this.adminActions ? this.data.action ?? 'reply' : 'reply');
  readonly forwardQuery = signal('');
  readonly forwardRecipients = signal<string[]>([]);
  private readonly recipientInput = viewChild<ElementRef<HTMLInputElement>>('recipientInput');
  private readonly contacts = signal(new Map<string, ContactDto>());
  private readonly savedRecipients = loadSavedRecipients(this.auth.auth()?.userId);
  private requestedContacts = false;
  readonly forwardSuggestions = computed(() => {
    const query = this.forwardQuery().trim().toLowerCase();
    const addresses = new Set(this.savedRecipients);
    if (query) for (const address of this.contacts().keys()) addresses.add(address);
    const selected = new Set(this.forwardRecipients());
    return [...addresses].filter((address) => !selected.has(address) &&
      (address.includes(query) || this.contacts().get(address)?.fullName.toLowerCase().includes(query))).slice(0, 20);
  });
  readonly emojis = [
    { value: '👍', label: 'Thumbs up' }, { value: '❤️', label: 'Heart' }, { value: '😊', label: 'Smile' },
    { value: '🎉', label: 'Celebrate' }, { value: '😮', label: 'Surprised' }, { value: '🙏', label: 'Thanks' },
  ];
  readonly sending = signal(false);
  readonly loadError = signal('');
  readonly sendError = signal('');
  readonly sendStatus = signal('');
  readonly body = signal('');
  readonly bodyInfo = computed(() => richTextInfo(this.body()));
  readonly attachments = signal<MailAttachmentDto[]>([]);
  readonly uploading = signal(false);
  readonly editor = viewChild(RichTextEditorComponent);
  readonly working = computed(() => this.sending() || this.uploading() || !!this.editor()?.processing());
  readonly canSend = computed(() => this.thread()?.supportsRichActions === true && !this.working() && (this.mode() === 'forward'
    ? this.forwardRecipients().length > 0 || !!this.forwardQuery().trim()
    : !!(this.bodyInfo().text.trim() || this.bodyInfo().images || this.attachments().length)));
  private requestId: string | null = null;
  private requestKey = '';
  private loading = false;

  constructor() {
    this.load();
    if (this.mode() === 'forward') this.loadForwardContacts();
    interval(5_000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

  load(): void {
    if (this.loading) return;
    this.loading = true;
    const previous = this.thread();
    this.api.mailConversation(this.data.source, this.data.id, !previous, !previous)
      .pipe(switchMap((thread) => {
        if (!previous) return of(thread);
        const existing = new Map(previous.replies.map((reply) => [reply.id, reply]));
        if (thread.replies.some((reply) => !existing.has(reply.id))) {
          return this.api.mailConversation(this.data.source, this.data.id, false);
        }
        return of({ ...thread, replies: thread.replies.map((reply) => ({ ...existing.get(reply.id), ...reply })) });
      }), takeUntilDestroyed(this.destroyRef), finalize(() => this.loading = false)).subscribe({
      next: (thread) => {
        this.thread.set({ ...thread, bodyHtml: thread.bodyHtml ?? this.thread()?.bodyHtml ?? '' });
        this.loadError.set('');
      },
      error: () => this.loadError.set('Could not load the conversation.'),
    });
  }

  recipientLabel(conversation: MailConversationDto): string {
    if (this.data.source === 'inbox') return 'To the organizer only';
    if (this.parent()?.isPrivate) return 'To the participant only';
    return `To ${conversation.recipientCount} original email recipient(s)`;
  }

  choose(mode: MailConversationAction, message: MailConversationReplyDto | null): void {
    if (this.working()) return;
    this.mode.set(mode);
    if (mode === 'forward') this.loadForwardContacts();
    this.parent.set(message);
    this.sendError.set('');
    this.sendStatus.set('');
  }

  private loadForwardContacts(): void {
    if (this.requestedContacts) return;
    this.requestedContacts = true;
    this.api.listContacts().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (contacts) => {
        const directory = new Map<string, ContactDto>();
        for (const contact of contacts) {
          const address = recipientAddress(contact.email);
          if (address && address !== ALL_USERS) directory.set(address, contact);
        }
        this.contacts.set(directory);
      },
      error: () => { this.requestedContacts = false; },
    });
  }

  forwardLabel(address: string): string {
    const name = this.contacts().get(address)?.fullName;
    return name ? `${name} <${address}>` : address;
  }

  addForwardRecipients(value: string): boolean {
    if (this.sending()) return false;
    const addresses: string[] = [];
    for (const entry of value.split(/[\s,;]+/).filter(Boolean)) {
      const address = recipientAddress(entry);
      if (!address || address === ALL_USERS) {
        this.sendError.set('Enter valid email addresses for the forward.');
        return false;
      }
      addresses.push(address);
    }
    const recipients = [...new Set([...this.forwardRecipients(), ...addresses])];
    if (recipients.length > 500) {
      this.sendError.set('Add up to 500 recipients.');
      return false;
    }
    this.forwardRecipients.set(recipients);
    this.clearForwardQuery();
    this.sendError.set('');
    return true;
  }

  private clearForwardQuery(): void {
    this.forwardQuery.set('');
    const input = this.recipientInput()?.nativeElement;
    if (input) input.value = '';
  }

  removeForwardRecipient(address: string): void {
    if (this.sending()) return;
    this.forwardRecipients.update((recipients) => recipients.filter((recipient) => recipient !== address));
  }

  forwardKey(event: KeyboardEvent, trigger: MatAutocompleteTrigger): void {
    if (!['Enter', ',', ';', 'Tab'].includes(event.key) || !this.forwardQuery().trim()) return;
    if (event.key === 'Enter' && trigger.panelOpen && trigger.activeOption) return;
    if (event.key !== 'Tab') event.preventDefault();
    if (this.addForwardRecipients(this.forwardQuery())) trigger.closePanel();
  }

  pasteForwardRecipients(event: ClipboardEvent): void {
    const value = event.clipboardData?.getData('text/plain') ?? '';
    if (!/[\s,;]/.test(value.trim())) return;
    event.preventDefault();
    const draft = [this.forwardQuery(), value].filter(Boolean).join(',');
    if (!this.addForwardRecipients(draft)) {
      this.forwardQuery.set(draft.replace(/[\r\n]+/g, ','));
    }
  }

  react(emoji: string, message: MailConversationReplyDto | null): void {
    if (this.working()) return;
    this.submit({ parentId: message?.id ?? null, emoji });
  }

  send(): void {
    if (this.mode() === 'react' || !this.canSend()) return;
    if (attachmentLimitExceeded(this.attachments(), this.bodyInfo().imageBytes)) {
      this.sendError.set(ATTACHMENT_LIMIT_MESSAGE);
      return;
    }
    const bodyHtml = toXhtml(this.body());
    const attachmentIds = this.attachments().map((file) => file.id);
    const parentId = this.parent()?.id ?? null;
    let forwardTo: string[] | undefined;
    if (this.mode() === 'forward') {
      if (!this.addForwardRecipients(this.forwardQuery())) return;
      forwardTo = this.forwardRecipients();
      if (!forwardTo.length) {
        this.sendError.set('Add at least one recipient to forward this email.');
        return;
      }
    }
    this.submit({ parentId, bodyHtml, attachmentIds, forwardTo });
  }

  private submit(payload: Omit<Parameters<ApiService['replyToMail']>[2], 'id'>): void {
    if (this.thread()?.supportsRichActions !== true) return;
    const key = JSON.stringify(payload);
    if (!this.requestId || this.requestKey !== key) {
      const bytes = globalThis.crypto.getRandomValues(new Uint8Array(16));
      bytes[6] = (bytes[6] & 15) | 64;
      bytes[8] = (bytes[8] & 63) | 128;
      const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
      this.requestId = [hex.slice(0, 8), hex.slice(8, 12), hex.slice(12, 16), hex.slice(16, 20), hex.slice(20)].join('-');
    }
    this.requestKey = key;
    this.sending.set(true);
    this.sendError.set('');
    this.sendStatus.set('');
    this.api.replyToMail(this.data.source, this.data.id, { id: this.requestId, ...payload })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.sending.set(false)))
      .subscribe({
        next: () => {
          if (!payload.emoji) {
            this.body.set('');
            this.attachments.set([]);
            this.forwardRecipients.set([]);
            this.clearForwardQuery();
            this.parent.set(null);
          }
          this.requestId = null;
          this.sendStatus.set(payload.emoji ? 'Reaction queued.' : payload.forwardTo ? 'Forward queued.' : 'Reply queued.');
          this.load();
        },
        error: (error) => {
          const message = error?.error?.message;
          this.sendError.set(message === 'Enter a reply of up to 4,000 characters.'
            ? this.apiUpdateMessage : message ?? 'Could not queue the email. Try again.');
        },
      });
  }
}

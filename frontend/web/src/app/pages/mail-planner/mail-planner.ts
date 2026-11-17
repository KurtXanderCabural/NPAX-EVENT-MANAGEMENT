import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, catchError, finalize, forkJoin, from, interval, map, of, switchMap, tap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ConfirmService } from '../../core/confirm.service';
import { ALL_USERS, recipientAddress } from '../../core/invitation.util';
import { InboxMailDto, MailAudienceGroup, MailPlanDto, MailPlanSendDto } from '../../core/models';
import { ToastService } from '../../core/toast.service';
import { RichTextViewComponent } from '../../shared/rich-text/rich-text-view';
import type { MailConversationAction, MailConversationDialogData } from '../../shared/mail-conversation/mail-conversation-dialog';
import { MailRecycleDialogComponent } from '../../shared/mail-recycle/mail-recycle-dialog';
import { mailRecycleError } from '../../shared/mail-recycle/mail-recycle-error';
import type { MailPlanDialogComponent, MailPlanDialogData } from './mail-plan-dialog';
import {
  MailPlan,
  PlanStatus,
  PlanSend,
  ReceivedMail,
  ReminderStatus,
  resolveReceivers,
  planStatus,
  toPlan,
  toPlanSend,
  toReceived,
} from './mail-planner.model';
import { ALL_USERS_GROUP, CHANNEL_ICONS, KIND_ICONS, dayKey, shift, sortedSends, startOfToday } from './mail-planner.util';

type Tab = 'plan' | 'reminders';
type ReminderFilter = 'All' | ReminderStatus;
type PlanFilter = 'All' | PlanStatus;

interface QuickPlan {
  label: string;
  icon: string;
  // Only offered for mails that belong to an event.
  needsEvent?: boolean;
  pick: (mail: ReceivedMail) => Date;
}

/** One target day of a plan, as shown on the day timeline. */
interface DayItem {
  plan: MailPlan;
  send: PlanSend;
  // 1-based position of this day within the plan's days.
  index: number;
  total: number;
  receivers: number;
}

/**
 * Mail Planner: organizers create mail plans — the days they go out, each day's mail and a
 * receiver list — which the server sends on schedule; receivers turn the mails they got into
 * reminders. Lists hold message previews; full bodies (with images) load when expanded.
 */
@Component({
  selector: 'app-mail-planner',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatTooltipModule, RichTextViewComponent],
  templateUrl: './mail-planner.html',
  styleUrl: './mail-planner.scss',
})
export class MailPlannerComponent {
  protected auth = inject(AuthService);
  private api = inject(ApiService);
  private confirm = inject(ConfirmService);
  private dialog = inject(MatDialog);
  private toast = inject(ToastService);
  private destroyRef = inject(DestroyRef);

  readonly kindIcons = KIND_ICONS;
  readonly channelIcons = CHANNEL_ICONS;
  readonly reminderFilters: ReminderFilter[] = ['All', 'Due today', 'Upcoming', 'Not planned', 'Snoozed', 'Done'];

  readonly tab = signal<Tab>(this.auth.isAdmin() ? 'plan' : 'reminders');
  readonly loading = signal(true);
  readonly loadError = signal('');
  // Plan or reminder action in progress, so buttons can't be double-clicked.
  readonly busy = signal(false);

  // ---- Send plan (organizer) ----
  readonly plans = signal<MailPlan[]>([]);
  readonly planFilters: readonly PlanFilter[] = ['All', 'Draft', 'Scheduled', 'Sending', 'Queued', 'Failed', 'Completed'];
  readonly planFilter = signal<PlanFilter>('All');
  readonly planStatus = planStatus;
  readonly visiblePlans = computed(() => this.plans().filter((p) => this.matchesPlanFilter(p, this.planFilter())));
  readonly groups = signal<MailAudienceGroup[]>([]);
  readonly events = signal<string[]>([]);
  readonly savedEvents = signal<string[]>([]);
  readonly savedCc = signal<string[]>([]);
  readonly selectedDay = signal(startOfToday());
  readonly week = computed(() => Array.from({ length: 7 }, (_, i) => shift(startOfToday(), i)));

  readonly receiverCounts = computed(() => new Map(this.plans().map((p) => [
    p.id,
    new Set([...resolveReceivers(this.groups(), p.receivers), ...p.receivers.cc.map((email) => email.toLowerCase())]).size,
  ])));

  readonly dayItems = computed<DayItem[]>(() => {
    const key = dayKey(this.selectedDay());
    const items: DayItem[] = [];
    for (const plan of this.visiblePlans()) {
      const sends = sortedSends(plan.sends);
      sends.forEach((send, i) => {
        if (dayKey(send.at) === key)
          items.push({ plan, send, index: i + 1, total: sends.length, receivers: this.receiversOf(plan, send) });
      });
    }
    return items.sort((a, b) => a.send.at.getTime() - b.send.at.getTime());
  });

  // Plans for the side list: upcoming first, then drafts, then finished.
  readonly planList = computed(() => {
    const rank = (p: MailPlan) => (p.draft ? 1 : this.nextSend(p) ? 0 : 2);
    return [...this.visiblePlans()].sort((a, b) => rank(a) - rank(b) || (this.nextSend(a)?.at.getTime() ?? 0) - (this.nextSend(b)?.at.getTime() ?? 0));
  });

  readonly stats = computed(() => {
    const plans = this.plans();
    const live = plans.filter((p) => !p.draft);
    const sends = live.flatMap((p) => p.sends.map((s) => ({ s, n: this.receiverCounts().get(p.id) ?? 0 })));
    const today = dayKey(new Date());
    return {
      active: live.filter((p) => this.nextSend(p)).length,
      today: sends.filter(({ s }) => dayKey(s.at) === today && s.status === 'Scheduled').length,
      drafts: plans.length - live.length,
      deliveries: sends.filter(({ s }) => s.status === 'Scheduled').reduce((sum, { n }) => sum + n, 0),
      failed: sends.filter(({ s }) => s.status === 'Failed').length,
    };
  });

  // ---- My reminders (receiver) ----
  readonly received = signal<ReceivedMail[]>([]);
  readonly reminderFilter = signal<ReminderFilter>('All');

  readonly visibleReceived = computed(() => {
    const f = this.reminderFilter();
    return f === 'All' ? this.received() : this.received().filter((m) => m.status === f);
  });

  readonly reminderCounts = computed(() => {
    const counts: Partial<Record<ReminderFilter, number>> = { All: this.received().length };
    for (const m of this.received()) counts[m.status] = (counts[m.status] ?? 0) + 1;
    return counts;
  });

  // Next reminders in time order, for the agenda side card.
  readonly agenda = computed(() =>
    this.received()
      .filter((m) => m.remindAt && m.status !== 'Done')
      .sort((a, b) => a.remindAt!.getTime() - b.remindAt!.getTime())
      .slice(0, 5),
  );

  readonly quickPlans: QuickPlan[] = [
    { label: 'Tonight 6 PM', icon: 'nights_stay', pick: () => shift(startOfToday(), 0, 18) },
    { label: 'Tomorrow 9 AM', icon: 'wb_sunny', pick: () => shift(startOfToday(), 1, 9) },
    { label: 'Day before event', icon: 'event_upcoming', needsEvent: true, pick: (m) => shift(m.eventAt!, -1, 9) },
    { label: 'Event morning', icon: 'alarm', needsEvent: true, pick: (m) => shift(m.eventAt!, 0, 7) },
    { label: 'In 3 days', icon: 'date_range', pick: () => shift(startOfToday(), 3, 9) },
  ];

  // Expanded messages and their loaded bodies: 's<sendId>' on the timeline, 'r<mailId>' in reminders.
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly bodies = signal<ReadonlyMap<string, string>>(new Map());

  readonly isToday = (d: Date) => dayKey(d) === dayKey(new Date());
  readonly sameDay = (a: Date, b: Date) => dayKey(a) === dayKey(b);

  constructor() {
    this.load();
    // Picks up days the server sent (or failed) and reminders coming due.
    interval(30_000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      if (!this.busy() && document.visibilityState === 'visible') this.refresh();
    });
  }

  load(): void {
    this.loading.set(true);
    const admin = this.auth.isAdmin();
    forkJoin({
      inbox: this.api.mailInbox(),
      plans: admin ? this.api.mailPlans() : of(null),
      options: admin ? this.loadOptions() : of(null),
    })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ inbox, plans }) => {
          this.received.set(inbox.map(toReceived));
          if (plans) this.plans.set(plans.map(toPlan));
          this.loadError.set('');
        },
        error: () => this.loadError.set('The mail planner could not be loaded. Check your connection and try again.'),
      });
  }

  private loadOptions() {
    return this.api.mailPlanOptions().pipe(
      switchMap((options) => {
        const groups = options.groups ?? [];
        const allUsers = groups.find((group) => group.id === ALL_USERS_GROUP);
        if (allUsers?.members) return of({ options, groups });

        // Older APIs still provide the directory used by the invitation composer.
        return this.api.listContacts().pipe(
          catchError(() => of(null)),
          map((contacts) => {
            const names = new Map((contacts ?? []).map((contact) => [contact.email.trim().toLowerCase(), contact.fullName]));
            const directory = allUsers?.emails ?? contacts?.map((contact) => contact.email) ?? [];
            const emails = [...new Set(directory.map(recipientAddress))]
              .filter((email): email is string => email !== null && email !== ALL_USERS);
            const members = emails.map((email) => ({ email, fullName: names.get(email) ?? '' }))
              .sort((a, b) => (a.fullName || a.email).localeCompare(b.fullName || b.email));
            return {
              options,
              groups: [{ id: ALL_USERS_GROUP, name: 'All users', icon: 'groups', emails, members },
                ...groups.filter((group) => group.id !== ALL_USERS_GROUP)],
            };
          }),
        );
      }),
      tap(({ options, groups }) => {
        this.groups.set(groups);
        this.events.set(options.events ?? []);
        this.savedEvents.set(options.savedEvents ?? []);
        this.savedCc.set(options.savedCc ?? []);
      }),
    );
  }

  private refresh(): void {
    if (this.auth.isAdmin())
      this.api.mailPlans().pipe(takeUntilDestroyed(this.destroyRef)).subscribe((plans) => this.plans.set(plans.map(toPlan)));
    this.api.mailInbox().pipe(takeUntilDestroyed(this.destroyRef)).subscribe((inbox) => this.received.set(inbox.map(toReceived)));
  }

  // Days that have gone out show who actually got them; the rest show the list's current size.
  private receiversOf(plan: MailPlan, send: PlanSend): number {
    return send.status === 'Scheduled' ? this.receiverCounts().get(plan.id) ?? 0 : send.recipientCount;
  }

  countOn(day: Date): number {
    const key = dayKey(day);
    return this.visiblePlans().reduce((n, p) => n + p.sends.filter((s) => dayKey(s.at) === key).length, 0);
  }

  selectPlanFilter(value: string): void {
    const filter = this.planFilters.find((item) => item === value);
    if (filter) this.planFilter.set(filter);
  }

  planCount(filter: PlanFilter): number {
    return this.plans().filter((p) => this.matchesPlanFilter(p, filter)).length;
  }

  private matchesPlanFilter(plan: MailPlan, filter: PlanFilter): boolean {
    return filter === 'All' || planStatus(plan) === filter;
  }

  daysUntil(d: Date): number {
    return Math.round((shift(d, 0, 0).getTime() - startOfToday().getTime()) / 86_400_000);
  }

  nextSend(plan: MailPlan): PlanSend | undefined {
    return plan.sends
      .filter((s) => s.status === 'Scheduled' && s.at.getTime() > Date.now())
      .sort((a, b) => a.at.getTime() - b.at.getTime())[0];
  }

  /** A plan is named after its first day's subject. */
  planTitle(plan: MailPlan): string {
    return sortedSends(plan.sends)[0]?.subject ?? 'Untitled plan';
  }

  sentCount(plan: MailPlan): number {
    return plan.sends.filter((s) => s.status === 'Sent').length;
  }

  receiverSummary(plan: MailPlan): string {
    const parts = plan.receivers.groupIds.map((id) => this.groups().find((g) => g.id === id)?.name ?? 'Removed list');
    if (plan.receivers.emails.length) parts.unshift(`${plan.receivers.emails.length} email${plan.receivers.emails.length === 1 ? '' : 's'}`);
    if (plan.receivers.cc.length) parts.push(`CC ${plan.receivers.cc.length}`);
    return parts.join(', ');
  }

  quickPlansFor(mail: ReceivedMail): QuickPlan[] {
    return this.quickPlans.filter((q) => !q.needsEvent || mail.eventAt);
  }

  // Long text or any image gets a "Read full message" toggle.
  isLong(preview: string, images: number): boolean {
    return preview.length > 140 || images > 0;
  }

  /** Expands a message, loading its full body (with images) the first time. */
  toggleBody(key: string, load: () => Observable<{ bodyHtml: string }>): void {
    const open = this.expanded().has(key);
    this.expanded.update((set) => {
      const next = new Set(set);
      if (open) next.delete(key);
      else next.add(key);
      return next;
    });
    if (open || this.bodies().has(key)) return;
    load().pipe(takeUntilDestroyed(this.destroyRef)).subscribe(({ bodyHtml }) =>
      this.bodies.update((map) => new Map(map).set(key, bodyHtml)));
  }

  sendBody(send: PlanSend): () => Observable<{ bodyHtml: string }> {
    return () => this.api.mailPlanSendBody(send.id);
  }

  mailBody(mail: ReceivedMail): () => Observable<{ bodyHtml: string }> {
    return () => this.api.mailInboxBody(mail.id);
  }

  async openConversation(source: 'plan' | 'inbox', message: PlanSend | ReceivedMail, action: MailConversationAction = 'reply'): Promise<void> {
    try {
      const { MailConversationDialogComponent } = await import('../../shared/mail-conversation/mail-conversation-dialog');
      if (this.destroyRef.destroyed) return;
      const data: MailConversationDialogData = { source, id: message.id, subject: message.subject, action };
      this.dialog.open(MailConversationDialogComponent, {
        data,
        width: '640px',
        maxWidth: '94vw',
      });
    } catch {
      this.toast.error('Could not open the conversation. Try again.');
    }
  }

  // ---- Send plan actions ----
  openPlan(plan: MailPlan | null = null): void {
    if (this.busy()) return;
    this.busy.set(true);
    // Load a fresh directory and, when editing, the full message for each day.
    const options = this.loadOptions().pipe(catchError(() => {
      this.toast.warning('Could not refresh the receiver directory.');
      return of(null);
    }));
    forkJoin({
      options,
      full: plan ? this.api.mailPlan(plan.id) : of(null),
      dialog: from(import('./mail-plan-dialog')),
    })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false)))
      .subscribe({
        next: ({ full, dialog }) => this.openDialog(dialog.MailPlanDialogComponent, full ? toPlan(full) : null),
        error: () => this.toast.error('Could not load the plan. Try again.'),
      });
  }

  private openDialog(component: typeof MailPlanDialogComponent, plan: MailPlan | null): void {
    const day = this.selectedDay().getTime() < startOfToday().getTime() ? startOfToday() : this.selectedDay();
    this.dialog
      .open<MailPlanDialogComponent, MailPlanDialogData, MailPlanDto>(component, {
        data: {
          plan,
          groups: this.groups(),
          events: this.events(),
          savedEvents: this.savedEvents(),
          onSaveEvent: (name) => this.savedEvents.update((list) => [...list, name]),
          savedCc: this.savedCc(),
          onSavedCcChange: (list) => this.savedCc.set(list),
          defaultDay: day,
        },
        width: '1040px',
        maxWidth: '96vw',
        maxHeight: '94vh',
        panelClass: 'mail-plan-panel',
        autoFocus: false,
      })
      .afterClosed()
      .subscribe((dto) => {
        if (!dto) return;
        const saved = toPlan(dto);
        this.plans.update((list) => list.some((p) => p.id === saved.id)
          ? list.map((p) => p.id === saved.id ? saved : p)
          : [...list, saved]);
        if (!this.matchesPlanFilter(saved, this.planFilter())) {
          this.planFilter.set(planStatus(saved));
        }
        // Edited days get new bodies; drop the cached copies.
        this.bodies.update((map) => new Map([...map].filter(([key]) => !saved.sends.some((s) => key === 's' + s.id))));
        const first = this.nextSend(saved) ?? saved.sends[0];
        if (first) this.selectedDay.set(shift(first.at, 0, 0));
        const days = saved.sends.filter((s) => s.status === 'Scheduled').length;
        this.toast.success(saved.draft ? 'Plan saved as draft.' : saved.sendImmediately
          ? 'Message queued for immediate delivery.'
          : `Plan scheduled on ${days} day${days === 1 ? '' : 's'}.`);
      });
  }

  async sendNow(item: DayItem): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Send this mail now?',
      message: `"${item.send.subject}" goes out to ${item.receivers} receiver${item.receivers === 1 ? '' : 's'} now instead of ${item.send.at.toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}.`,
      confirmText: 'Send now',
      cancelText: 'Keep schedule',
    });
    if (ok) this.deliver(item, this.api.sendMailPlanNow(item.send.id));
  }

  retry(item: DayItem): void {
    this.deliver(item, this.api.retryMailPlanSend(item.send.id));
  }

  private deliver(item: DayItem, request: Observable<MailPlanSendDto>): void {
    if (this.busy()) return;
    this.busy.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false))).subscribe((dto) => {
      const send = toPlanSend(dto);
      this.plans.update((list) =>
        list.map((p) => (p.id === item.plan.id ? { ...p, sends: p.sends.map((s) => (s.id === send.id ? send : s)) } : p)));
      this.selectedDay.set(shift(send.at, 0, 0));
      if (send.status === 'Sent') this.toast.success(`Sent to ${send.recipientCount} receiver${send.recipientCount === 1 ? '' : 's'}.`);
      else this.toast.warning(send.lastError ?? 'The mail could not be sent yet.', 8000);
    });
  }

  async removeDay(item: DayItem): Promise<void> {
    const last = item.total === 1;
    const ok = await this.confirm.confirm({
      title: last ? 'Move this plan to Recycle?' : 'Move this email to Recycle?',
      message: `"${item.send.subject}" will move to Recycle. Pending delivery for this email will stop.`,
      confirmText: 'Move to Recycle',
      cancelText: 'Keep',
      tone: 'error',
    });
    if (!ok || this.busy()) return;
    this.busy.set(true);
    this.api.deleteMailPlanSend(item.send.id).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false)))
      .subscribe({ next: ({ planDeleted }) => {
        this.plans.update((list) =>
          planDeleted
            ? list.filter((p) => p.id !== item.plan.id)
            : list.map((p) => (p.id === item.plan.id ? { ...p, sends: p.sends.filter((s) => s.id !== item.send.id) } : p)));
        this.toast.success('Email moved to Recycle.');
        this.refresh();
      }, error: (error) => this.toast.error(mailRecycleError(error, 'Could not move this email to Recycle.')) });
  }

  async deletePlan(plan: MailPlan): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Delete this plan?',
      message: `"${this.planTitle(plan)}" and its emails will move to Recycle and be removed from receivers’ My Reminders. Pending delivery for this plan will stop.`,
      confirmText: 'Move to Recycle',
      cancelText: 'Keep',
      tone: 'error',
    });
    if (!ok || this.busy()) return;
    this.busy.set(true);
    this.api.deleteMailPlan(plan.id).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false)))
      .subscribe({ next: () => {
        this.plans.update((list) => list.filter((p) => p.id !== plan.id));
        this.toast.success('Plan moved to Recycle.');
        this.refresh();
      }, error: (error) => this.toast.error(mailRecycleError(error, 'Could not move this plan to Recycle.')) });
  }

  openRecycle(): void {
    this.dialog.open(MailRecycleDialogComponent, { data: {}, width: '540px' })
      .afterClosed().pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.refresh());
  }

  async deleteAllHistory(): Promise<void> {
    if (this.busy()) return;
    const confirmed = await this.confirm.confirm({
      title: 'Delete all Mail Planner history?',
      message: 'Sent and failed emails will move to Recycle and be removed from My Reminders. Drafts, schedules, and emails with pending delivery stay in the planner.',
      confirmText: 'Move to Recycle',
      cancelText: 'Keep history',
      tone: 'error',
    });
    if (!confirmed || this.busy()) return;
    this.busy.set(true);
    this.api.deleteMailHistory().pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false)))
      .subscribe({ next: ({ count }) => {
        this.toast.success(count ? `${count} email(s) moved to Recycle.` : 'No eligible emails to move.');
        this.refresh();
      }, error: (error) => this.toast.error(mailRecycleError(error, 'Could not move email history to Recycle.')) });
  }

  jumpTo(plan: MailPlan): void {
    const target = this.nextSend(plan) ?? plan.sends[0];
    if (target) this.selectedDay.set(shift(target.at, 0, 0));
  }

  // ---- Reminder actions ----
  planReminder(mail: ReceivedMail, when: Date): void {
    if (when.getTime() < Date.now()) {
      this.toast.warning('Pick a reminder time in the future.');
      return;
    }
    this.updateReceived(this.api.setMailReminder(mail.id, when), () =>
      this.toast.success(`Reminder set for ${when.toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}.`));
  }

  planCustom(mail: ReceivedMail, value: string): void {
    if (value) this.planReminder(mail, new Date(value));
  }

  snooze(mail: ReceivedMail): void {
    this.updateReceived(this.api.snoozeMailReminder(mail.id), () => this.toast.info('Snoozed for one day.'));
  }

  markDone(mail: ReceivedMail): void {
    this.updateReceived(this.api.markMailDone(mail.id));
  }

  clearReminder(mail: ReceivedMail): void {
    this.updateReceived(this.api.setMailReminder(mail.id, null));
  }

  private updateReceived(request: Observable<InboxMailDto>, done?: () => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false))).subscribe((dto) => {
      const mail = toReceived(dto);
      this.received.update((list) => list.map((m) => (m.id === mail.id ? mail : m)));
      done?.();
    });
  }

  statusClass(status: string): string {
    switch (status) {
      case 'Sent':
      case 'Completed':
      case 'Done':
        return 'ok';
      case 'Scheduled':
      case 'Sending':
      case 'Upcoming':
        return 'brand';
      case 'Draft':
      case 'Snoozed':
      case 'Not planned':
        return '';
      case 'Queued':
      case 'Due today':
        return 'pending';
      default:
        return 'closed';
    }
  }
}

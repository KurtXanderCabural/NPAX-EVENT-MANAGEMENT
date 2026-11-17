// Mail planner view models, mapped from the API (dates as Date objects for the calendar views).
import { InboxMailDto, MailAttachmentDto, MailAudienceGroup, MailChannel, MailKind, MailPlanDto, MailPlanSendDto, MailSendStatus } from '../../core/models';

export type { MailChannel, MailKind, MailSendStatus };
export type ReminderStatus = 'Upcoming' | 'Due today' | 'Done' | 'Snoozed' | 'Not planned';

/** Who a plan goes to: the entered e-mail list (plus saved lists on older plans), and who is copied. */
export interface ReceiverSelection {
  groupIds: string[];
  emails: string[];
  excludedEmails: string[];
  cc: string[];
}

/** One target day of a plan: its own send time, subject and message. */
export interface PlanSend {
  id: number;
  at: Date;
  subject: string;
  // Full message HTML; only loaded when the plan is opened for editing.
  body: string;
  attachments: MailAttachmentDto[];
  preview: string;
  images: number;
  status: MailSendStatus;
  canReply: boolean;
  recipientCount: number;
  lastError: string | null;
}

/** A mail plan: a series of mails on one or more target days, sent to one receiver list. */
export interface MailPlan {
  id: number;
  kind: MailKind;
  event: string | null;
  channel: MailChannel;
  draft: boolean;
  sendImmediately: boolean;
  version: string;
  sends: PlanSend[];
  receivers: ReceiverSelection;
}

/** A mail the signed-in user received, with the reminder they planned for it. */
export interface ReceivedMail {
  id: number;
  from: string;
  subject: string;
  preview: string;
  images: number;
  receivedAt: Date;
  event: string | null;
  eventAt: Date | null;
  venue: string | null;
  remindAt: Date | null;
  status: ReminderStatus;
  canReply: boolean;
}

export function toPlanSend(s: MailPlanSendDto): PlanSend {
  return {
    id: s.id,
    at: new Date(s.sendAt),
    subject: s.subject,
    body: s.bodyHtml ?? '',
    attachments: s.attachments ?? [],
    preview: s.previewText,
    images: s.imageCount,
    status: s.status,
    canReply: s.canReply === true,
    recipientCount: s.recipientCount,
    lastError: s.lastError,
  };
}

export function toPlan(p: MailPlanDto): MailPlan {
  return {
    id: p.id,
    kind: p.kind,
    event: p.eventName,
    channel: p.channel,
    draft: p.draft,
    sendImmediately: p.sendImmediately === true,
    version: p.version,
    sends: p.sends.map(toPlanSend),
    receivers: { groupIds: p.groupIds, emails: p.emails, excludedEmails: p.excludedEmails ?? [], cc: p.cc ?? [] },
  };
}

export type PlanStatus = 'Draft' | 'Scheduled' | 'Sending' | 'Queued' | 'Failed' | 'Completed';

export function planStatus(p: MailPlan): PlanStatus {
  if (p.draft) return 'Draft';
  // Prioritize active delivery, then days that need attention, then future days.
  for (const status of ['Sending', 'Failed', 'Queued', 'Scheduled'] as const) {
    if (p.sends.some((s) => s.status === status)) return status;
  }
  return 'Completed';
}

export function toReceived(m: InboxMailDto): ReceivedMail {
  const remindAt = m.remindAt ? new Date(m.remindAt) : null;
  return {
    id: m.id,
    from: m.from,
    subject: m.subject,
    preview: m.previewText,
    images: m.imageCount,
    receivedAt: new Date(m.receivedAt),
    event: m.event,
    eventAt: m.eventAt ? new Date(m.eventAt) : null,
    venue: m.venue,
    remindAt,
    status: reminderStatus(m.done, m.snoozed, remindAt),
    canReply: m.canReply === true,
  };
}

function reminderStatus(done: boolean, snoozed: boolean, remindAt: Date | null): ReminderStatus {
  if (done) return 'Done';
  if (!remindAt) return 'Not planned';
  if (snoozed) return 'Snoozed';
  const endOfToday = new Date();
  endOfToday.setHours(23, 59, 59, 999);
  // Includes reminders whose time has passed but that aren't marked done.
  return remindAt.getTime() <= endOfToday.getTime() ? 'Due today' : 'Upcoming';
}

/** Unique, lower-cased e-mail addresses a selection resolves to today. */
export function resolveReceivers(groups: MailAudienceGroup[], sel: Pick<ReceiverSelection, 'groupIds' | 'emails' | 'excludedEmails'>): string[] {
  const emails = new Set<string>();
  for (const g of groups) if (sel.groupIds.includes(g.id)) g.emails.forEach((e) => emails.add(e.toLowerCase()));
  sel.emails.forEach((e) => emails.add(e.toLowerCase()));
  sel.excludedEmails.forEach((e) => emails.delete(e.toLowerCase()));
  return [...emails];
}

using EventRegistration.Api.Data;
using EventRegistration.Api.Models;

namespace EventRegistration.Api.Services;

public static class MailRecycle
{
    public static IQueryable<MailConversationReply> PendingReplies(AppDbContext db) =>
        db.MailConversationReplies.Where(r => r.Status == MailSendStatus.Queued || r.Status == MailSendStatus.Sending);

    public static IQueryable<InvitationEmail> EligibleInvitations(AppDbContext db, int eventId) =>
        db.InvitationEmails.Where(m => m.EventId == eventId && m.RecycledAt == null &&
            InvitationEmailStatus.Deletable.Contains(m.Status) &&
            !PendingReplies(db).Any(r => r.InvitationEmailId == m.Id));

    public static IQueryable<MailPlanSend> EligiblePlanEmails(AppDbContext db) =>
        db.MailPlanSends.Where(s => s.RecycledAt == null && s.Status != MailSendStatus.Sending &&
            !PendingReplies(db).Any(r => r.MailPlanSendId == s.Id));
}

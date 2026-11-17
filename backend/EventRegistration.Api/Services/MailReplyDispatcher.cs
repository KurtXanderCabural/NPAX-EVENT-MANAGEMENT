using System.Net;
using System.Text.Json;
using EventRegistration.Api.Data;
using EventRegistration.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRegistration.Api.Services;

public sealed class MailReplyDispatcher(AppDbContext db, EmailService email, ILogger<MailReplyDispatcher> log)
{
    public async Task DispatchAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.MailConversationReplies
            .Where(r => r.Id == id && r.Status == MailSendStatus.Queued && r.NextAttemptAt <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, MailSendStatus.Sending)
                .SetProperty(r => r.LastAttemptAt, now).SetProperty(r => r.Attempts, r => r.Attempts + 1), cancellationToken);
        if (claimed == 0) return;

        var reply = await db.MailConversationReplies.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (reply is null) return;
        var delivered = JsonSerializer.Deserialize<HashSet<string>>(reply.DeliveredJson ?? "[]")!;
        var recipients = JsonSerializer.Deserialize<string[]>(reply.RecipientsJson)!;
        var thread = reply.Kind == "Forward"
            ? new EmailThreadHeaders(reply.MessageId, reply.ReplyToAddress)
            : new EmailThreadHeaders(reply.MessageId, reply.ReplyToAddress, reply.ParentMessageId,
                JsonSerializer.Deserialize<string[]>(reply.ReferencesJson));
        var (html, images) = reply.BodyHtml is null
            ? ("<div>" + WebUtility.HtmlEncode(reply.Body).ReplaceLineEndings("<br />") + "</div>", new Dictionary<string, byte[]>())
            : InvitationMessageBuilder.RenderBody(reply.BodyHtml);
        var attachments = await MailAttachments.LoadForDeliveryAsync(db.Entry(reply).Collection(r => r.Attachments).Query());
        foreach (var batch in recipients.Where(address => !delivered.Contains(address)).Chunk(EmailService.MaxRecipientsPerMessage))
        {
            EmailDeliveryStatus result;
            try
            {
                result = recipients.Length == 1
                    ? await email.SendMessageAsync(batch, [], reply.Subject, html, inlineImages: images, attachments: attachments, thread: thread)
                    : await email.SendBccAsync(batch, reply.Subject, html, inlineImages: images, attachments: attachments, thread: thread);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to send mail reply {ReplyId}.", id);
                result = EmailDeliveryStatus.Queued;
            }
            if (result != EmailDeliveryStatus.Sent)
            {
                reply.Status = reply.Attempts >= MailPlanDispatcher.MaxAttempts ? MailSendStatus.Failed : MailSendStatus.Queued;
                reply.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            delivered.UnionWith(batch);
            reply.DeliveredJson = JsonSerializer.Serialize(delivered);
            reply.LastAttemptAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        reply.Status = MailSendStatus.Sent;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task DispatchDueAsync(IServiceScopeFactory scopes, AppDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.MailConversationReplies.Where(r => r.Status == MailSendStatus.Sending && r.LastAttemptAt < now.AddMinutes(-10))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, MailSendStatus.Failed), cancellationToken);
        var ids = await db.MailConversationReplies.Where(r => r.Status == MailSendStatus.Queued && r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt).Select(r => r.Id).Take(10).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) break;
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MailReplyDispatcher>().DispatchAsync(id, cancellationToken);
        }
    }
}

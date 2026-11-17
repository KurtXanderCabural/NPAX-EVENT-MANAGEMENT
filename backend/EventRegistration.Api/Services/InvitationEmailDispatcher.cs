using System.Text.Json;
using EventRegistration.Api.Data;
using EventRegistration.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRegistration.Api.Services;

public class InvitationEmailDispatcher(AppDbContext db, EmailService email, IConfiguration config,
    ILogger<InvitationEmailDispatcher> log)
{
    public const int MaxAttempts = 5;

    public async Task DispatchAsync(int id)
    {
        var now = DateTimeOffset.UtcNow;
        // Claim in SQL, shared by requests and every API instance. An edit changes
        // the rowversion; a claimed message can no longer be edited or cancelled.
        var claimed = await db.InvitationEmails.Where(m => m.Id == id && m.RecycledAt == null &&
            (m.Status == InvitationEmailStatus.Scheduled || m.Status == InvitationEmailStatus.Queued) &&
            m.NextAttemptAt <= now && m.Attempts < MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, InvitationEmailStatus.Sending)
                .SetProperty(m => m.LastAttemptAt, now).SetProperty(m => m.UpdatedAt, now)
                .SetProperty(m => m.Attempts, m => m.Attempts + 1));
        if (claimed == 0) return;
        db.ChangeTracker.Clear();
        var item = await db.InvitationEmails.Include(m => m.Event).SingleOrDefaultAsync(m => m.Id == id);
        if (item is null) return; // The event may have been deleted after the claim.
        var recordingDelivery = false;
        try
        {
            var ev = item.Event ?? throw new InvalidOperationException("The event no longer exists.");
            var message = await new InvitationMessageBuilder(db, config).BuildAsync(ev,
                new(JsonSerializer.Deserialize<List<string>>(item.ToJson),
                    JsonSerializer.Deserialize<List<string>>(item.CcJson), item.Subject, item.BodyHtml), enforceRecipientEntryLimit: false,
                sendAt: item.ScheduledAt ?? DateTimeOffset.UtcNow);
            if (ev.StartsAt == default || ev.EndsAt <= ev.StartsAt)
                throw new ArgumentException("Set a valid start and end date/time for this event.");
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.InviteSequence, e => e.InviteSequence + 2));
            await db.Entry(ev).ReloadAsync();
            var recipients = message.To.Concat(message.Cc).ToList();
            var delivered = item.GetDeliveredRecipients();
            var pending = recipients.Where(address => !delivered.Contains(address)).ToList();
            var invitations = await db.Invitations.Where(i => i.EventId == ev.Id && pending.Contains(i.Email)).ToListAsync();
            var known = invitations.Select(i => i.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var address in pending)
                if (known.Add(address))
                {
                    var invite = new Invitation { EventId = ev.Id, Email = address, Status = InvitationStatus.Queued };
                    db.Invitations.Add(invite);
                    invitations.Add(invite);
                }
            if (delivered.Count == 0) item.SmtpMessageId ??= email.NewMessageId();
            await db.SaveChangesAsync();
            var ccAddresses = message.Cc.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var invitesByEmail = invitations.ToLookup(i => i.Email, StringComparer.OrdinalIgnoreCase);
            var attachments = await MailAttachments.LoadForDeliveryAsync(db.Entry(item).Collection(m => m.Attachments).Query());
            foreach (var batch in pending.Chunk(EmailService.MaxRecipientsPerMessage))
            {
                var to = batch.Where(address => !ccAddresses.Contains(address)).ToArray();
                var cc = batch.Where(ccAddresses.Contains).ToArray();
                var calendar = CalendarInvite.ForInvitation(ev, email.FromName, email.FromAddress, to, cc, message.RegisterUrl);
                var delivery = await email.SendMessageAsync(to, cc, message.Subject, message.Html,
                    inlineImages: message.Images, attachments: [.. attachments, calendar],
                    thread: item.SmtpMessageId is null ? null : new(item.SmtpMessageId, item.ReplyToAddress));
                if (delivery != EmailDeliveryStatus.Sent)
                {
                    var lastAttempt = item.Attempts >= MaxAttempts;
                    item.Status = lastAttempt ? InvitationEmailStatus.Failed : InvitationEmailStatus.Queued;
                    item.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5);
                    var sent = recipients.Count(delivered.Contains);
                    var progress = $"SMTP accepted {sent} of {recipients.Count} recipients. The next batch could not be sent.";
                    item.LastError = lastAttempt
                        ? $"{progress} Open this email to retry delivery to the remaining recipients."
                        : $"{progress} Delivery to the remaining recipients will retry in five minutes.";
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();
                    return;
                }

                // If the checkpoint fails, leave this in Sending for review. SMTP already accepted this batch.
                recordingDelivery = true;
                var acceptedAt = DateTimeOffset.UtcNow;
                delivered.UnionWith(batch);
                item.DeliveredRecipientsJson = JsonSerializer.Serialize(delivered);
                item.LastAttemptAt = acceptedAt;
                item.UpdatedAt = acceptedAt;
                foreach (var invite in batch.SelectMany(address => invitesByEmail[address])
                    .Where(i => i.Status != InvitationStatus.Registered))
                {
                    invite.Status = InvitationStatus.Sent;
                    invite.SentAt = acceptedAt.UtcDateTime;
                }
                await db.SaveChangesAsync();
                recordingDelivery = false;
            }
            item.Status = InvitationEmailStatus.Sent;
            item.SentAt = DateTimeOffset.UtcNow;
            item.LastError = null;
        }
        catch (Exception ex) when (!recordingDelivery)
        {
            log.LogError(ex, "Invitation email {Id} failed.", id);
            item.Status = InvitationEmailStatus.Failed;
            // Do not disclose SMTP credentials, filesystem paths or database details.
            item.LastError = "Delivery could not be completed. Check the event and email settings, then open this email to retry.";
        }
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

public class InvitationEmailScheduler(IServiceScopeFactory scopes, ILogger<InvitationEmailScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTimeOffset.UtcNow;
                // A restart or interrupted SMTP attempt has an uncertain outcome.
                await db.InvitationEmails.Where(m => m.Status == InvitationEmailStatus.Sending &&
                        m.LastAttemptAt < now.AddMinutes(-10))
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, InvitationEmailStatus.Failed)
                        .SetProperty(m => m.UpdatedAt, now)
                        .SetProperty(m => m.LastError, "Delivery was interrupted. Check whether the last batch arrived before retrying. Recipients already recorded as sent will be skipped."), stoppingToken);
                var ids = await db.InvitationEmails.AsNoTracking()
                    .Where(m => m.RecycledAt == null && (m.Status == InvitationEmailStatus.Scheduled || m.Status == InvitationEmailStatus.Queued) &&
                        m.NextAttemptAt <= now && m.Attempts < InvitationEmailDispatcher.MaxAttempts)
                    .OrderBy(m => m.NextAttemptAt).Select(m => m.Id).Take(20).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    using var deliveryScope = scopes.CreateScope();
                    await deliveryScope.ServiceProvider.GetRequiredService<InvitationEmailDispatcher>().DispatchAsync(id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Invitation email scheduler failed; retrying on the next poll."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

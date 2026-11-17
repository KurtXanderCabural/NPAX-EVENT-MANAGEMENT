using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventRegistration.Api.Data;
using EventRegistration.Api.Models;
using EventRegistration.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventRegistration.Api.Controllers;

// Mail planner (organizers): plans with target days, each with its own mail, sent to receiver lists.
[ApiController]
[Route("api/mail-plans")]
[Authorize(Roles = Roles.Admin + "," + Roles.SuperAdmin)]
public class MailPlansController(AppDbContext db, MailPlanAudience audience, MailPlanDispatcher dispatcher) : ControllerBase
{
    private const int MaxDays = 60;
    private const int MaxEmails = 200;
    private const int MaxCc = 50;
    private const int MaxGroups = 50;
    private const int MaxBodyText = 4000;
    private static readonly TimeSpan PhOffset = TimeSpan.FromHours(8);

    // Plans with their days; bodies stay out of the list (they can carry several MB of images).
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var plans = await db.MailPlans.AsNoTracking()
            .Where(p => p.Sends.Any(s => s.RecycledAt == null))
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new MailPlan {
                Id = p.Id, Kind = p.Kind, EventId = p.EventId, EventName = p.EventName, Channel = p.Channel, Draft = p.Draft,
                SendImmediately = p.SendImmediately,
                GroupIdsJson = p.GroupIdsJson, EmailsJson = p.EmailsJson, ExcludedEmailsJson = p.ExcludedEmailsJson,
                CcJson = p.CcJson, CreatedByUserId = p.CreatedByUserId,
                CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt, Version = p.Version,
                Sends = p.Sends.Where(s => s.RecycledAt == null).OrderBy(s => s.SendAt).Select(s => new MailPlanSend {
                    Id = s.Id, PlanId = s.PlanId, SendAt = s.SendAt, Subject = s.Subject, PreviewText = s.PreviewText,
                    ImageCount = s.ImageCount, Status = s.Status, SentAt = s.SentAt, RecipientCount = s.RecipientCount,
                    SmtpMessageId = s.SmtpMessageId,
                    LastError = s.LastError, Attempts = s.Attempts, NextAttemptAt = s.NextAttemptAt,
                }).ToList(),
            }).ToListAsync();
        return Ok(plans.Select(p => PlanDto(p, withBodies: false)));
    }

    // One plan with every day's full message, for editing.
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var plan = await db.MailPlans.AsNoTracking().AsSplitQuery()
            .Where(p => p.Id == id && p.Sends.Any(s => s.RecycledAt == null))
            .Include(p => p.Sends.Where(s => s.RecycledAt == null)).ThenInclude(s => s.Attachments)
            .SingleOrDefaultAsync();
        return plan is null ? NotFound() : Ok(PlanDto(plan, withBodies: true));
    }

    [HttpGet("sends/{sendId:int}/body")]
    public async Task<IActionResult> Body(int sendId)
    {
        var body = await db.MailPlanSends.AsNoTracking().Where(s => s.Id == sendId && s.RecycledAt == null)
            .Select(s => s.BodyHtml).SingleOrDefaultAsync();
        return body is null ? NotFound() : Ok(new { bodyHtml = body });
    }

    // Event names and receiver lists for the plan form.
    [HttpGet("options")]
    public async Task<IActionResult> Options()
    {
        var since = DateTime.Now.AddDays(-30);
        var events = await db.Events.AsNoTracking().Where(e => e.EndsAt >= since).OrderBy(e => e.StartsAt)
            .Select(e => e.Title).Take(100).ToListAsync();
        var saved = await db.MailPlanEventNames.AsNoTracking().Select(n => n.Name).ToListAsync();
        var used = await db.MailPlans.AsNoTracking().Where(p => p.EventName != null).Select(p => p.EventName!).Distinct().ToListAsync();
        var known = events.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Ok(new {
            events = events.Distinct(StringComparer.OrdinalIgnoreCase),
            savedEvents = saved.Concat(used).Where(known.Add).Order(StringComparer.OrdinalIgnoreCase),
            groups = await audience.GroupsAsync(),
            savedCc = await db.MailPlanSavedCcs.AsNoTracking().OrderBy(c => c.Email).Select(c => c.Email).ToListAsync(),
        });
    }

    // Saved CC addresses: offered in the CC dropdown; typing a new address still works.
    [HttpPost("saved-cc")]
    public async Task<IActionResult> SaveCc(MailSavedCcRequest req)
    {
        var email = InvitationMessageBuilder.Address(req.Email);
        if (email is null) return BadRequest(new { message = "Enter a valid email address." });
        if (!await db.MailPlanSavedCcs.AnyAsync(c => c.Email == email))
        {
            if (await db.MailPlanSavedCcs.CountAsync() >= 500)
                return BadRequest(new { message = "The saved CC list is full (500). Remove addresses you no longer use." });
            db.MailPlanSavedCcs.Add(new MailPlanSavedCc { Email = email });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* Saved at the same time by someone else. */ }
        }
        return Ok(new { email });
    }

    [HttpDelete("saved-cc")]
    public async Task<IActionResult> RemoveSavedCc([FromQuery] string? email)
    {
        var address = InvitationMessageBuilder.Address(email);
        if (address is not null) await db.MailPlanSavedCcs.Where(c => c.Email == address).ExecuteDeleteAsync();
        return NoContent();
    }

    [HttpPost("event-names")]
    public async Task<IActionResult> SaveEventName(MailEventNameRequest req)
    {
        var name = CleanEventName(req.Name);
        if (name is null) return BadRequest(new { message = "Enter an event name of up to 200 characters." });
        if (!await db.MailPlanEventNames.AnyAsync(n => n.Name == name))
        {
            db.MailPlanEventNames.Add(new MailPlanEventName { Name = name });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* Saved at the same time by someone else. */ }
        }
        return Ok(new { name });
    }

    [HttpPost]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> Create(MailPlanRequest req)
    {
        var plan = new MailPlan { CreatedByUserId = this.UserId() };
        string? error;
        try { error = await Apply(plan, req); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        if (error is not null) return BadRequest(new { message = error });
        db.MailPlans.Add(plan);
        await db.SaveChangesAsync();
        return Ok(PlanDto(plan, withBodies: false));
    }

    [HttpPut("{id:int}")]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> Update(int id, MailPlanRequest req)
    {
        var plan = await db.MailPlans.AsSplitQuery().Include(p => p.Sends.Where(s => s.RecycledAt == null)).ThenInclude(s => s.Attachments)
            .SingleOrDefaultAsync(p => p.Id == id && p.Sends.Any(s => s.RecycledAt == null));
        if (plan is null) return NotFound(new { message = "This plan no longer exists." });
        if (Convert.ToBase64String(plan.Version) != req.Version)
            return Conflict(new { message = "This plan was changed by someone else. Reopen it before editing." });
        string? error;
        try { error = await Apply(plan, req); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        if (error is not null) return BadRequest(new { message = error });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "A day of this plan started sending or was changed. Reopen the plan before editing." });
        }
        return Ok(PlanDto(plan, withBodies: false));
    }

    [HttpDelete("{id:int}")]
    [HttpPost("{id:int}/recycle")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var current = db.MailPlanSends.Where(s => s.PlanId == id && s.RecycledAt == null);
        var eligible = MailRecycle.EligiblePlanEmails(db).Where(s => s.PlanId == id);
        if (await current.CountAsync(ct) != await eligible.CountAsync(ct))
            return Conflict(new { message = "A message is sending, or a reply or forward is still pending. Try again after delivery finishes." });
        var now = DateTimeOffset.UtcNow;
        await eligible.ExecuteUpdateAsync(u => u.SetProperty(s => s.RecycledAt, now), ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpDelete("sends/{sendId:int}")]
    [HttpPost("sends/{sendId:int}/recycle")]
    public async Task<IActionResult> DeleteSend(int sendId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var send = await db.MailPlanSends.Where(s => s.Id == sendId && s.RecycledAt == null)
            .Select(s => new { s.PlanId }).SingleOrDefaultAsync(ct);
        if (send is null) return NotFound(new { message = "This email is no longer in the planner." });
        var now = DateTimeOffset.UtcNow;
        var count = await MailRecycle.EligiblePlanEmails(db).Where(s => s.Id == sendId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RecycledAt, now), ct);
        if (count == 0)
            return Conflict(new { message = "This email is sending, or a reply or forward is still pending." });
        var planDeleted = !await db.MailPlanSends.AnyAsync(s => s.PlanId == send.PlanId && s.RecycledAt == null, ct);
        await transaction.CommitAsync(ct);
        return Ok(new { planDeleted });
    }

    [HttpDelete("history")]
    [HttpPost("history/recycle-all")]
    public async Task<IActionResult> RecycleHistory(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var now = DateTimeOffset.UtcNow;
        var count = await MailRecycle.EligiblePlanEmails(db)
            .Where(s => !s.Plan!.Draft && (s.Status == MailSendStatus.Sent || s.Status == MailSendStatus.Failed))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RecycledAt, now), ct);
        await transaction.CommitAsync(ct);
        return Ok(new { count });
    }

    [HttpGet("recycle")]
    public async Task<IActionResult> RecycledEmails(CancellationToken ct) =>
        Ok(await db.MailPlanSends.Where(s => s.RecycledAt != null)
            .OrderByDescending(s => s.RecycledAt).ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.Subject, s.RecycledAt, messageAt = s.SentAt ?? s.SendAt, s.Plan!.EventName })
            .ToListAsync(ct));

    [HttpPost("recycle/{id:int}/restore")]
    public async Task<IActionResult> RestoreEmail(int id, CancellationToken ct)
    {
        var recycled = db.MailPlanSends.Where(s => s.Id == id && s.RecycledAt != null);
        // Restoring previously scheduled or queued mail requires an explicit retry.
        var count = await recycled.Where(s => !s.Plan!.Draft &&
                (s.Status == MailSendStatus.Scheduled || s.Status == MailSendStatus.Queued))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.RecycledAt, (DateTimeOffset?)null)
                .SetProperty(s => s.Status, MailSendStatus.Failed)
                .SetProperty(s => s.LastError, "Restored from Recycle. Select Retry to send this email."), ct);
        if (count == 0)
        {
            count = await recycled.Where(s => s.Plan!.Draft ||
                    (s.Status != MailSendStatus.Scheduled && s.Status != MailSendStatus.Queued))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.RecycledAt, (DateTimeOffset?)null), ct);
        }
        return count == 0 ? NotFound() : NoContent();
    }

    [HttpDelete("recycle")]
    [HttpPost("recycle/empty")]
    public async Task<IActionResult> EmptyRecycle(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var recycled = db.MailPlanSends.Where(s => s.RecycledAt != null);
        var affectedPlans = await recycled.Select(s => s.PlanId).Distinct().ToListAsync(ct);
        var count = await recycled.ExecuteDeleteAsync(ct);
        await db.MailPlans.Where(p => affectedPlans.Contains(p.Id) && !p.Sends.Any()).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return Ok(new { count });
    }

    [HttpPost("sends/{sendId:int}/send-now")]
    public async Task<IActionResult> SendNow(int sendId)
    {
        var send = await db.MailPlanSends.Include(s => s.Plan).SingleOrDefaultAsync(s => s.Id == sendId && s.RecycledAt == null);
        if (send is null) return NotFound(new { message = "This day no longer exists. Refresh the planner." });
        if (send.Plan!.Draft) return BadRequest(new { message = "Finish this draft plan before sending it." });
        if (send.Status != MailSendStatus.Scheduled) return Conflict(new { message = "This day is already being sent or was sent." });
        send.SendAt = send.NextAttemptAt = DateTimeOffset.UtcNow;
        send.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAndDispatch(send);
    }

    // Tries a failed or queued day again now; receivers it already reached are skipped.
    [HttpPost("sends/{sendId:int}/retry")]
    public async Task<IActionResult> Retry(int sendId)
    {
        var send = await db.MailPlanSends.Include(s => s.Plan).SingleOrDefaultAsync(s => s.Id == sendId && s.RecycledAt == null);
        if (send is null) return NotFound(new { message = "This day no longer exists. Refresh the planner." });
        if (send.Status is not (MailSendStatus.Failed or MailSendStatus.Queued))
            return Conflict(new { message = "Only failed or queued days can be retried." });
        if (send.Plan!.Draft) return BadRequest(new { message = "Finish this draft plan before sending it." });
        send.Status = MailSendStatus.Queued;
        send.Attempts = 0;
        send.NextAttemptAt = send.UpdatedAt = DateTimeOffset.UtcNow;
        send.LastError = null;
        return await SaveAndDispatch(send);
    }

    private async Task<IActionResult> SaveAndDispatch(MailPlanSend send)
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "This day started sending. Refresh the planner." }); }
        await dispatcher.DispatchAsync(send.Id);
        var saved = await db.MailPlanSends.AsNoTracking().SingleAsync(s => s.Id == send.Id);
        return Ok(SendDto(saved, withBody: false));
    }

    // Validates the request and applies it to the plan. Days that have gone out (anything not
    // Scheduled) are kept as they are; Scheduled days are updated, added or removed to match.
    private async Task<string?> Apply(MailPlan plan, MailPlanRequest req)
    {
        if (!MailPlanKinds.IsValid(req.Kind)) return "Choose a mail type.";
        if (!MailPlanChannels.IsValid(req.Channel)) return "Choose Email, Viber or Both.";
        string? eventName = null;
        if (!string.IsNullOrWhiteSpace(req.EventName) && (eventName = CleanEventName(req.EventName)) is null)
            return "Enter an event name of up to 200 characters.";

        var emails = new List<string>();
        foreach (var raw in req.Emails ?? [])
            emails.Add(InvitationMessageBuilder.Address(raw) ?? $"!{raw}");
        if (emails.FirstOrDefault(e => e.StartsWith('!')) is { } invalid) return $"Check this address: {invalid[1..]}";
        emails = emails.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (emails.Count > MaxEmails) return $"Add at most {MaxEmails} email addresses per plan.";

        var excludedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in req.ExcludedEmails ?? JsonSerializer.Deserialize<List<string>>(plan.ExcludedEmailsJson) ?? [])
        {
            if (InvitationMessageBuilder.Address(raw) is not { } address) return $"Check this excluded address: {raw}";
            excludedEmails.Add(address);
        }

        var cc = new List<string>();
        foreach (var raw in req.Cc ?? [])
        {
            if (InvitationMessageBuilder.Address(raw) is not { } address) return $"Check this CC address: {raw}";
            cc.Add(address);
        }
        cc = cc.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cc.Count > MaxCc) return $"Add at most {MaxCc} CC addresses per plan.";

        var groupIds = (req.GroupIds ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();
        if (groupIds.Count > MaxGroups) return $"Pick at most {MaxGroups} saved lists.";
        var previous = JsonSerializer.Deserialize<List<string>>(plan.GroupIdsJson) ?? [];
        var known = (await audience.GroupsAsync()).Select(g => g.Id).Concat(previous).ToHashSet();
        if (groupIds.Any(g => !known.Contains(g))) return "A saved list no longer exists. Reload the planner and pick the lists again.";

        var locked = plan.Sends.Where(s => s.Status != MailSendStatus.Scheduled).ToList();
        var editable = plan.Sends.Where(s => s.Status == MailSendStatus.Scheduled).ToDictionary(s => s.Id);
        var inputs = req.Sends ?? [];
        if (req.SendImmediately && inputs.Count != 1) return "Send now requires exactly one message.";
        if (inputs.Count > MaxDays) return $"A plan can have at most {MaxDays} days.";
        if (locked.Count + inputs.Count == 0) return "Pick at least one target day.";
        var days = locked.Select(s => s.SendAt.ToOffset(PhOffset).Date).ToHashSet();
        var uploads = await MailAttachments.FindAsync(db, inputs.SelectMany(input => input.AttachmentIds ?? []));
        var prepared = new List<(MailPlanSendInput Input, string Subject, string Body, string Text, int Images,
            List<MailAttachmentUpload> Attachments)>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var label = req.SendImmediately ? "Message" : $"Day {locked.Count + i + 1}";
            if (input.Id is { } existingId && !editable.ContainsKey(existingId))
                throw new InvalidOperationException($"{label} has already gone out or was removed. Reopen the plan before editing.");
            var subject = (input.Subject ?? "").Trim();
            if (subject.Length is 0 or > 200 || subject.Contains('\r') || subject.Contains('\n'))
                return $"{label}: enter a subject of up to 200 characters.";
            string body;
            int images;
            List<MailAttachmentUpload> attachments;
            try
            {
                var rendered = InvitationMessageBuilder.RenderBody(input.BodyHtml);
                body = InvitationMessageBuilder.EditableBody(rendered.Html, rendered.Images);
                images = rendered.Images.Count;
                var inlineBytes = rendered.Images.Values.Sum(bytes => (long)bytes.Length);
                if (input.AttachmentIds is null && input.Id is { } id)
                {
                    var currentFiles = editable[id].Attachments.ToDictionary(file => file.Id);
                    attachments = MailAttachments.SelectFiles(currentFiles.Keys, currentFiles, inlineBytes);
                }
                else
                    attachments = MailAttachments.SelectFiles(input.AttachmentIds ?? [], uploads, inlineBytes);
                if (attachments.Count > 0 && req.Channel == MailPlanChannels.Viber)
                    return $"{label}: choose Email or Both to attach files.";
            }
            catch (ArgumentException ex) { return $"{label}: {ex.Message}"; }
            var text = InvitationMessageBuilder.PlainText(body);
            if (text.Length == 0 && images == 0 && attachments.Count == 0) return $"{label}: write the message or attach a file.";
            if (text.Length > MaxBodyText) return $"{label}: keep the message under {MaxBodyText} characters.";
            if (!req.SendImmediately)
            {
                if (input.SendAt is not { } sendAt) return $"{label}: choose a date and time.";
                if (!req.Draft && sendAt <= now) return $"{label}: pick a send time that has not passed.";
                if (!days.Add(sendAt.ToOffset(PhOffset).Date)) return $"{label}: another day of this plan is already on that date.";
            }
            prepared.Add((input, subject, body, text, images, attachments));
        }
        if (!req.Draft && cc.Count == 0 && (await audience.ResolveAsync(groupIds, emails, excludedEmails)).Count == 0)
            return "Add at least one receiver or CC address.";

        plan.Kind = req.Kind!;
        plan.Channel = req.Channel!;
        plan.Draft = req.Draft;
        plan.SendImmediately = req.SendImmediately;
        plan.EventName = eventName;
        var today = DateTime.Now;
        plan.EventId = eventName is null ? null : await db.Events.Where(e => e.Title == eventName)
            .OrderByDescending(e => e.EndsAt >= today).ThenBy(e => e.StartsAt).Select(e => (int?)e.Id).FirstOrDefaultAsync();
        plan.GroupIdsJson = JsonSerializer.Serialize(groupIds);
        plan.EmailsJson = JsonSerializer.Serialize(emails);
        plan.ExcludedEmailsJson = JsonSerializer.Serialize(excludedEmails);
        plan.CcJson = JsonSerializer.Serialize(cc);
        plan.UpdatedAt = now;

        var kept = prepared.Where(p => p.Input.Id is not null).Select(p => p.Input.Id!.Value).ToHashSet();
        foreach (var removed in editable.Values.Where(s => !kept.Contains(s.Id))) removed.RecycledAt = now;
        foreach (var (input, subject, body, text, images, attachments) in prepared)
        {
            var send = input.Id is { } existingId ? editable[existingId] : new MailPlanSend();
            send.SendAt = req.SendImmediately ? now : input.SendAt!.Value.ToUniversalTime();
            send.NextAttemptAt = send.SendAt;
            // Send now uses the existing delivery worker and its batching/retry rules.
            send.Status = req.SendImmediately && !req.Draft ? MailSendStatus.Queued : MailSendStatus.Scheduled;
            send.Subject = subject;
            send.BodyHtml = body;
            MailAttachments.Replace(send.Attachments, attachments);
            var preview = Regex.Replace(text, @"\s+", " ");
            send.PreviewText = preview.Length > 300 ? preview[..300] : preview;
            send.ImageCount = images;
            send.Attempts = 0;
            send.LastError = null;
            send.UpdatedAt = now;
            if (input.Id is null) plan.Sends.Add(send);
        }
        return null;
    }

    private static string? CleanEventName(string? value)
    {
        var name = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return name.Length is 0 or > 200 || name.Any(char.IsControl) ? null : name;
    }

    private static object PlanDto(MailPlan p, bool withBodies) => new {
        p.Id, p.Kind, p.EventId, p.EventName, p.Channel, p.Draft, p.SendImmediately,
        groupIds = JsonSerializer.Deserialize<List<string>>(p.GroupIdsJson),
        emails = JsonSerializer.Deserialize<List<string>>(p.EmailsJson),
        excludedEmails = JsonSerializer.Deserialize<List<string>>(p.ExcludedEmailsJson),
        cc = JsonSerializer.Deserialize<List<string>>(p.CcJson),
        p.CreatedAt, p.UpdatedAt, p.Version,
        sends = p.Sends.Where(s => s.RecycledAt == null).OrderBy(s => s.SendAt).Select(s => SendDto(s, withBodies)),
    };

    private static object SendDto(MailPlanSend s, bool withBody) => new {
        s.Id, s.SendAt, s.Subject, s.PreviewText, s.ImageCount, bodyHtml = withBody ? s.BodyHtml : null,
        attachments = withBody ? s.Attachments.OrderBy(file => file.UploadedAt).Select(MailAttachments.Describe) : null,
        s.Status, s.SentAt, s.RecipientCount, s.LastError, s.Attempts, s.NextAttemptAt,
        canReply = s.Status == MailSendStatus.Sent && s.SmtpMessageId != null,
    };
}

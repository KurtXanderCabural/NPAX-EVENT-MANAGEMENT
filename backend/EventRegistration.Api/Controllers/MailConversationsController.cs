using System.Data;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EventRegistration.Api.Data;
using EventRegistration.Api.Models;
using EventRegistration.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventRegistration.Api.Controllers;

[ApiController]
[Route("api/mail-conversations/{source}/{id:int}")]
[Authorize]
public sealed class MailConversationsController(AppDbContext db, EmailService email, IConfiguration config) : ControllerBase
{
    private sealed record Conversation(string Subject, string? BodyHtml, string MessageId, int? InvitationId, int? SendId,
        int? DeliveryId, bool IsOrganizer, string? OrganizerEmail, string? ParticipantEmail, string[] Recipients);

    public record ReplyRequest(Guid Id, Guid? ParentId, string? Body, string? BodyHtml = null,
        List<Guid>? AttachmentIds = null, List<string>? ForwardTo = null, string? Emoji = null);

    private static readonly HashSet<string> ReactionEmoji = ["👍", "❤️", "😊", "🎉", "😮", "🙏"];
    private bool IsAdmin => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.SuperAdmin);

    [HttpGet]
    public async Task<IActionResult> Read(string source, int id, [FromQuery] bool includeBody = true,
        [FromQuery] bool includeReplyContent = true)
    {
        var conversation = await FindConversation(source, id, includeBody);
        if (conversation is null) return NotFound(new { message = "This message has no available conversation." });
        var query = VisibleReplies(conversation).AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id);
        if (!includeReplyContent)
        {
            var metadata = await query.Select(r => new { r.Id, r.AuthorName, r.Status, r.CreatedAt, r.Kind,
                isPrivate = r.PrivateDeliveryId != null }).ToListAsync();
            return Ok(new { conversation.Subject, conversation.BodyHtml, replies = metadata, supportsRichActions = true,
                recipientCount = conversation.DeliveryId is null ? conversation.Recipients.Length : 1 });
        }
        var replies = await query.AsSplitQuery()
            .Select(r => new { r.Id, r.AuthorName, r.Body, r.Status, r.CreatedAt, r.Kind,
                r.BodyHtml, attachments = r.Attachments.OrderBy(a => a.UploadedAt)
                    .Select(a => new MailAttachmentDto(a.Id, a.FileName, a.Size)).ToList(),
                isPrivate = r.PrivateDeliveryId != null }).ToListAsync();
        return Ok(new { conversation.Subject, conversation.BodyHtml, replies, supportsRichActions = true,
            recipientCount = conversation.DeliveryId is null ? conversation.Recipients.Length : 1 });
    }

    [HttpPost("attachments")]
    [RequestSizeLimit(MailAttachments.MaxBytes + 100_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = MailAttachments.MaxBytes)]
    public async Task<IActionResult> UploadAttachment(string source, int id, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (await FindConversation(source, id, includeBody: false) is null) return NotFound();
        MailAttachmentUpload upload;
        try { upload = await MailAttachments.ReadUploadAsync(file, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        upload.ReplyUploaderId = this.UserId();
        db.MailAttachmentUploads.Add(upload);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MailAttachments.Describe(upload));
    }

    [HttpGet("attachments/{attachmentId:guid}")]
    public async Task<IActionResult> DownloadAttachment(string source, int id, Guid attachmentId, CancellationToken cancellationToken)
    {
        var conversation = await FindConversation(source, id, includeBody: false);
        if (conversation is null) return NotFound();
        var authorId = this.UserId();
        var visible = VisibleReplies(conversation);
        var file = await db.MailAttachmentUploads.AsNoTracking().Where(a => a.Id == attachmentId &&
                ((a.ReplyUploaderId == authorId && !a.MailConversationReplies.Any()) ||
                 a.MailConversationReplies.Any(r => visible.Any(v => v.Id == r.Id))))
            .Select(a => new { a.FileName, a.Content.Bytes }).SingleOrDefaultAsync(cancellationToken);
        return file is null ? NotFound() : File(file.Bytes, "application/octet-stream", file.FileName);
    }

    [HttpPost("reply")]
    [RequestSizeLimit(16_000_000)]
    public async Task<IActionResult> Reply(string source, int id, ReplyRequest req)
    {
        var forwarding = req.ForwardTo is not null;
        var reacting = req.Emoji is not null;
        if ((forwarding || reacting) && (!IsAdmin || source == "inbox")) return Forbid();
        if (reacting && (forwarding || !ReactionEmoji.Contains(req.Emoji!) || req.AttachmentIds?.Count > 0 ||
            req.BodyHtml is not null || !string.IsNullOrWhiteSpace(req.Body)))
            return BadRequest(new { message = "Choose a supported emoji for the reaction." });

        var conversation = await FindConversation(source, id, includeBody: forwarding && req.ParentId is null);
        if (conversation is null) return NotFound(new { message = "This message has no available conversation." });
        var body = req.Body?.Trim() ?? "";
        if (req.Id == Guid.Empty) return BadRequest(new { message = "The reply is missing its identifier." });

        var authorId = this.UserId();
        var authorName = await db.Users.AsNoTracking().Where(u => u.Id == authorId)
            .Select(u => u.FullName).SingleOrDefaultAsync();
        if (authorName is null) return Forbid();
        var query = VisibleReplies(conversation);
        if (await query.AnyAsync(r => r.Id == req.Id && r.AuthorId == authorId)) return Accepted();
        var parent = req.ParentId is null ? null : await query.SingleOrDefaultAsync(r =>
            r.Id == req.ParentId && r.Status == MailSendStatus.Sent && r.Kind != "Forward");
        if (req.ParentId is not null && parent is null)
            return BadRequest(new { message = "Choose a reply that has been sent." });

        string? bodyHtml = null;
        List<MailAttachmentUpload> attachments;
        try
        {
            var (html, images) = InvitationMessageBuilder.RenderBody(req.BodyHtml);
            if (req.BodyHtml is not null)
            {
                bodyHtml = InvitationMessageBuilder.EditableBody(html, images);
                // Count the sanitized text before embedding the base64 images.
                body = InvitationMessageBuilder.PlainText($"<div>{html}</div>");
            }
            var ids = req.AttachmentIds ?? [];
            attachments = MailAttachments.SelectFiles(ids, await MailAttachments.FindAsync(db, ids, authorId),
                images.Values.Sum(bytes => (long)bytes.Length));
            if (forwarding)
            {
                var original = await ForwardContent(conversation, parent);
                var combined = new XElement("div");
                if (bodyHtml is not null) combined.Add(XElement.Parse(bodyHtml));
                else if (body.Length != 0) combined.Add(new XElement("p", body));
                combined.Add(new XElement("blockquote", new XElement("p", $"Forwarded message: {conversation.Subject}"), original.Body));
                var (forwardHtml, forwardImages) = InvitationMessageBuilder.RenderBody(combined.ToString(SaveOptions.DisableFormatting));
                bodyHtml = InvitationMessageBuilder.EditableBody(forwardHtml, forwardImages);
                var allFiles = attachments.Concat(original.Files).DistinctBy(file => file.Id).ToDictionary(file => file.Id);
                attachments = MailAttachments.SelectFiles(allFiles.Keys, allFiles, forwardImages.Values.Sum(bytes => (long)bytes.Length));
            }
            else if (reacting) body = $"{authorName} reacted {req.Emoji}";
            else if (body.Length == 0 && images.Count == 0 && attachments.Count == 0)
                return BadRequest(new { message = "Write a reply, insert an image, or attach a file." });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (XmlException) { return BadRequest(new { message = "The original message formatting could not be forwarded." }); }

        var privateDeliveryId = conversation.DeliveryId ?? parent?.PrivateDeliveryId;
        var replyTo = conversation.OrganizerEmail ?? InvitationMessageBuilder.Address(email.FromAddress);
        var recipients = conversation.Recipients;
        if (forwarding)
        {
            var addresses = req.ForwardTo!.Select(InvitationMessageBuilder.Address).ToArray();
            if (addresses.Length is 0 or > 500 || addresses.Any(address => address is null))
                return BadRequest(new { message = "Enter 1 to 500 valid email addresses to forward this message." });
            recipients = addresses.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            replyTo = await UserEmail(authorId) ?? InvitationMessageBuilder.Address(email.FromAddress);
        }
        else if (conversation.DeliveryId is not null)
        {
            // A recipient replies only to the organizer, even when the original was sent to a group.
            var organizer = conversation.OrganizerEmail ?? InvitationMessageBuilder.Address(email.FromAddress);
            if (organizer is null) return BadRequest(new { message = "The organizer has no email address for replies." });
            recipients = [organizer];
            replyTo = conversation.ParticipantEmail;
        }
        else if (privateDeliveryId is int deliveryId)
        {
            var address = await db.MailPlanDeliveries.Where(d => d.Id == deliveryId && d.SendId == conversation.SendId)
                .Select(d => d.Email).SingleOrDefaultAsync();
            if (address is null) return NotFound();
            recipients = [address];
        }
        if (recipients.Length == 0)
            return BadRequest(new { message = "There are no email recipients for this reply." });

        var references = parent is null
            ? new[] { conversation.MessageId }
            : new[] { conversation.MessageId }.Concat(JsonSerializer.Deserialize<string[]>(parent.ReferencesJson)!
                .Where(reference => reference != conversation.MessageId).TakeLast(30)).Append(parent.MessageId).ToArray();
        var now = DateTimeOffset.UtcNow;
        var reply = new MailConversationReply
        {
            Id = req.Id,
            InvitationEmailId = conversation.InvitationId,
            MailPlanSendId = conversation.SendId,
            PrivateDeliveryId = privateDeliveryId,
            AuthorId = authorId,
            AuthorName = authorName,
            ReplyToAddress = replyTo,
            Body = body,
            BodyHtml = bodyHtml,
            Kind = forwarding ? "Forward" : reacting ? "Reaction" : "Reply",
            Subject = PrefixSubject(conversation.Subject, forwarding ? "Fwd: " : "Re: "),
            MessageId = email.NewMessageId(),
            ParentMessageId = parent?.MessageId ?? conversation.MessageId,
            ReferencesJson = JsonSerializer.Serialize(references),
            RecipientsJson = JsonSerializer.Serialize(recipients),
            CreatedAt = now,
            NextAttemptAt = now,
        };
        MailAttachments.Replace(reply.Attachments, attachments);
        // A message can be recycled while its composer is open. Hold the parent
        // read lock until the queued reply is saved.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var originalAvailable = conversation.InvitationId is int invitationId
            ? await db.InvitationEmails.AnyAsync(m => m.Id == invitationId && m.RecycledAt == null)
            : await db.MailPlanSends.AnyAsync(s => s.Id == conversation.SendId && s.RecycledAt == null);
        if (!originalAvailable) return Conflict(new { message = "This email is in Recycle or has been permanently deleted." });
        db.MailConversationReplies.Add(reply);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            if (!await query.AnyAsync(r => r.Id == req.Id && r.AuthorId == authorId)) throw;
        }
        await transaction.CommitAsync();
        return Accepted();
    }

    private static string PrefixSubject(string subject, string prefix) =>
        subject.StartsWith(prefix.TrimEnd(), StringComparison.OrdinalIgnoreCase) ? subject : prefix + subject;

    private async Task<(XElement Body, List<MailAttachmentUpload> Files)> ForwardContent(Conversation conversation,
        MailConversationReply? parent)
    {
        if (parent is not null)
        {
            var files = await db.Entry(parent).Collection(r => r.Attachments).Query().ToListAsync();
            var content = parent.BodyHtml is null ? new XElement("p", parent.Body) : XElement.Parse(parent.BodyHtml);
            return (content, files);
        }

        var original = string.IsNullOrWhiteSpace(conversation.BodyHtml)
            ? new XElement("div") : XElement.Parse(conversation.BodyHtml);
        if (conversation.InvitationId is int invitationId)
        {
            var invitation = await db.InvitationEmails.Include(m => m.Event).Include(m => m.Attachments)
                .SingleAsync(m => m.Id == invitationId);
            var ev = invitation.Event!;
            var frontend = config["App:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:4200";
            var registerUrl = $"{frontend}/e/{Uri.EscapeDataString(ev.PublicSlug)}";
            var sendAt = invitation.ScheduledAt ?? invitation.SentAt ?? invitation.CreatedAt;
            original = XElement.Parse(InvitationMessageBuilder.InjectScheduleDay(original.ToString(), sendAt));
            original.Add(new XElement("h2", ev.Title),
                new XElement("p", EmailTemplates.WhenText(ev.StartsAt, ev.EndsAt)));
            if (!string.IsNullOrWhiteSpace(ev.Venue)) original.Add(new XElement("p", ev.Venue));
            if (!string.IsNullOrWhiteSpace(ev.Description)) original.Add(new XElement("p", ev.Description));
            original.Add(new XElement("p", new XElement("a", new XAttribute("href", registerUrl), "View event and register")));
            return (original, invitation.Attachments.ToList());
        }

        var send = await db.MailPlanSends.Include(s => s.Attachments).Include(s => s.Plan).ThenInclude(p => p!.Event)
            .SingleAsync(s => s.Id == conversation.SendId);
        if (send.Plan?.Event is { } planEvent)
        {
            original.Add(new XElement("h2", planEvent.Title),
                new XElement("p", EmailTemplates.WhenText(planEvent.StartsAt, planEvent.EndsAt)));
            if (!string.IsNullOrWhiteSpace(planEvent.Venue)) original.Add(new XElement("p", planEvent.Venue));
        }
        return (original, send.Attachments.ToList());
    }

    private IQueryable<MailConversationReply> VisibleReplies(Conversation c)
    {
        var authorId = this.UserId();
        var visible = db.MailConversationReplies.Where(r => r.Kind != "Forward" || r.AuthorId == authorId);
        if (c.InvitationId is int invitationId)
            return visible.Where(r => r.InvitationEmailId == invitationId);
        var replies = visible.Where(r => r.MailPlanSendId == c.SendId);
        return c.IsOrganizer ? replies : replies.Where(r => r.PrivateDeliveryId == null ||
            (c.DeliveryId != null && r.PrivateDeliveryId == c.DeliveryId));
    }

    private async Task<Conversation?> FindConversation(string source, int id, bool includeBody)
    {
        var admin = IsAdmin;
        if (source == "invitation" && admin)
        {
            var invitation = await db.InvitationEmails.AsNoTracking().Where(m => m.Id == id && m.RecycledAt == null &&
                m.Status == InvitationEmailStatus.Sent && m.SmtpMessageId != null)
                .Select(m => new { m.Subject, BodyHtml = includeBody ? m.BodyHtml : null, m.SmtpMessageId,
                    m.ReplyToAddress, m.DeliveredRecipientsJson, m.ToJson, m.CcJson }).SingleOrDefaultAsync();
            if (invitation is null) return null;
            var recipients = invitation.DeliveredRecipientsJson is { } delivered
                ? JsonSerializer.Deserialize<string[]>(delivered) ?? []
                : (JsonSerializer.Deserialize<string[]>(invitation.ToJson) ?? [])
                    .Concat(JsonSerializer.Deserialize<string[]>(invitation.CcJson) ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new(invitation.Subject, invitation.BodyHtml, invitation.SmtpMessageId!, id, null, null, false, invitation.ReplyToAddress, null,
                recipients.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        if (source == "plan" && admin)
        {
            var send = await db.MailPlanSends.AsNoTracking()
                .Where(s => s.Id == id && s.RecycledAt == null && s.Status == MailSendStatus.Sent && s.SmtpMessageId != null)
                .Select(s => new { s.Subject, BodyHtml = includeBody ? s.BodyHtml : null, s.SmtpMessageId,
                    s.ReplyToAddress, s.Plan!.CreatedByUserId }).SingleOrDefaultAsync();
            if (send is null) return null;
            var recipients = await db.MailPlanDeliveries.Where(d => d.SendId == id && d.EmailSentAt != null)
                .OrderBy(d => d.Email).Select(d => d.Email).ToArrayAsync();
            return new(send.Subject, send.BodyHtml, send.SmtpMessageId!, null, id, null,
                send.CreatedByUserId == this.UserId(), send.ReplyToAddress, null, recipients);
        }
        if (source != "inbox") return null;

        var userId = this.UserId();
        var currentEmail = await UserEmail(userId);
        if (currentEmail is null) return null;
        var delivery = await db.MailPlanDeliveries.AsNoTracking().Where(d => d.Id == id && d.Send!.RecycledAt == null &&
                d.Email == currentEmail && (d.UserId == null || d.UserId == userId) && d.EmailSentAt != null &&
                d.Send!.SmtpMessageId != null)
            .Select(d => new { d.SendId, d.Send!.Subject, BodyHtml = includeBody ? d.Send.BodyHtml : null,
                d.Send.SmtpMessageId, d.Send.ReplyToAddress })
            .SingleOrDefaultAsync();
        if (delivery is null) return null;
        return new(delivery.Subject, delivery.BodyHtml, delivery.SmtpMessageId!, null, delivery.SendId, id, false,
            delivery.ReplyToAddress, currentEmail, []);
    }

    private async Task<string?> UserEmail(int userId) =>
        InvitationMessageBuilder.Address(await db.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync());
}

using System.ComponentModel.DataAnnotations;

namespace EventRegistration.Api.Models;

public sealed class MailConversationReply
{
    public Guid Id { get; set; }
    public int? InvitationEmailId { get; set; }
    public InvitationEmail? InvitationEmail { get; set; }
    public int? MailPlanSendId { get; set; }
    public MailPlanSend? MailPlanSend { get; set; }
    public int? PrivateDeliveryId { get; set; }
    public int AuthorId { get; set; }
    [MaxLength(250)] public string AuthorName { get; set; } = "";
    [MaxLength(256)] public string? ReplyToAddress { get; set; }
    public string Body { get; set; } = "";
    public string? BodyHtml { get; set; }
    [MaxLength(12)] public string Kind { get; set; } = "Reply";
    public ICollection<MailAttachmentUpload> Attachments { get; } = new List<MailAttachmentUpload>();
    [MaxLength(204)] public string Subject { get; set; } = "";
    [MaxLength(255)] public string MessageId { get; set; } = "";
    [MaxLength(255)] public string ParentMessageId { get; set; } = "";
    public string ReferencesJson { get; set; } = "[]";
    public string RecipientsJson { get; set; } = "[]";
    public string? DeliveredJson { get; set; }
    [MaxLength(20)] public string Status { get; set; } = MailSendStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int Attempts { get; set; }
}

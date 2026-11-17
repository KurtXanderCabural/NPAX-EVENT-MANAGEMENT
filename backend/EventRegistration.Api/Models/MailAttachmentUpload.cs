using System.ComponentModel.DataAnnotations;

namespace EventRegistration.Api.Models;

public sealed class MailAttachmentUpload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(150)] public string FileName { get; set; } = "";
    [MaxLength(120)] public string ContentType { get; set; } = "";
    public int Size { get; set; }
    // Set for uploads made from a reply composer; these files are private to their thread.
    public int? ReplyUploaderId { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public MailAttachmentContent Content { get; set; } = null!;
    public ICollection<InvitationEmail> InvitationEmails { get; } = new List<InvitationEmail>();
    public ICollection<MailPlanSend> MailPlanSends { get; } = new List<MailPlanSend>();
    public ICollection<MailConversationReply> MailConversationReplies { get; } = new List<MailConversationReply>();
}

public sealed class MailAttachmentContent
{
    public Guid Id { get; set; }
    public byte[] Bytes { get; set; } = [];
}

using EventRegistration.Api.Data;
using EventRegistration.Api.Models;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace EventRegistration.Api.Services;

public record MailAttachmentDto(Guid Id, string FileName, int Size);

public static class MailAttachments
{
    public const int MaxBytes = 25_000_000;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static MailAttachmentDto Describe(MailAttachmentUpload file) => new(file.Id, file.FileName, file.Size);

    public static async Task<MailAttachmentUpload> ReadUploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var name = file.FileName.Split('/', '\\').Last().Trim();
        if (name.Length is 0 or > 150 || name is "." or ".." || name.Any(char.IsControl) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Choose a file with a valid name of up to 150 characters.");
        if (file.Length == 0) throw new ArgumentException("Empty files cannot be attached.");
        ValidateTotal(file.Length, 0);
        using var data = new MemoryStream((int)file.Length);
        await file.CopyToAsync(data, cancellationToken);
        return new MailAttachmentUpload
        {
            FileName = name,
            ContentType = ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream",
            Size = (int)data.Length,
            Content = new MailAttachmentContent { Bytes = data.ToArray() },
        };
    }

    public static async Task<Dictionary<Guid, MailAttachmentUpload>> FindAsync(AppDbContext db, IEnumerable<Guid> ids, int? replyUploaderId = null)
    {
        var keys = ids.Distinct().ToArray();
        return keys.Length == 0 ? [] : await db.MailAttachmentUploads.Where(file =>
                keys.Contains(file.Id) && file.ReplyUploaderId == replyUploaderId)
            .ToDictionaryAsync(file => file.Id);
    }

    public static List<MailAttachmentUpload> SelectFiles(IEnumerable<Guid> ids,
        IReadOnlyDictionary<Guid, MailAttachmentUpload> files, long inlineImageBytes)
    {
        var selected = new List<MailAttachmentUpload>();
        foreach (var id in ids.Distinct())
        {
            if (!files.TryGetValue(id, out var file))
                throw new ArgumentException("An attachment is unavailable. Remove it and attach the file again.");
            selected.Add(file);
        }
        ValidateTotal(selected.Sum(file => (long)file.Size), inlineImageBytes);
        return selected;
    }

    private static void ValidateTotal(long files, long images)
    {
        if (files + images > MaxBytes)
            throw new ArgumentException("Attached files and inline images must total 25 MB or less per email.");
    }

    public static async Task<EmailAttachment[]> LoadForDeliveryAsync(IQueryable<MailAttachmentUpload> files) =>
        await files.OrderBy(file => file.UploadedAt).ThenBy(file => file.Id)
            .Select(file => new EmailAttachment(file.FileName, file.Content.Bytes, file.ContentType)).ToArrayAsync();

    public static void Replace(ICollection<MailAttachmentUpload> target, IReadOnlyCollection<MailAttachmentUpload> files)
    {
        foreach (var previous in target.Except(files).ToList()) target.Remove(previous);
        foreach (var file in files.Except(target)) target.Add(file);
    }
}

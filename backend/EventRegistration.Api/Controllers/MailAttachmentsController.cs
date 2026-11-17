using EventRegistration.Api.Data;
using EventRegistration.Api.Models;
using EventRegistration.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventRegistration.Api.Controllers;

[ApiController]
[Route("api/mail-attachments")]
[Authorize(Roles = Roles.Admin + "," + Roles.SuperAdmin)]
public class MailAttachmentsController(AppDbContext db) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(MailAttachments.MaxBytes + 100_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = MailAttachments.MaxBytes)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        MailAttachmentUpload upload;
        try { upload = await MailAttachments.ReadUploadAsync(file, cancellationToken); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        db.MailAttachmentUploads.Add(upload);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MailAttachments.Describe(upload));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var file = await db.MailAttachmentUploads.AsNoTracking().Where(file => file.Id == id && file.ReplyUploaderId == null)
            .Select(file => new { file.FileName, file.Content.Bytes }).SingleOrDefaultAsync(cancellationToken);
        return file is null ? NotFound() : File(file.Bytes, "application/octet-stream", file.FileName);
    }
}

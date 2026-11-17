using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRegistration.Api.Migrations
{
    /// <inheritdoc />
    public partial class MailMessageAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailAttachmentUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Size = table.Column<int>(type: "int", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAttachmentUploads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvitationEmailAttachments",
                columns: table => new
                {
                    AttachmentsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationEmailsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvitationEmailAttachments", x => new { x.AttachmentsId, x.InvitationEmailsId });
                    table.ForeignKey(
                        name: "FK_InvitationEmailAttachments_InvitationEmails_InvitationEmailsId",
                        column: x => x.InvitationEmailsId,
                        principalTable: "InvitationEmails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvitationEmailAttachments_MailAttachmentUploads_AttachmentsId",
                        column: x => x.AttachmentsId,
                        principalTable: "MailAttachmentUploads",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MailAttachmentContent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAttachmentContent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailAttachmentContent_MailAttachmentUploads_Id",
                        column: x => x.Id,
                        principalTable: "MailAttachmentUploads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailPlanSendAttachments",
                columns: table => new
                {
                    AttachmentsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MailPlanSendsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailPlanSendAttachments", x => new { x.AttachmentsId, x.MailPlanSendsId });
                    table.ForeignKey(
                        name: "FK_MailPlanSendAttachments_MailAttachmentUploads_AttachmentsId",
                        column: x => x.AttachmentsId,
                        principalTable: "MailAttachmentUploads",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MailPlanSendAttachments_MailPlanSends_MailPlanSendsId",
                        column: x => x.MailPlanSendsId,
                        principalTable: "MailPlanSends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvitationEmailAttachments_InvitationEmailsId",
                table: "InvitationEmailAttachments",
                column: "InvitationEmailsId");

            migrationBuilder.CreateIndex(
                name: "IX_MailAttachmentUploads_UploadedAt",
                table: "MailAttachmentUploads",
                column: "UploadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MailPlanSendAttachments_MailPlanSendsId",
                table: "MailPlanSendAttachments",
                column: "MailPlanSendsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvitationEmailAttachments");

            migrationBuilder.DropTable(
                name: "MailAttachmentContent");

            migrationBuilder.DropTable(
                name: "MailPlanSendAttachments");

            migrationBuilder.DropTable(
                name: "MailAttachmentUploads");
        }
    }
}

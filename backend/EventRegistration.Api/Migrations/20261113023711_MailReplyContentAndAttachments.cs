using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRegistration.Api.Migrations
{
    /// <inheritdoc />
    public partial class MailReplyContentAndAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyHtml",
                table: "MailConversationReplies",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplyUploaderId",
                table: "MailAttachmentUploads",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MailConversationReplyAttachments",
                columns: table => new
                {
                    AttachmentsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MailConversationRepliesId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailConversationReplyAttachments", x => new { x.AttachmentsId, x.MailConversationRepliesId });
                    table.ForeignKey(
                        name: "FK_MailConversationReplyAttachments_MailAttachmentUploads_AttachmentsId",
                        column: x => x.AttachmentsId,
                        principalTable: "MailAttachmentUploads",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MailConversationReplyAttachments_MailConversationReplies_MailConversationRepliesId",
                        column: x => x.MailConversationRepliesId,
                        principalTable: "MailConversationReplies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailConversationReplyAttachments_MailConversationRepliesId",
                table: "MailConversationReplyAttachments",
                column: "MailConversationRepliesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailConversationReplyAttachments");

            migrationBuilder.DropColumn(
                name: "BodyHtml",
                table: "MailConversationReplies");

            migrationBuilder.DropColumn(
                name: "ReplyUploaderId",
                table: "MailAttachmentUploads");
        }
    }
}

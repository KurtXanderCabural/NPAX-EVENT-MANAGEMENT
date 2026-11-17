using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRegistration.Api.Migrations
{
    /// <inheritdoc />
    public partial class MailConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplyToAddress",
                table: "MailPlanSends",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpMessageId",
                table: "MailPlanSends",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToAddress",
                table: "InvitationEmails",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpMessageId",
                table: "InvitationEmails",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MailConversationReplies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationEmailId = table.Column<int>(type: "int", nullable: true),
                    MailPlanSendId = table.Column<int>(type: "int", nullable: true),
                    PrivateDeliveryId = table.Column<int>(type: "int", nullable: true),
                    AuthorId = table.Column<int>(type: "int", nullable: false),
                    AuthorName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ReplyToAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(204)", maxLength: 204, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ParentMessageId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ReferencesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecipientsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeliveredJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailConversationReplies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailConversationReplies_InvitationEmails_InvitationEmailId",
                        column: x => x.InvitationEmailId,
                        principalTable: "InvitationEmails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailConversationReplies_MailPlanSends_MailPlanSendId",
                        column: x => x.MailPlanSendId,
                        principalTable: "MailPlanSends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailConversationReplies_InvitationEmailId",
                table: "MailConversationReplies",
                column: "InvitationEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_MailConversationReplies_MailPlanSendId",
                table: "MailConversationReplies",
                column: "MailPlanSendId");

            migrationBuilder.CreateIndex(
                name: "IX_MailConversationReplies_Status_NextAttemptAt",
                table: "MailConversationReplies",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailConversationReplies");

            migrationBuilder.DropColumn(
                name: "ReplyToAddress",
                table: "MailPlanSends");

            migrationBuilder.DropColumn(
                name: "SmtpMessageId",
                table: "MailPlanSends");

            migrationBuilder.DropColumn(
                name: "ReplyToAddress",
                table: "InvitationEmails");

            migrationBuilder.DropColumn(
                name: "SmtpMessageId",
                table: "InvitationEmails");
        }
    }
}

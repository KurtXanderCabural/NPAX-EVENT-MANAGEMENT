using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRegistration.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHiddenMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HiddenMails");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HiddenMails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvitationEmailId = table.Column<int>(type: "int", nullable: true),
                    MailPlanId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiddenMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HiddenMails_InvitationEmails_InvitationEmailId",
                        column: x => x.InvitationEmailId,
                        principalTable: "InvitationEmails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HiddenMails_MailPlans_MailPlanId",
                        column: x => x.MailPlanId,
                        principalTable: "MailPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HiddenMails_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HiddenMails_InvitationEmailId",
                table: "HiddenMails",
                column: "InvitationEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_HiddenMails_MailPlanId",
                table: "HiddenMails",
                column: "MailPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_HiddenMails_UserId_InvitationEmailId",
                table: "HiddenMails",
                columns: new[] { "UserId", "InvitationEmailId" },
                unique: true,
                filter: "[InvitationEmailId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HiddenMails_UserId_MailPlanId",
                table: "HiddenMails",
                columns: new[] { "UserId", "MailPlanId" },
                unique: true,
                filter: "[MailPlanId] IS NOT NULL");
        }
    }
}

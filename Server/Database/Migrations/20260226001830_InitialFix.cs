using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SFDRN.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_Recipient_Delivered",
                table: "Messages");

            migrationBuilder.RenameColumn(
                name: "Ttl",
                table: "Messages",
                newName: "TtlHops");

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "Messages",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ContentType",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadAt",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TtlSeconds",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 604800);

            migrationBuilder.CreateTable(
                name: "MessageStatusHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageStatusHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ContentHash",
                table: "Messages",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Expiration",
                table: "Messages",
                columns: new[] { "StoredAt", "TtlSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Recipient_Status",
                table: "Messages",
                columns: new[] { "ToNodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StatusHistory_Message_Timestamp",
                table: "MessageStatusHistory",
                columns: new[] { "MessageId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_StatusHistory_MessageId",
                table: "MessageStatusHistory",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageStatusHistory");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ContentHash",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_Expiration",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_Recipient_Status",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "TtlSeconds",
                table: "Messages");

            migrationBuilder.RenameColumn(
                name: "TtlHops",
                table: "Messages",
                newName: "Ttl");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Recipient_Delivered",
                table: "Messages",
                columns: new[] { "ToNodeId", "DeliveredAt" });
        }
    }
}

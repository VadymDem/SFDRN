using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SFDRN.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialProfileStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FromNodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ToNodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EncryptedPayload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    StoredAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    Ttl = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 10)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    GlobalNickname = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: "Hey! I'm using SFDRN"),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DiscoveredAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    Avatar = table.Column<string>(type: "TEXT", maxLength: 100000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.NodeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Recipient_Delivered",
                table: "Messages",
                columns: new[] { "ToNodeId", "DeliveredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Recipient_Timestamp",
                table: "Messages",
                columns: new[] { "ToNodeId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_StoredAt",
                table: "Messages",
                column: "StoredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_GlobalNickname",
                table: "Profiles",
                column: "GlobalNickname");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Hash",
                table: "Profiles",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_LastSeenAt",
                table: "Profiles",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_LastUpdated",
                table: "Profiles",
                column: "LastUpdated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}

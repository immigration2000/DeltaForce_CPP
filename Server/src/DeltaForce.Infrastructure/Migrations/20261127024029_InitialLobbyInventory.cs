using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeltaForce.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialLobbyInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.PlayerId);
                    table.CheckConstraint("CK_Players_Revision", "[Revision] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "ItemInstances",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    CurrentDurability = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemInstances", x => x.ItemId);
                    table.UniqueConstraint("AK_ItemInstances_PlayerId_ItemId", x => new { x.PlayerId, x.ItemId });
                    table.CheckConstraint("CK_Items_Values", "[Quantity] > 0 AND [CurrentDurability] >= 0");
                    table.ForeignKey(
                        name: "FK_ItemInstances_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationRecords",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationRecords", x => new { x.PlayerId, x.RequestId });
                    table.ForeignKey(
                        name: "FK_OperationRecords_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetiredItems",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetiredItems", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_RetiredItems_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Containers",
                columns: table => new
                {
                    ContainerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LayoutId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    StorageTag = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Level = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Containers", x => x.ContainerId);
                    table.UniqueConstraint("AK_Containers_PlayerId_ContainerId", x => new { x.PlayerId, x.ContainerId });
                    table.ForeignKey(
                        name: "FK_Containers_ItemInstances_PlayerId_OwnerItemId",
                        columns: x => new { x.PlayerId, x.OwnerItemId },
                        principalTable: "ItemInstances",
                        principalColumns: new[] { "PlayerId", "ItemId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Containers_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemLocations",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GridIndex = table.Column<int>(type: "int", nullable: false),
                    X = table.Column<int>(type: "int", nullable: false),
                    Y = table.Column<int>(type: "int", nullable: false),
                    Rotated = table.Column<bool>(type: "bit", nullable: false),
                    SlotTag = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemLocations", x => x.ItemId);
                    table.CheckConstraint("CK_Location_Shape", "([Type] = 0 AND [ContainerId] IS NULL AND [SlotTag] IS NULL) OR ([Type] = 1 AND [ContainerId] IS NOT NULL AND [GridIndex] >= 0 AND [X] >= 0 AND [Y] >= 0 AND [SlotTag] IS NULL) OR ([Type] = 2 AND [ContainerId] IS NULL AND [GridIndex] = -1 AND [X] = 0 AND [Y] = 0 AND [Rotated] = 0 AND [SlotTag] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ItemLocations_Containers_PlayerId_ContainerId",
                        columns: x => new { x.PlayerId, x.ContainerId },
                        principalTable: "Containers",
                        principalColumns: new[] { "PlayerId", "ContainerId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemLocations_ItemInstances_PlayerId_ItemId",
                        columns: x => new { x.PlayerId, x.ItemId },
                        principalTable: "ItemInstances",
                        principalColumns: new[] { "PlayerId", "ItemId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Containers_OwnerItemId",
                table: "Containers",
                column: "OwnerItemId",
                unique: true,
                filter: "[OwnerItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Containers_PlayerId_OwnerItemId",
                table: "Containers",
                columns: new[] { "PlayerId", "OwnerItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemLocations_PlayerId_ContainerId",
                table: "ItemLocations",
                columns: new[] { "PlayerId", "ContainerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemLocations_PlayerId_ItemId",
                table: "ItemLocations",
                columns: new[] { "PlayerId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemLocations_PlayerId_SlotTag",
                table: "ItemLocations",
                columns: new[] { "PlayerId", "SlotTag" },
                unique: true,
                filter: "[Type] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_Players_UserId",
                table: "Players",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RetiredItems_PlayerId",
                table: "RetiredItems",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemLocations");

            migrationBuilder.DropTable(
                name: "OperationRecords");

            migrationBuilder.DropTable(
                name: "RetiredItems");

            migrationBuilder.DropTable(
                name: "Containers");

            migrationBuilder.DropTable(
                name: "ItemInstances");

            migrationBuilder.DropTable(
                name: "Players");
        }
    }
}

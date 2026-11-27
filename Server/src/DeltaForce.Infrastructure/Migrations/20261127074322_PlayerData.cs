using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeltaForce.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlayerData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarId",
                table: "Players",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DogtagImageId",
                table: "Players",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Experience",
                table: "Players",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Nickname",
                table: "Players",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "BlackSites",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StashLevel = table.Column<int>(type: "int", nullable: false),
                    CommandCenterLevel = table.Column<int>(type: "int", nullable: false),
                    TrainingCenterLevel = table.Column<int>(type: "int", nullable: false),
                    FiringRangeLevel = table.Column<int>(type: "int", nullable: false),
                    DivingCenterLevel = table.Column<int>(type: "int", nullable: false),
                    CollectionRoomLevel = table.Column<int>(type: "int", nullable: false),
                    DecontaminationRoomLevel = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlackSites", x => x.PlayerId);
                    table.CheckConstraint("CK_BlackSites_Levels", "[StashLevel] >= 0 AND [CommandCenterLevel] >= 0 AND [TrainingCenterLevel] >= 0 AND [FiringRangeLevel] >= 0 AND [DivingCenterLevel] >= 0 AND [CollectionRoomLevel] >= 0 AND [DecontaminationRoomLevel] >= 0");
                    table.ForeignKey(
                        name: "FK_BlackSites_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CraftingJobs",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FacilityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecipeId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletesAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CraftingJobs", x => x.JobId);
                    table.CheckConstraint("CK_CraftingJobs_Status", "([Status] IN (N'InProgress', N'ReadyToClaim', N'Cancelled') AND [ClaimedAt] IS NULL) OR ([Status] = N'Claimed' AND [ClaimedAt] IS NOT NULL AND [ClaimedAt] >= [CompletesAt])");
                    table.CheckConstraint("CK_CraftingJobs_Values", "[Quantity] > 0 AND [CompletesAt] >= [StartedAt]");
                    table.ForeignKey(
                        name: "FK_CraftingJobs_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlayerRuntime",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaidId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaidStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRuntime", x => x.PlayerId);
                    table.CheckConstraint("CK_PlayerRuntime_Raid", "([RaidStatus] = N'None' AND [RaidId] IS NULL) OR ([RaidStatus] IN (N'Preparing', N'Joining', N'InRaid', N'Finished') AND [RaidId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PlayerRuntime_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "PlayerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_Progression",
                table: "Players",
                sql: "[Level] >= 0 AND [Experience] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_CraftingJobs_PlayerId_Status_CompletesAt",
                table: "CraftingJobs",
                columns: new[] { "PlayerId", "Status", "CompletesAt" });

            // Populate new sections for existing accounts without replacing inventory or reservations.
            migrationBuilder.Sql("""
                UPDATE [Players] SET [Nickname] = [UserId];
                INSERT INTO [BlackSites] ([PlayerId], [StashLevel], [CommandCenterLevel], [TrainingCenterLevel],
                    [FiringRangeLevel], [DivingCenterLevel], [CollectionRoomLevel], [DecontaminationRoomLevel])
                SELECT p.[PlayerId], COALESCE((SELECT MAX(c.[Level]) FROM [Containers] c
                    WHERE c.[PlayerId] = p.[PlayerId] AND c.[StorageTag] = N'Storage.Stash.Main'), 0), 0, 0, 0, 0, 0, 0
                FROM [Players] p;
                INSERT INTO [PlayerRuntime] ([PlayerId], [CurrentPartyId], [RaidId], [RaidStatus], [UpdatedAt])
                SELECT [PlayerId], NULL, NULL, N'None', SYSDATETIMEOFFSET() FROM [Players];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlackSites");

            migrationBuilder.DropTable(
                name: "CraftingJobs");

            migrationBuilder.DropTable(
                name: "PlayerRuntime");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_Progression",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "AvatarId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "DogtagImageId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Experience",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Nickname",
                table: "Players");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PoTraffic.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BaselineSlots",
                columns: table => new
                {
                    DayOfWeek = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TimeSlotBucket = table.Column<int>(type: "int", nullable: false),
                    MeanDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    StdDevDurationSeconds = table.Column<double>(type: "float", nullable: true),
                    SessionCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateTable(
                name: "PublicHolidays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Locale = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    HolidayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    HolidayName = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicHolidays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemConfigurations",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemConfigurations", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "UserDailyUsages",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TodayPollCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsGdprDeleteRequested = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IsEmailVerified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    EmailVerificationToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RefreshToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RefreshTokenExpiry = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OriginCoordinates = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DestinationAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DestinationCoordinates = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    MonitoringStatus = table.Column<int>(type: "int", nullable: false),
                    HangfireJobChainId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Routes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    FirstPollAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastPollAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QuotaConsumed = table.Column<int>(type: "int", nullable: false),
                    PollCount = table.Column<int>(type: "int", nullable: false),
                    IsHolidayExcluded = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringSessions_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    DaysOfWeekMask = table.Column<byte>(type: "tinyint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringWindows_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PollRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TravelDurationSeconds = table.Column<int>(type: "int", nullable: false),
                    DistanceMetres = table.Column<int>(type: "int", nullable: false),
                    IsRerouted = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    RawProviderResponse = table.Column<string>(type: "nvarchar(max)", maxLength: 2147483647, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PollRecords_MonitoringSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "MonitoringSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PollRecords_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.InsertData(
                table: "SystemConfigurations",
                columns: new[] { "Key", "Description", "IsSensitive", "Value" },
                values: new object[,]
                {
                    { "cost.perpoll.googlemaps", "Cost per poll - Google Maps", false, "0.005" },
                    { "cost.perpoll.tomtom", "Cost per poll - TomTom", false, "0.004" },
                    { "quota.daily.default", "Default daily session quota per user", false, "10" },
                    { "quota.reset.utc", "Quota reset time (UTC)", false, "00:00" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringSessions_RouteId_SessionDate",
                table: "MonitoringSessions",
                columns: new[] { "RouteId", "SessionDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringWindows_RouteId",
                table: "MonitoringWindows",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_PollRecords_PolledAt",
                table: "PollRecords",
                column: "PolledAt");

            migrationBuilder.CreateIndex(
                name: "IX_PollRecords_RouteId_PolledAt",
                table: "PollRecords",
                columns: new[] { "RouteId", "PolledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PollRecords_SessionId",
                table: "PollRecords",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "UX_PublicHolidays_Locale_Date",
                table: "PublicHolidays",
                columns: new[] { "Locale", "HolidayDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_UserId_MonitoringStatus",
                table: "Routes",
                columns: new[] { "UserId", "MonitoringStatus" });

            migrationBuilder.CreateIndex(
                name: "UX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BaselineSlots");

            migrationBuilder.DropTable(
                name: "MonitoringWindows");

            migrationBuilder.DropTable(
                name: "PollRecords");

            migrationBuilder.DropTable(
                name: "PublicHolidays");

            migrationBuilder.DropTable(
                name: "SystemConfigurations");

            migrationBuilder.DropTable(
                name: "UserDailyUsages");

            migrationBuilder.DropTable(
                name: "MonitoringSessions");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}

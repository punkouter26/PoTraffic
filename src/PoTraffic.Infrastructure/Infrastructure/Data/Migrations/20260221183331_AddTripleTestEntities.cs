using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PoTraffic.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTripleTestEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TripleTestSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    OriginAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OriginCoordinates = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DestinationAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DestinationCoordinates = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripleTestSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TripleTestShots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShotIndex = table.Column<int>(type: "int", nullable: false),
                    OffsetSeconds = table.Column<int>(type: "int", nullable: false),
                    FiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: true),
                    DurationSeconds = table.Column<int>(type: "int", nullable: true),
                    DistanceMetres = table.Column<int>(type: "int", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripleTestShots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripleTestShots_TripleTestSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "TripleTestSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_TripleTestShots_SessionId_ShotIndex",
                table: "TripleTestShots",
                columns: new[] { "SessionId", "ShotIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TripleTestShots");

            migrationBuilder.DropTable(
                name: "TripleTestSessions");
        }
    }
}

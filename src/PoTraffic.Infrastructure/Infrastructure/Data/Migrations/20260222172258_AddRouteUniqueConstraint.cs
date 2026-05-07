using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PoTraffic.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteUniqueConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // T105: Soft-delete duplicate active routes before applying the filtered unique index.
            // Keep the earliest created route per (UserId, OriginAddress, DestinationAddress, Provider)
            // and mark later duplicates as Deleted (MonitoringStatus = 2).
            migrationBuilder.Sql(
                """
                ;WITH duplicate_routes AS
                (
                    SELECT
                        Id,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY UserId, OriginAddress, DestinationAddress, Provider
                            ORDER BY CreatedAt ASC, Id ASC
                        ) AS rn
                    FROM Routes
                    WHERE MonitoringStatus <> 2
                )
                UPDATE r
                SET MonitoringStatus = 2
                FROM Routes r
                INNER JOIN duplicate_routes d ON d.Id = r.Id
                WHERE d.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Routes_User_Origin_Dest_Provider",
                table: "Routes",
                columns: new[] { "UserId", "OriginAddress", "DestinationAddress", "Provider" },
                unique: true,
                filter: "[MonitoringStatus] != 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Routes_User_Origin_Dest_Provider",
                table: "Routes");
        }
    }
}

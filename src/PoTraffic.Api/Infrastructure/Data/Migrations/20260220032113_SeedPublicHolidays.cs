using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PoTraffic.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedPublicHolidays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "PublicHolidays",
                columns: new[] { "Id", "HolidayDate", "HolidayName", "Locale" },
                values: new object[,]
                {
                    { 1, new DateOnly(2025, 1, 1), "New Year's Day", "en-IE" },
                    { 2, new DateOnly(2025, 2, 3), "St. Brigid's Day", "en-IE" },
                    { 3, new DateOnly(2025, 3, 17), "St. Patrick's Day", "en-IE" },
                    { 4, new DateOnly(2025, 4, 21), "Easter Monday", "en-IE" },
                    { 5, new DateOnly(2025, 5, 5), "May Bank Holiday", "en-IE" },
                    { 6, new DateOnly(2025, 6, 2), "June Bank Holiday", "en-IE" },
                    { 7, new DateOnly(2025, 8, 4), "August Bank Holiday", "en-IE" },
                    { 8, new DateOnly(2025, 10, 27), "October Bank Holiday", "en-IE" },
                    { 9, new DateOnly(2025, 12, 25), "Christmas Day", "en-IE" },
                    { 10, new DateOnly(2025, 12, 26), "St. Stephen's Day", "en-IE" },
                    { 11, new DateOnly(2025, 1, 1), "New Year's Day", "en-GB" },
                    { 12, new DateOnly(2025, 4, 18), "Good Friday", "en-GB" },
                    { 13, new DateOnly(2025, 4, 21), "Easter Monday", "en-GB" },
                    { 14, new DateOnly(2025, 5, 5), "Early May Bank Holiday", "en-GB" },
                    { 15, new DateOnly(2025, 5, 26), "Spring Bank Holiday", "en-GB" },
                    { 16, new DateOnly(2025, 8, 25), "Summer Bank Holiday", "en-GB" },
                    { 17, new DateOnly(2025, 12, 25), "Christmas Day", "en-GB" },
                    { 18, new DateOnly(2025, 12, 26), "Boxing Day", "en-GB" },
                    { 19, new DateOnly(2025, 1, 1), "Neujahrstag", "de-DE" },
                    { 20, new DateOnly(2025, 4, 18), "Karfreitag", "de-DE" },
                    { 21, new DateOnly(2025, 4, 21), "Ostermontag", "de-DE" },
                    { 22, new DateOnly(2025, 5, 1), "Tag der Arbeit", "de-DE" },
                    { 23, new DateOnly(2025, 5, 29), "Christi Himmelfahrt", "de-DE" },
                    { 24, new DateOnly(2025, 6, 9), "Pfingstmontag", "de-DE" },
                    { 25, new DateOnly(2025, 10, 3), "Tag der Deutschen Einheit", "de-DE" },
                    { 26, new DateOnly(2025, 12, 25), "1. Weihnachtstag", "de-DE" },
                    { 27, new DateOnly(2025, 12, 26), "2. Weihnachtstag", "de-DE" },
                    { 28, new DateOnly(2025, 1, 1), "Jour de l'An", "fr-FR" },
                    { 29, new DateOnly(2025, 4, 21), "Lundi de Pâques", "fr-FR" },
                    { 30, new DateOnly(2025, 5, 1), "Fête du Travail", "fr-FR" },
                    { 31, new DateOnly(2025, 5, 8), "Victoire 1945", "fr-FR" },
                    { 32, new DateOnly(2025, 5, 29), "Ascension", "fr-FR" },
                    { 33, new DateOnly(2025, 6, 9), "Lundi de Pentecôte", "fr-FR" },
                    { 34, new DateOnly(2025, 7, 14), "Fête Nationale", "fr-FR" },
                    { 35, new DateOnly(2025, 8, 15), "Assomption", "fr-FR" },
                    { 36, new DateOnly(2025, 11, 1), "Toussaint", "fr-FR" },
                    { 37, new DateOnly(2025, 11, 11), "Armistice", "fr-FR" },
                    { 38, new DateOnly(2025, 12, 25), "Noël", "fr-FR" },
                    { 39, new DateOnly(2025, 1, 1), "New Year's Day", "en-US" },
                    { 40, new DateOnly(2025, 1, 20), "Martin Luther King Jr. Day", "en-US" },
                    { 41, new DateOnly(2025, 2, 17), "Presidents' Day", "en-US" },
                    { 42, new DateOnly(2025, 5, 26), "Memorial Day", "en-US" },
                    { 43, new DateOnly(2025, 6, 19), "Juneteenth", "en-US" },
                    { 44, new DateOnly(2025, 7, 4), "Independence Day", "en-US" },
                    { 45, new DateOnly(2025, 9, 1), "Labor Day", "en-US" },
                    { 46, new DateOnly(2025, 11, 11), "Veterans Day", "en-US" },
                    { 47, new DateOnly(2025, 11, 27), "Thanksgiving Day", "en-US" },
                    { 48, new DateOnly(2025, 12, 25), "Christmas Day", "en-US" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 16);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 18);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 19);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 20);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 21);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 22);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 23);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 24);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 25);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 26);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 27);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 28);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 29);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 30);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 31);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 32);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 33);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 34);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 35);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 36);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 37);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 38);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 39);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 40);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 41);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 42);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 43);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 44);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 45);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 46);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 47);

            migrationBuilder.DeleteData(
                table: "PublicHolidays",
                keyColumn: "Id",
                keyValue: 48);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SimpleBroker.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "accounts");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "accounts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "balances",
                schema: "accounts",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    available = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    reserved = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_balances", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "positions",
                schema: "accounts",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    instrument_id = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    avg_price = table.Column<decimal>(type: "numeric(28,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_positions", x => new { x.account_id, x.instrument_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts",
                schema: "accounts");

            migrationBuilder.DropTable(
                name: "balances",
                schema: "accounts");

            migrationBuilder.DropTable(
                name: "positions",
                schema: "accounts");
        }
    }
}

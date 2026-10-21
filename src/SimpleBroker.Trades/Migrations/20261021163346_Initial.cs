using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleBroker.Trades.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "trades");

            migrationBuilder.CreateTable(
                name: "trades",
                schema: "trades",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    instrument_id = table.Column<int>(type: "integer", nullable: false),
                    buy_order_id = table.Column<long>(type: "bigint", nullable: false),
                    sell_order_id = table.Column<long>(type: "bigint", nullable: false),
                    buy_account_id = table.Column<long>(type: "bigint", nullable: false),
                    sell_account_id = table.Column<long>(type: "bigint", nullable: false),
                    price = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    executed_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trades", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trades",
                schema: "trades");
        }
    }
}

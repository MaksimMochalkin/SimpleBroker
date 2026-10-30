using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleBroker.Trades.Migrations
{
    /// <inheritdoc />
    public partial class Stage03TradesIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_trades_buy_account_executed_at_id
                ON trades.trades (buy_account_id, executed_at DESC, id DESC);
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_trades_sell_account_executed_at_id
                ON trades.trades (sell_account_id, executed_at DESC, id DESC);
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS trades.ix_trades_buy_account_executed_at_id;",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS trades.ix_trades_sell_account_executed_at_id;",
                suppressTransaction: true);
        }
    }
}

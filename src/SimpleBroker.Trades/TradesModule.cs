namespace SimpleBroker.Trades;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SimpleBroker.Trades.Internal;

public static class TradesModule
{
    private const int _maxSize = 200;

    public static IServiceCollection AddTradesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("Broker")
                 ?? throw new InvalidOperationException("ConnectionStrings:Broker is not configured");

        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(cs));
        services.AddDbContextPool<TradesDbContext>((sp, o) =>
            TradesDbContext.Configure(o, sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<TradesService>();
        services.AddScoped<DapperTradesService>();
        return services;
    }

    public static IEndpointRouteBuilder MapTradesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/accounts/{id:long}/trades", async (
            long id,
            TradesService svc,
            CancellationToken ct,
            int size = 50,
            string? cursor = null,
            int? page = null) =>
        {
            if (size < 1 || size > _maxSize || page is < 1 || page > 1)
                return Results.BadRequest(new { error = $"size must be 1..{_maxSize}; page is deprecated, use cursor for subsequent results" });

            TradeCursorValue? decodedCursor = null;
            if (cursor is not null)
            {
                if (!TradeCursorCodec.TryDecode(cursor, out var value))
                    return Results.BadRequest(new { error = "cursor is invalid" });
                decodedCursor = value;
            }

            return Results.Ok(await svc.GetForAccountAsync(id, size, decodedCursor, ct));
        });

        return app;
    }

    public static IEndpointRouteBuilder MapDapperTradesBenchmarkEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/accounts/{id:long}/trades/dapper", async (
            long id,
            DapperTradesService svc,
            CancellationToken ct,
            int size = 50,
            string? cursor = null,
            int? page = null) =>
        {
            var validation = ValidatePaging(size, page, cursor, out var decodedCursor);
            if (validation is not null)
                return validation;

            return Results.Ok(await svc.GetForAccountAsync(id, size, decodedCursor, ct));
        });

        return app;
    }

    private static IResult? ValidatePaging(
        int size,
        int? page,
        string? cursor,
        out TradeCursorValue? decodedCursor)
    {
        decodedCursor = null;
        if (size < 1 || size > _maxSize || page is < 1 || page > 1)
            return Results.BadRequest(new { error = $"size must be 1..{_maxSize}; page is deprecated, use cursor for subsequent results" });

        if (cursor is not null)
        {
            if (!TradeCursorCodec.TryDecode(cursor, out var value))
                return Results.BadRequest(new { error = "cursor is invalid" });
            decodedCursor = value;
        }

        return null;
    }
}

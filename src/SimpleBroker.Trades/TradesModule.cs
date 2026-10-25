namespace SimpleBroker.Trades;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleBroker.Trades.Internal;
public static class TradesModule
{
    private const int _maxPage = 100_000;
    private const int _maxSize = 200;

    public static IServiceCollection AddTradesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("Broker")
                 ?? throw new InvalidOperationException("ConnectionStrings:Broker is not configured");

        services.AddDbContextPool<TradesDbContext>(o => TradesDbContext.Configure(o, cs));
        services.AddScoped<TradesService>();
        return services;
    }

    public static IEndpointRouteBuilder MapTradesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/accounts/{id:long}/trades", async (long id, TradesService svc, CancellationToken ct, int page = 1, int size = 50) =>
        {
            if (page < 1 || page > _maxPage || size < 1 || size > _maxSize)
                return Results.BadRequest(new { error = $"page must be 1..{_maxPage}, size 1..{_maxSize}" });

            return Results.Ok(await svc.GetForAccountAsync(id, page, size, ct));
        });

        return app;
    }
}

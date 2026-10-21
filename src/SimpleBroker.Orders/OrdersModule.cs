using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleBroker.Orders.Contracts;
using SimpleBroker.Orders.Internal;

namespace SimpleBroker.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("Broker")
                 ?? throw new InvalidOperationException("ConnectionStrings:Broker is not configured");

        services.AddDbContextPool<OrdersDbContext>(o => OrdersDbContext.Configure(o, cs));
        services.AddScoped<OrdersService>();
        return services;
    }

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders", async (CreateOrderRequest req, OrdersService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateAsync(req, ct);
            return result.Outcome switch
            {
                CreateOrderOutcome.Created => Results.Created($"/orders/{result.Id}", new OrderCreatedResponse(result.Id!.Value, "New")),
                CreateOrderOutcome.Invalid => Results.BadRequest(new { error = result.Error }),
                _ => Results.NotFound(new { error = result.Error }),
            };
        });

        return app;
    }
}

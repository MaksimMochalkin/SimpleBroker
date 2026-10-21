namespace SimpleBroker.Accounts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Accounts.Internal;

public static class AccountsModule
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("Broker")
                 ?? throw new InvalidOperationException("ConnectionStrings:Broker is not configured");

        services.AddDbContextPool<AccountsDbContext>(o => AccountsDbContext.Configure(o, cs));
        services.AddScoped<AccountsService>();
        services.AddScoped<IAccountsApi>(sp => sp.GetRequiredService<AccountsService>());
        return services;
    }

    public static IEndpointRouteBuilder MapAccountsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/accounts", async (CreateAccountRequest req, AccountsService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Owner) || req.Owner.Length > 200)
                return Results.BadRequest(new { error = "owner is required (max 200 chars)" });
            if (req.Currency is not { Length: 3 })
                return Results.BadRequest(new { error = "currency must be a 3-letter code" });

            var id = await svc.CreateAsync(req.Owner.Trim(), req.Currency.ToUpperInvariant(), ct);
            return Results.Created($"/accounts/{id}/balance", new AccountCreatedResponse(id));
        });

        app.MapPost("/accounts/{id:long}/deposit", async (long id, DepositRequest req, AccountsService svc, CancellationToken ct) =>
        {
            if (req.Amount <= 0)
                return Results.BadRequest(new { error = "amount must be positive" });

            var balance = await svc.DepositAsync(id, req.Amount, ct);
            return balance is null ? Results.NotFound() : Results.Ok(balance);
        });

        app.MapGet("/accounts/{id:long}/balance", async (long id, AccountsService svc, CancellationToken ct) =>
        {
            var balance = await svc.GetBalanceAsync(id, ct);
            return balance is null ? Results.NotFound() : Results.Ok(balance);
        });

        return app;
    }
}

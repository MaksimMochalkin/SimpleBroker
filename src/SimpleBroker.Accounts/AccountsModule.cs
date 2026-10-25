namespace SimpleBroker.Accounts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Accounts.Internal;
using SimpleBroker.Platform;
using SimpleBroker.Platform.Outbox;

public static class AccountsModule
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("Broker")
                 ?? throw new InvalidOperationException("ConnectionStrings:Broker is not configured");

        services.AddDbContextPool<AccountsDbContext>(o => AccountsDbContext.Configure(o, cs));
        services.AddScoped<AccountsService>();
        services.AddScoped<IAccountsApi>(sp => sp.GetRequiredService<AccountsService>());
        services.AddOutboxPublisher<AccountsDbContext>();
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

        app.MapPost("/accounts/{id:long}/withdraw",
            async (long id, WithdrawRequest req, string? mode, AccountsService svc, IConfiguration cfg, CancellationToken ct) =>
            {
                if (req.Amount <= 0)
                    return Results.BadRequest(new { error = "amount must be positive" });
                if (!TryResolveMode(mode, cfg, "WithdrawMode", WithdrawMode.Atomic, out var m))
                    return Results.BadRequest(new { error = "unknown mode" });

                try
                {
                    var r = await svc.WithdrawAsync(id, req.Amount, m, ct);
                    return r.Outcome switch
                    {
                        WithdrawOutcome.Ok => Results.Ok(r.Balance),
                        WithdrawOutcome.NotFound => Results.NotFound(),
                        WithdrawOutcome.Insufficient => Results.UnprocessableEntity(new { error = "insufficient funds" }),
                        _ => Results.Conflict(new { error = "conflict" }),
                    };
                }
                catch (Exception e) when (TxRetry.IsRetryable(e))
                {
                    return Results.Conflict(new { error = TxRetry.IsDeadlock(e) ? "deadlock" : "serialization failure" });
                }
            });

        app.MapPost("/transfers",
            async (TransferRequest req, string? mode, AccountsService svc, IConfiguration cfg, CancellationToken ct) =>
            {
                if (req.Amount <= 0)
                    return Results.BadRequest(new { error = "amount must be positive" });
                if (req.FromAccountId == req.ToAccountId)
                    return Results.BadRequest(new { error = "accounts must differ" });
                if (!TryResolveMode(mode, cfg, "TransferMode", TransferMode.Ordered, out var m))
                    return Results.BadRequest(new { error = "unknown mode" });

                try
                {
                    var o = await svc.TransferAsync(req.FromAccountId, req.ToAccountId, req.Amount, m, ct);
                    return o switch
                    {
                        TransferOutcome.Ok => Results.Ok(new TransferResponse(req.FromAccountId, req.ToAccountId, req.Amount)),
                        TransferOutcome.NotFound => Results.NotFound(),
                        _ => Results.UnprocessableEntity(new { error = "insufficient funds" }),
                    };
                }
                catch (Exception e) when (TxRetry.IsRetryable(e))
                {
                    return Results.Conflict(new { error = TxRetry.IsDeadlock(e) ? "deadlock" : "serialization failure" });
                }
            });

        app.MapGet("/accounts/{id:long}/balance", async (long id, AccountsService svc, CancellationToken ct) =>
        {
            var balance = await svc.GetBalanceAsync(id, ct);
            return balance is null ? Results.NotFound() : Results.Ok(balance);
        });

        return app;
    }

    /// <summary>
    /// Default comes from configuration (Accounts:{key}); the ?mode= query override works only when
    /// Accounts:AllowModeOverride is true (tests, tools/Contention, load tests). False = unknown mode name.
    /// </summary>
    private static bool TryResolveMode<T>(string? query, IConfiguration cfg, string key, T fallback, out T mode) where T : struct, Enum
    {
        mode = Enum.TryParse(cfg[$"Accounts:{key}"], true, out T configured) && Enum.IsDefined(configured) ? configured : fallback;

        var allowOverride = bool.TryParse(cfg["Accounts:AllowModeOverride"], out var allowed) && allowed;
        if (query is null || !allowOverride) return true;

        if (Enum.TryParse(query, true, out T requested) && Enum.IsDefined(requested))
        {
            mode = requested;
            return true;
        }
        return false;
    }
}

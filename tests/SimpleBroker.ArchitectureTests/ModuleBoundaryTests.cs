namespace SimpleBroker.ArchitectureTests;

using System.Reflection;

using NetArchTest.Rules;

using SimpleBroker.Accounts;
using SimpleBroker.Orders;
using SimpleBroker.Trades;

using Xunit;
/// <summary>
/// Module boundaries as executable rules (ADR-0001):
/// a module may use another module only through its *.Contracts assembly.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly string[] Modules = ["Accounts", "Orders", "Trades"];

    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(AccountsModule).Assembly,
        typeof(OrdersModule).Assembly,
        typeof(TradesModule).Assembly,
    ];

    [Theory]
    [InlineData("Accounts")]
    [InlineData("Orders")]
    [InlineData("Trades")]
    public void Module_does_not_reference_other_module_implementations(string module)
    {
        var assembly = ModuleAssemblies.Single(a => a.GetName().Name == $"SimpleBroker.{module}");
        var forbidden = Modules.Where(m => m != module).Select(m => $"SimpleBroker.{m}").ToHashSet();

        var offending = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(forbidden.Contains)
            .ToList();

        Assert.True(offending.Count == 0, $"{module} references: {string.Join(", ", offending)}");
    }

    [Theory]
    [InlineData("Accounts", "Orders", "Trades")]
    [InlineData("Orders", "Accounts", "Trades")]
    [InlineData("Trades", "Accounts", "Orders")]
    public void Module_types_do_not_depend_on_foreign_Internal_namespaces(string module, string other1, string other2)
    {
        var assembly = ModuleAssemblies.Single(a => a.GetName().Name == $"SimpleBroker.{module}");

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny($"SimpleBroker.{other1}.Internal", $"SimpleBroker.{other2}.Internal")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Platform_does_not_reference_modules()
    {
        var referenced = typeof(SimpleBroker.Platform.Outbox.OutboxMessage).Assembly
            .GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referenced, n => Modules.Any(m => n == $"SimpleBroker.{m}" || n == $"SimpleBroker.{m}.Contracts"));
    }

    [Theory]
    [InlineData("Accounts")]
    [InlineData("Orders")]
    [InlineData("Trades")]
    public void Contracts_do_not_reference_implementations_or_EF(string module)
    {
        var contracts = Assembly.Load($"SimpleBroker.{module}.Contracts");
        var referenced = contracts.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referenced, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, n => Modules.Any(m => n == $"SimpleBroker.{m}"));
    }
}
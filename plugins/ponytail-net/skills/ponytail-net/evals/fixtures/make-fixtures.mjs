#!/usr/bin/env node
// Writes the four fixture solutions the ponytail-net evals run against, plus the hidden test
// projects the grader drops in afterwards. Usage: node make-fixtures.mjs <output-root>
//
// Each fixture is a tiny .NET 10 solution with an xunit test project. The "customers" fixture
// is written twice: the base tree and a `.pr` overlay that setup.sh commits on top of it, so the
// review eval has a real "last commit" to look at.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

const root = process.argv[2];
if (!root) {
  console.error('usage: node make-fixtures.mjs <output-root>');
  process.exit(2);
}

const raw = String.raw;

const buildProps = raw`<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
`;

const gitignore = raw`bin/
obj/
`;

const libCsproj = raw`<Project Sdk="Microsoft.NET.Sdk">
</Project>
`;

function testCsproj(projectRef) {
  return raw`<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="${projectRef}" />
  </ItemGroup>
</Project>
`;
}

const fixtures = {};

// ---------------------------------------------------------------------------------------------
// 1. pricing — a rounding bug in a shared helper, reported from one of its two callers.
// ---------------------------------------------------------------------------------------------
fixtures.pricing = {
  'Directory.Build.props': buildProps,
  '.gitignore': gitignore,
  'src/PricingApp/PricingApp.csproj': libCsproj,
  'src/PricingApp/Money.cs': raw`namespace PricingApp;

public static class Money
{
    public static decimal RoundToCents(decimal amount) => Math.Round(amount, 2);
}
`,
  'src/PricingApp/InvoiceLine.cs': raw`namespace PricingApp;

public sealed record InvoiceLine(string Sku, decimal UnitPrice, int Quantity);
`,
  'src/PricingApp/InvoiceService.cs': raw`namespace PricingApp;

public sealed class InvoiceService
{
    private readonly decimal _vatRate;

    public InvoiceService(decimal vatRate)
    {
        if (vatRate < 0m || vatRate > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(vatRate));
        }

        _vatRate = vatRate;
    }

    // Totals are rounded half-up to whole cents, which is what finance prints on the invoice.
    public decimal Total(IReadOnlyCollection<InvoiceLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var net = lines.Sum(line => line.UnitPrice * line.Quantity);
        return Money.RoundToCents(net * (1m + _vatRate));
    }
}
`,
  'src/PricingApp/RefundService.cs': raw`namespace PricingApp;

public sealed class RefundService
{
    // A refund reverses the invoiced amount for the returned quantity and is rounded the same way as the invoice.
    public decimal RefundAmount(InvoiceLine line, int returnedQuantity, decimal vatRate)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (returnedQuantity < 0 || returnedQuantity > line.Quantity)
        {
            throw new ArgumentOutOfRangeException(nameof(returnedQuantity));
        }

        return Money.RoundToCents(line.UnitPrice * returnedQuantity * (1m + vatRate));
    }
}
`,
  'tests/PricingApp.Tests/PricingApp.Tests.csproj': testCsproj('../../src/PricingApp/PricingApp.csproj'),
  'tests/PricingApp.Tests/InvoiceServiceTests.cs': raw`using Xunit;

namespace PricingApp.Tests;

public class InvoiceServiceTests
{
    [Fact]
    public void Total_AppliesVatAndRoundsToCents()
    {
        var service = new InvoiceService(0.2m);

        var total = service.Total([new InvoiceLine("A", 10m, 1), new InvoiceLine("B", 0.333m, 3)]);

        Assert.Equal(13.20m, total);
    }

    [Fact]
    public void Constructor_RejectsNegativeVat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InvoiceService(-0.1m));
    }
}
`,
  'tests/PricingApp.Tests/RefundServiceTests.cs': raw`using Xunit;

namespace PricingApp.Tests;

public class RefundServiceTests
{
    [Fact]
    public void RefundAmount_ReversesReturnedQuantity()
    {
        var refund = new RefundService().RefundAmount(new InvoiceLine("A", 4.99m, 3), 2, 0m);

        Assert.Equal(9.98m, refund);
    }

    [Fact]
    public void RefundAmount_RejectsMoreThanBought()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RefundService().RefundAmount(new InvoiceLine("A", 1m, 1), 2, 0m));
    }
}
`,
};

fixtures['pricing-hidden'] = {
  'PricingApp.HiddenTests/PricingApp.HiddenTests.csproj': testCsproj('../../src/PricingApp/PricingApp.csproj'),
  'PricingApp.HiddenTests/RoundingTests.cs': raw`using Xunit;

namespace PricingApp.HiddenTests;

public class RoundingTests
{
    [Theory]
    [InlineData("2.345", "2.35")]
    [InlineData("2.355", "2.36")]
    [InlineData("2.344", "2.34")]
    [InlineData("0.005", "0.01")]
    public void RoundToCents_RoundsHalfUp(string amount, string expected)
    {
        Assert.Equal(decimal.Parse(expected), Money.RoundToCents(decimal.Parse(amount)));
    }

    [Fact]
    public void InvoiceTotal_ReportedCase_RoundsHalfUp()
    {
        var total = new InvoiceService(0m).Total([new InvoiceLine("A", 2.345m, 1)]);

        Assert.Equal(2.35m, total);
    }

    [Fact]
    public void RefundAmount_RoundsHalfUpLikeTheInvoice()
    {
        var refund = new RefundService().RefundAmount(new InvoiceLine("A", 2.345m, 1), 1, 0m);

        Assert.Equal(2.35m, refund);
    }
}
`,
};

// ---------------------------------------------------------------------------------------------
// 2. rates — "cache rates for 5 minutes" where IMemoryCache is already registered and in use.
// ---------------------------------------------------------------------------------------------
fixtures.rates = {
  'Directory.Build.props': buildProps,
  '.gitignore': gitignore,
  'src/RatesApp/RatesApp.csproj': raw`<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
  </ItemGroup>
</Project>
`,
  'src/RatesApp/IExchangeRateClient.cs': raw`namespace RatesApp;

public interface IExchangeRateClient
{
    Task<decimal> FetchRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken);
}
`,
  'src/RatesApp/HttpExchangeRateClient.cs': raw`using System.Globalization;

namespace RatesApp;

public sealed class HttpExchangeRateClient(HttpClient http) : IExchangeRateClient
{
    public async Task<decimal> FetchRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken)
    {
        var text = await http.GetStringAsync($"https://rates.example.com/{fromCurrency}/{toCurrency}", cancellationToken);
        return decimal.Parse(text, CultureInfo.InvariantCulture);
    }
}
`,
  'src/RatesApp/IExchangeRateService.cs': raw`namespace RatesApp;

public interface IExchangeRateService
{
    Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default);

    Task<decimal> ConvertAsync(decimal amount, string fromCurrency, string toCurrency, CancellationToken cancellationToken = default);
}
`,
  'src/RatesApp/ExchangeRateService.cs': raw`namespace RatesApp;

public sealed class ExchangeRateService(IExchangeRateClient client) : IExchangeRateService
{
    public async Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCurrency);
        ArgumentException.ThrowIfNullOrWhiteSpace(toCurrency);

        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        return await client.FetchRateAsync(fromCurrency.ToUpperInvariant(), toCurrency.ToUpperInvariant(), cancellationToken);
    }

    public async Task<decimal> ConvertAsync(decimal amount, string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        var rate = await GetRateAsync(fromCurrency, toCurrency, cancellationToken);
        return amount * rate;
    }
}
`,
  'src/RatesApp/CountryNameService.cs': raw`using Microsoft.Extensions.Caching.Memory;

namespace RatesApp;

public sealed class CountryNameService(IMemoryCache cache)
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "United States",
        ["DE"] = "Germany",
        ["JP"] = "Japan",
    };

    // In production the names come from a slow directory lookup; the table above stands in for it here.
    public string? GetName(string countryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(countryCode);

        return cache.GetOrCreate($"country-name:{countryCode.ToUpperInvariant()}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return Names.TryGetValue(countryCode, out var name) ? name : null;
        });
    }
}
`,
  'src/RatesApp/ServiceCollectionExtensions.cs': raw`using Microsoft.Extensions.DependencyInjection;

namespace RatesApp;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRates(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IExchangeRateClient, HttpExchangeRateClient>();
        services.AddSingleton<IExchangeRateService, ExchangeRateService>();
        services.AddSingleton<CountryNameService>();
        return services;
    }
}
`,
  'tests/RatesApp.Tests/RatesApp.Tests.csproj': testCsproj('../../src/RatesApp/RatesApp.csproj'),
  'tests/RatesApp.Tests/FakeExchangeRateClient.cs': raw`namespace RatesApp.Tests;

public sealed class FakeExchangeRateClient : IExchangeRateClient
{
    public int Calls { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public Dictionary<(string From, string To), decimal> Rates { get; } = new();

    public Task<decimal> FetchRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken)
    {
        Calls++;
        LastToken = cancellationToken;
        return Task.FromResult(Rates[(fromCurrency, toCurrency)]);
    }
}
`,
  'tests/RatesApp.Tests/ExchangeRateServiceTests.cs': raw`using Xunit;

namespace RatesApp.Tests;

public class ExchangeRateServiceTests
{
    [Fact]
    public async Task GetRate_SameCurrency_ReturnsOneWithoutCallingTheProvider()
    {
        var client = new FakeExchangeRateClient();
        var service = new ExchangeRateService(client);

        var rate = await service.GetRateAsync("EUR", "eur");

        Assert.Equal(1m, rate);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Convert_MultipliesByTheProviderRate()
    {
        var client = new FakeExchangeRateClient();
        client.Rates[("USD", "EUR")] = 0.9m;
        var service = new ExchangeRateService(client);

        var converted = await service.ConvertAsync(100m, "usd", "eur");

        Assert.Equal(90m, converted);
    }

    [Fact]
    public async Task GetRate_PassesTheCancellationTokenToTheProvider()
    {
        var client = new FakeExchangeRateClient();
        client.Rates[("USD", "GBP")] = 0.8m;
        var service = new ExchangeRateService(client);
        using var cts = new CancellationTokenSource();

        await service.GetRateAsync("USD", "GBP", cts.Token);

        Assert.Equal(cts.Token, client.LastToken);
    }
}
`,
};

fixtures['rates-hidden'] = {
  'RatesApp.HiddenTests/RatesApp.HiddenTests.csproj': raw`<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/RatesApp/RatesApp.csproj" />
  </ItemGroup>
</Project>
`,
  'RatesApp.HiddenTests/CountingClient.cs': raw`namespace RatesApp.HiddenTests;

public sealed class CountingClient : IExchangeRateClient
{
    public int Calls { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public Task<decimal> FetchRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken)
    {
        Calls++;
        LastToken = cancellationToken;
        return Task.FromResult(1.5m);
    }
}
`,
  'RatesApp.HiddenTests/CachingTests.cs': raw`using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RatesApp.HiddenTests;

public class CachingTests
{
    private static (IExchangeRateService Service, CountingClient Client, ServiceProvider Provider) Build()
    {
        var client = new CountingClient();
        var services = new ServiceCollection();
        services.AddRates();
        services.AddSingleton<IExchangeRateClient>(client);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IExchangeRateService>(), client, provider);
    }

    [Fact]
    public async Task RepeatedRequestsForOnePair_CallTheProviderOnce()
    {
        var (service, client, provider) = Build();
        await using var _ = provider;

        await service.GetRateAsync("USD", "EUR");
        await service.GetRateAsync("USD", "EUR");
        await service.GetRateAsync("USD", "EUR");

        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task DifferentPairs_AreCachedSeparately()
    {
        var (service, client, provider) = Build();
        await using var _ = provider;

        await service.GetRateAsync("USD", "EUR");
        await service.GetRateAsync("USD", "GBP");
        await service.GetRateAsync("EUR", "USD");

        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task CurrencyCaseDoesNotSplitTheCacheEntry()
    {
        var (service, client, provider) = Build();
        await using var _ = provider;

        await service.GetRateAsync("usd", "eur");
        await service.GetRateAsync("USD", "EUR");

        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task FirstFetch_StillPropagatesTheCancellationToken()
    {
        var (service, client, provider) = Build();
        await using var _ = provider;
        using var cts = new CancellationTokenSource();

        await service.GetRateAsync("USD", "JPY", cts.Token);

        Assert.Equal(cts.Token, client.LastToken);
    }

    [Fact]
    public async Task Convert_UsesTheCachedRate()
    {
        var (service, client, provider) = Build();
        await using var _ = provider;

        await service.GetRateAsync("USD", "CHF");
        var converted = await service.ConvertAsync(10m, "USD", "CHF");

        Assert.Equal(15m, converted);
        Assert.Equal(1, client.Calls);
    }
}
`,
};

// ---------------------------------------------------------------------------------------------
// 3. orders — an "ultra" simplification with a reflection-invoked member and a wire contract.
// ---------------------------------------------------------------------------------------------
fixtures.orders = {
  'Directory.Build.props': buildProps,
  '.gitignore': gitignore,
  'src/OrdersApp/OrdersApp.csproj': libCsproj,
  'src/OrdersApp/Exporting/Order.cs': raw`using System.Text.Json.Serialization;

namespace OrdersApp.Exporting;

public sealed class Order
{
    public int Id { get; set; }

    public string CustomerName { get; set; } = "";

    [JsonPropertyName("legacy_id")]
    public string? LegacyId { get; set; }

    public List<OrderLine> Lines { get; set; } = new();
}

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);
`,
  'src/OrdersApp/Exporting/IExportFormatter.cs': raw`namespace OrdersApp.Exporting;

public interface IExportFormatter
{
    string Format(Order order);
}
`,
  'src/OrdersApp/Exporting/CsvFormatter.cs': raw`using System.Globalization;
using System.Text;

namespace OrdersApp.Exporting;

public sealed class CsvFormatter : IExportFormatter
{
    public string Format(Order order)
    {
        var builder = new StringBuilder();
        builder.AppendLine("sku,quantity,unit_price");
        foreach (var line in order.Lines)
        {
            builder.Append(line.Sku).Append(',')
                .Append(line.Quantity).Append(',')
                .Append(line.UnitPrice.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        return builder.ToString();
    }
}
`,
  'src/OrdersApp/Exporting/ExportFormatterFactory.cs': raw`namespace OrdersApp.Exporting;

public static class ExportFormatterFactory
{
    public static IExportFormatter Create(string format) => format switch
    {
        "csv" => new CsvFormatter(),
        _ => throw new NotSupportedException($"Export format {format} is not supported."),
    };
}
`,
  'src/OrdersApp/Exporting/ExportOptions.cs': raw`namespace OrdersApp.Exporting;

public sealed class ExportOptions
{
    public string Format { get; set; } = "csv";

    public bool IncludeHeader { get; set; } = true;

    public string DateFormat { get; set; } = "yyyyMMdd";
}
`,
  'src/OrdersApp/Exporting/OrderExporter.cs': raw`using System.Globalization;
using System.Text.Json;

namespace OrdersApp.Exporting;

public sealed class OrderExporter
{
    private readonly IExportFormatter _formatter = ExportFormatterFactory.Create("csv");

    public string Export(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Lines.Count == 0)
        {
            throw new InvalidOperationException("An order must have at least one line.");
        }

        return _formatter.Format(order);
    }

    public string ExportLegacy(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return JsonSerializer.Serialize(order);
    }

    private static string FormatLegacyDate(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
`,
  'src/OrdersApp/Exporting/ExportCommands.cs': raw`namespace OrdersApp.Exporting;

public static class ExportCommands
{
    // Command names as the operations console sends them, mapped to exporter methods.
    public static readonly IReadOnlyDictionary<string, string> Default = new Dictionary<string, string>
    {
        ["default"] = "Export",
        ["legacy"] = "ExportLegacy",
    };
}
`,
  'src/OrdersApp/Exporting/ExportCommandDispatcher.cs': raw`namespace OrdersApp.Exporting;

public sealed class ExportCommandDispatcher(OrderExporter exporter, IReadOnlyDictionary<string, string> commands)
{
    public string Dispatch(string command, Order order)
    {
        if (!commands.TryGetValue(command, out var methodName))
        {
            throw new ArgumentException($"Unknown export command {command}.", nameof(command));
        }

        var method = typeof(OrderExporter).GetMethod(methodName, [typeof(Order)])
            ?? throw new InvalidOperationException($"Exporter has no method {methodName}.");

        return (string)method.Invoke(exporter, [order])!;
    }
}
`,
  'tests/OrdersApp.Tests/OrdersApp.Tests.csproj': testCsproj('../../src/OrdersApp/OrdersApp.csproj'),
  'tests/OrdersApp.Tests/OrderExporterTests.cs': raw`using OrdersApp.Exporting;
using Xunit;

namespace OrdersApp.Tests;

public class OrderExporterTests
{
    private static Order SampleOrder() => new()
    {
        Id = 7,
        CustomerName = "Ada",
        Lines = { new OrderLine("A-1", 2, 9.5m), new OrderLine("B-2", 1, 100m) },
    };

    [Fact]
    public void Export_WritesHeaderAndOneRowPerLine()
    {
        var csv = new OrderExporter().Export(SampleOrder());

        var rows = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("sku,quantity,unit_price", rows[0]);
        Assert.Equal("A-1,2,9.5", rows[1]);
        Assert.Equal("B-2,1,100", rows[2]);
    }

    [Fact]
    public void Export_RejectsAnOrderWithoutLines()
    {
        Assert.Throws<InvalidOperationException>(() => new OrderExporter().Export(new Order { Id = 1 }));
    }

    [Fact]
    public void Dispatcher_DefaultCommand_ExportsCsv()
    {
        var dispatcher = new ExportCommandDispatcher(new OrderExporter(), ExportCommands.Default);

        var csv = dispatcher.Dispatch("default", SampleOrder());

        Assert.StartsWith("sku,quantity,unit_price", csv);
    }
}
`,
};

fixtures['orders-hidden'] = {
  'OrdersApp.HiddenTests/OrdersApp.HiddenTests.csproj': testCsproj('../../src/OrdersApp/OrdersApp.csproj'),
  'OrdersApp.HiddenTests/ContractTests.cs': raw`using System.Text.Json;
using OrdersApp.Exporting;
using Xunit;

namespace OrdersApp.HiddenTests;

public class ContractTests
{
    private static Order SampleOrder() => new()
    {
        Id = 7,
        CustomerName = "Ada",
        Lines = { new OrderLine("A-1", 2, 9.5m) },
    };

    [Fact]
    public void ExportLegacy_IsStillReachableByName_ForTheOperationsConsole()
    {
        var method = typeof(OrderExporter).GetMethod("ExportLegacy", [typeof(Order)]);

        Assert.NotNull(method);
        var json = (string)method!.Invoke(new OrderExporter(), [SampleOrder()])!;
        Assert.Contains("\"legacy_id\"", json);
    }

    [Fact]
    public void Export_StillRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new OrderExporter().Export(null!));
    }

    [Fact]
    public void Export_StillRejectsAnOrderWithoutLines()
    {
        Assert.Throws<InvalidOperationException>(() => new OrderExporter().Export(new Order { Id = 1 }));
    }

    [Fact]
    public void LegacyId_StillSerializesAsSnakeCase()
    {
        var order = JsonSerializer.Deserialize<Order>("""{"legacy_id":"L-1","Id":3}""")!;

        var json = JsonSerializer.Serialize(order);

        Assert.Contains("\"legacy_id\":\"L-1\"", json);
    }

    [Fact]
    public void Export_StillWritesCsv()
    {
        var csv = new OrderExporter().Export(SampleOrder());

        Assert.StartsWith("sku,quantity,unit_price", csv);
        Assert.Contains("A-1,2,9.5", csv);
    }
}
`,
};

// ---------------------------------------------------------------------------------------------
// 4. customers — a base commit plus a PR overlay for the review eval.
// ---------------------------------------------------------------------------------------------
fixtures.customers = {
  'Directory.Build.props': buildProps,
  '.gitignore': gitignore,
  'src/CustomersApp/CustomersApp.csproj': libCsproj,
  'src/CustomersApp/Customer.cs': raw`namespace CustomersApp;

public sealed record Customer(int Id, string Name, string City, string? Email);
`,
  'src/CustomersApp/User.cs': raw`namespace CustomersApp;

public sealed record User(string Login, bool IsAdmin);
`,
  'src/CustomersApp/ICustomerRepository.cs': raw`namespace CustomersApp;

public interface ICustomerRepository
{
    IReadOnlyList<Customer> All();
}
`,
  'src/CustomersApp/InMemoryCustomerRepository.cs': raw`namespace CustomersApp;

public sealed class InMemoryCustomerRepository(IEnumerable<Customer> customers) : ICustomerRepository
{
    private readonly List<Customer> _customers = customers.ToList();

    public IReadOnlyList<Customer> All() => _customers;
}
`,
  'src/CustomersApp/CustomerService.cs': raw`namespace CustomersApp;

public sealed class CustomerService(ICustomerRepository repository)
{
    // The UI renders this list as-is, so it is sorted by name here.
    public IReadOnlyList<Customer> FindByCity(string city) =>
        repository.All().Where(c => c.City == city).OrderBy(c => c.Name).ToList();

    public IReadOnlyList<Customer> ExportAll(User caller)
    {
        if (!caller.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only administrators may export customers.");
        }

        return repository.All();
    }
}
`,
  'src/CustomersApp/ReportService.cs': raw`namespace CustomersApp;

public sealed class ReportService(CustomerService customers)
{
    public async Task WriteCityReportAsync(string city, string path, CancellationToken cancellationToken = default)
    {
        var lines = customers.FindByCity(city).Select(c => $"{c.Id},{c.Name}");
        using var writer = new StreamWriter(path);
        await writer.WriteAsync(string.Join(Environment.NewLine, lines).AsMemory(), cancellationToken);
    }
}
`,
  'tests/CustomersApp.Tests/CustomersApp.Tests.csproj': testCsproj('../../src/CustomersApp/CustomersApp.csproj'),
  'tests/CustomersApp.Tests/CustomerServiceTests.cs': raw`using Xunit;

namespace CustomersApp.Tests;

public class CustomerServiceTests
{
    private static CustomerService CreateService() => new(new InMemoryCustomerRepository(
    [
        new Customer(1, "Zoe", "Oslo", "zoe@example.com"),
        new Customer(2, "Adam", "Oslo", null),
        new Customer(3, "Mia", "Bergen", "mia@example.com"),
    ]));

    [Fact]
    public void FindByCity_ReturnsOnlyCustomersInThatCity()
    {
        var found = CreateService().FindByCity("Oslo");

        Assert.Equal(2, found.Count);
        Assert.All(found, c => Assert.Equal("Oslo", c.City));
    }

    [Fact]
    public void ExportAll_RejectsNonAdministrators()
    {
        Assert.Throws<UnauthorizedAccessException>(() => CreateService().ExportAll(new User("guest", IsAdmin: false)));
    }

    [Fact]
    public void ExportAll_ReturnsEveryCustomerForAdministrators()
    {
        Assert.Equal(3, CreateService().ExportAll(new User("root", IsAdmin: true)).Count);
    }
}
`,
  'tests/CustomersApp.Tests/ReportServiceTests.cs': raw`using Xunit;

namespace CustomersApp.Tests;

public class ReportServiceTests
{
    [Fact]
    public async Task WriteCityReport_WritesOneLinePerCustomer()
    {
        var service = new ReportService(new CustomerService(new InMemoryCustomerRepository(
        [
            new Customer(1, "Zoe", "Oslo", "zoe@example.com"),
            new Customer(2, "Adam", "Oslo", null),
        ])));
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            await service.WriteCityReportAsync("Oslo", path);

            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
`,
};

fixtures['customers.pr'] = {
  'src/CustomersApp/ICustomerLookupService.cs': raw`namespace CustomersApp;

public interface ICustomerLookupService
{
    Customer? FindByEmail(string email);
}
`,
  'src/CustomersApp/CustomerLookupOptions.cs': raw`namespace CustomersApp;

public sealed class CustomerLookupOptions
{
    public bool CaseInsensitive { get; set; } = true;
}
`,
  'src/CustomersApp/CustomerLookupService.cs': raw`namespace CustomersApp;

public sealed class CustomerLookupService(ICustomerRepository repository, CustomerLookupOptions options) : ICustomerLookupService
{
    public Customer? FindByEmail(string email)
    {
        var comparison = options.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
#pragma warning disable CS8602 // Email is validated at import time
        return repository.All().FirstOrDefault(c => c.Email.Equals(email, comparison));
#pragma warning restore CS8602
    }
}
`,
  'src/CustomersApp/CustomerLookupServiceFactory.cs': raw`namespace CustomersApp;

public static class CustomerLookupServiceFactory
{
    public static ICustomerLookupService Create(ICustomerRepository repository) =>
        new CustomerLookupService(repository, new CustomerLookupOptions());
}
`,
  'src/CustomersApp/CustomerService.cs': raw`namespace CustomersApp;

public sealed class CustomerService(ICustomerRepository repository)
{
    public IReadOnlyList<Customer> FindByCity(string city) => [.. repository.All().Where(c => c.City == city)];

    public IReadOnlyList<Customer> ExportAll(User caller)
    {
        if (!caller.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only administrators may export customers.");
        }

        return repository.All();
    }
}
`,
  'src/CustomersApp/ReportService.cs': raw`namespace CustomersApp;

public sealed class ReportService(CustomerService customers)
{
    public Task WriteCityReportAsync(string city, string path, CancellationToken cancellationToken = default)
    {
        var lines = customers.FindByCity(city).Select(c => $"{c.Id},{c.Name}");
        using var writer = new StreamWriter(path);
        return writer.WriteAsync(string.Join(Environment.NewLine, lines).AsMemory(), cancellationToken);
    }
}
`,
  'tests/CustomersApp.Tests/CustomerLookupServiceTests.cs': raw`using Xunit;

namespace CustomersApp.Tests;

public class CustomerLookupServiceTests
{
    [Fact]
    public void FindByEmail_IgnoresCase()
    {
        var lookup = CustomerLookupServiceFactory.Create(new InMemoryCustomerRepository(
        [
            new Customer(1, "Ann", "Oslo", "ann@example.com"),
        ]));

        Assert.NotNull(lookup.FindByEmail("ANN@example.com"));
    }
}
`,
};

for (const [name, files] of Object.entries(fixtures)) {
  for (const [relative, content] of Object.entries(files)) {
    const path = join(root, name, relative);
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, content);
  }
  console.log(`${name}: ${Object.keys(files).length} files`);
}

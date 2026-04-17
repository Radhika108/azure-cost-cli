using AzureCostCli.CostApi;
using AzureCostCli.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Register HTTP clients matching the CLI setup
        services.AddHttpClient("CostApi", client =>
        {
            client.BaseAddress = new Uri("https://management.azure.com/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddPolicyHandler(PollyExtensions.GetRetryAfterPolicy());

        services.AddHttpClient("PriceApi", client =>
        {
            client.BaseAddress = new Uri("https://prices.azure.com/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddPolicyHandler(PollyExtensions.GetRetryAfterPolicy());

        services.AddHttpClient("RegionsApi", client =>
        {
            client.BaseAddress = new Uri("https://datacenters.microsoft.com/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "azure-cost-cli");
        }).AddPolicyHandler(PollyPolicyExtensions.GetRetryAfterPolicy());

        services.AddTransient<ICostRetriever, AzureCostApiRetriever>();
        services.AddTransient<IPriceRetriever, AzurePriceRetriever>();
        services.AddTransient<IRegionsRetriever, AzureRegionsRetriever>();
    })
    .Build();

await host.RunAsync();

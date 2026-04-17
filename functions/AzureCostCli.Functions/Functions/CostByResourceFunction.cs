using System.Net;
using System.Text.Json;
using AzureCostCli.Commands;
using AzureCostCli.CostApi;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureCostCli.Functions.Functions;

/// <summary>
/// HTTP-triggered Azure Function that returns cost breakdown per Azure resource.
/// </summary>
/// <remarks>
/// Query parameters:
///   subscriptionId      (required for subscription scope) - Azure subscription GUID
///   resourceGroup       (optional)
///   billingAccountId    (optional)
///   timeframe           (optional) - BillingMonthToDate (default), MonthToDate, WeekToDate, Custom
///   from                (optional) - yyyy-MM-dd
///   to                  (optional) - yyyy-MM-dd
///   metric              (optional) - ActualCost (default) or AmortizedCost
///   excludeMeterDetails (optional) - true/false (default false)
///   top                 (optional) - show only top N resources (0 = all)
///   sort                (optional) - cost (default), cost-asc, name, resource-group, resource-type, location
///   useUSD              (optional) - true/false
/// </remarks>
public class CostByResourceFunction
{
    private readonly ICostRetriever _costRetriever;
    private readonly ILogger<CostByResourceFunction> _logger;

    public CostByResourceFunction(ICostRetriever costRetriever, ILogger<CostByResourceFunction> logger)
    {
        _costRetriever = costRetriever;
        _logger = logger;
    }

    [Function("CostByResource")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "costByResource")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("CostByResource function triggered.");

        if (!AccumulatedCostFunction.TryResolveScope(req, out var scope, out var errorMessage))
        {
            return await AccumulatedCostFunction.BadRequest(req, errorMessage!);
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var timeframe = AccumulatedCostFunction.ParseEnum(req, "timeframe", TimeframeType.BillingMonthToDate);
        var from = AccumulatedCostFunction.ParseDate(req, "from");
        var to = AccumulatedCostFunction.ParseDate(req, "to");
        var metric = AccumulatedCostFunction.ParseEnum(req, "metric", MetricType.ActualCost);
        var useUsd = AccumulatedCostFunction.ParseBool(req, "useUSD");
        var excludeMeterDetails = AccumulatedCostFunction.ParseBool(req, "excludeMeterDetails");
        var sort = query["sort"] ?? "cost";

        int.TryParse(query["top"], out var top);

        if (from.HasValue && to.HasValue && timeframe != TimeframeType.Custom)
            timeframe = TimeframeType.Custom;

        var fromDate = from ?? DateOnly.FromDateTime(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1));
        var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            var resources = await _costRetriever.RetrieveCostForResources(
                false, scope!, Array.Empty<string>(), metric, excludeMeterDetails, timeframe, fromDate, toDate);

            IEnumerable<CostResourceItem> sorted = sort.ToLowerInvariant() switch
            {
                "cost-asc" => resources.OrderBy(r => useUsd ? r.CostUSD : r.Cost),
                "name" => resources.OrderBy(r => r.ResourceId),
                "resource-group" => resources.OrderBy(r => r.ResourceGroupName),
                "resource-type" => resources.OrderBy(r => r.ResourceType),
                "location" => resources.OrderBy(r => r.ResourceLocation),
                _ => resources.OrderByDescending(r => useUsd ? r.CostUSD : r.Cost)
            };

            var allResources = sorted.ToList();

            if (top > 0)
            {
                var topIds = new HashSet<string>(
                    allResources
                        .GroupBy(r => r.ResourceId ?? string.Empty)
                        .OrderByDescending(g => g.Sum(r => useUsd ? r.CostUSD : r.Cost))
                        .Take(top)
                        .Select(g => g.Key));

                allResources = allResources.Where(r => topIds.Contains(r.ResourceId ?? string.Empty)).ToList();
            }

            return await AccumulatedCostFunction.Ok(req, allResources);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cost by resource.");
            return await AccumulatedCostFunction.InternalServerError(req, ex.Message);
        }
    }
}

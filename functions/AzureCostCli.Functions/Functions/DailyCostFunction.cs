using System.Net;
using System.Text.Json;
using AzureCostCli.Commands;
using AzureCostCli.CostApi;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureCostCli.Functions.Functions;

/// <summary>
/// HTTP-triggered Azure Function that returns daily cost data grouped by a dimension.
/// </summary>
/// <remarks>
/// Query parameters:
///   subscriptionId  (required for subscription scope) - Azure subscription GUID
///   resourceGroup   (optional)
///   billingAccountId (optional)
///   timeframe       (optional) - BillingMonthToDate (default), MonthToDate, WeekToDate, Custom
///   from            (optional) - yyyy-MM-dd
///   to              (optional) - yyyy-MM-dd
///   metric          (optional) - ActualCost (default) or AmortizedCost
///   dimension       (optional) - grouping dimension, defaults to ResourceGroupName
///   useUSD          (optional) - true/false
/// </remarks>
public class DailyCostFunction
{
    private readonly ICostRetriever _costRetriever;
    private readonly ILogger<DailyCostFunction> _logger;

    public DailyCostFunction(ICostRetriever costRetriever, ILogger<DailyCostFunction> logger)
    {
        _costRetriever = costRetriever;
        _logger = logger;
    }

    [Function("DailyCosts")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "dailyCosts")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("DailyCosts function triggered.");

        if (!AccumulatedCostFunction.TryResolveScope(req, out var scope, out var errorMessage))
        {
            return await AccumulatedCostFunction.BadRequest(req, errorMessage!);
        }

        var timeframe = AccumulatedCostFunction.ParseEnum(req, "timeframe", TimeframeType.BillingMonthToDate);
        var from = AccumulatedCostFunction.ParseDate(req, "from");
        var to = AccumulatedCostFunction.ParseDate(req, "to");
        var metric = AccumulatedCostFunction.ParseEnum(req, "metric", MetricType.ActualCost);
        var useUsd = AccumulatedCostFunction.ParseBool(req, "useUSD");
        var dimension = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["dimension"] ?? "ResourceGroupName";

        if (from.HasValue && to.HasValue && timeframe != TimeframeType.Custom)
            timeframe = TimeframeType.Custom;

        var fromDate = from ?? DateOnly.FromDateTime(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1));
        var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            var dailyCosts = await _costRetriever.RetrieveDailyCost(
                false, scope!, Array.Empty<string>(), metric, dimension, timeframe, fromDate, toDate, false);

            var output = dailyCosts
                .GroupBy(a => a.Date)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    Date = g.Key,
                    Items = g.OrderByDescending(b => useUsd ? b.CostUsd : b.Cost)
                             .Select(b => new { b.Name, b.Cost, b.Currency, b.CostUsd })
                });

            return await AccumulatedCostFunction.Ok(req, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving daily costs.");
            return await AccumulatedCostFunction.InternalServerError(req, ex.Message);
        }
    }
}

using System.Net;
using System.Text.Json;
using AzureCostCli.Commands;
using AzureCostCli.OutputFormatters;
using AzureCostCli.CostApi;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureCostCli.Functions.Functions;

/// <summary>
/// HTTP-triggered Azure Function that returns accumulated cost details for a subscription or billing scope.
/// </summary>
/// <remarks>
/// Query parameters:
///   subscriptionId  (required for subscription scope) - Azure subscription GUID
///   resourceGroup   (optional) - limit scope to a resource group
///   billingAccountId (optional) - billing account scope
///   timeframe       (optional) - BillingMonthToDate (default), MonthToDate, WeekToDate, Custom
///   from            (optional) - start date yyyy-MM-dd, used when timeframe=Custom
///   to              (optional) - end date yyyy-MM-dd, used when timeframe=Custom
///   metric          (optional) - ActualCost (default) or AmortizedCost
///   useUSD          (optional) - true/false (default false)
/// </remarks>
public class AccumulatedCostFunction
{
    private readonly ICostRetriever _costRetriever;
    private readonly ILogger<AccumulatedCostFunction> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new DateOnlyJsonConverter() }
    };

    public AccumulatedCostFunction(ICostRetriever costRetriever, ILogger<AccumulatedCostFunction> logger)
    {
        _costRetriever = costRetriever;
        _logger = logger;
    }

    [Function("AccumulatedCost")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "accumulatedCost")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("AccumulatedCost function triggered.");

        if (!TryResolveScope(req, out var scope, out var errorMessage))
        {
            return await BadRequest(req, errorMessage!);
        }

        var timeframe = ParseEnum(req, "timeframe", TimeframeType.BillingMonthToDate);
        var from = ParseDate(req, "from");
        var to = ParseDate(req, "to");
        var metric = ParseEnum(req, "metric", MetricType.ActualCost);
        var useUsd = ParseBool(req, "useUSD");

        if (from.HasValue && to.HasValue && timeframe != TimeframeType.Custom)
            timeframe = TimeframeType.Custom;

        var fromDate = from ?? DateOnly.FromDateTime(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1));
        var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            var costs = await _costRetriever.RetrieveCosts(false, scope!, Array.Empty<string>(), metric, timeframe, fromDate, toDate);

            var forecastedCosts = new List<CostItem>();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            DateOnly forecastStartDate = timeframe switch
            {
                TimeframeType.BillingMonthToDate or TimeframeType.MonthToDate
                    => DateOnly.FromDateTime(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)),
                TimeframeType.TheLastBillingMonth or TimeframeType.TheLastMonth
                    => DateOnly.FromDateTime(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1)),
                TimeframeType.WeekToDate
                    => DateOnly.FromDateTime(DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek)),
                _ => toDate >= today ? today : default
            };

            if (forecastStartDate != default)
            {
                var forecastEndDate = new DateOnly(toDate.Year, toDate.Month, DateTime.DaysInMonth(toDate.Year, toDate.Month));
                forecastedCosts = (await _costRetriever.RetrieveForecastedCosts(false, scope!, Array.Empty<string>(), metric, TimeframeType.Custom, forecastStartDate, forecastEndDate)).ToList();
            }

            var byServiceName = await _costRetriever.RetrieveCostByServiceName(false, scope!, Array.Empty<string>(), metric, timeframe, fromDate, toDate);
            var byLocation = await _costRetriever.RetrieveCostByLocation(false, scope!, Array.Empty<string>(), metric, timeframe, fromDate, toDate);
            var byResourceGroup = await _costRetriever.RetrieveCostByResourceGroup(false, scope!, Array.Empty<string>(), metric, timeframe, fromDate, toDate);

            var output = new
            {
                totals = new
                {
                    todaysCost = costs.Where(a => a.Date == today).Sum(a => useUsd ? a.CostUsd : a.Cost),
                    yesterdayCost = costs.Where(a => a.Date == today.AddDays(-1)).Sum(a => useUsd ? a.CostUsd : a.Cost),
                    lastSevenDaysCost = costs.Where(a => a.Date >= today.AddDays(-7)).Sum(a => useUsd ? a.CostUsd : a.Cost),
                    lastThirtyDaysCost = costs.Where(a => a.Date >= today.AddDays(-30)).Sum(a => useUsd ? a.CostUsd : a.Cost),
                    totalCostInTimeframe = costs.Sum(a => useUsd ? a.CostUsd : a.Cost)
                },
                cost = costs.OrderBy(a => a.Date).Select(a => new { a.Date, a.Cost, a.Currency, a.CostUsd }),
                forecastedCosts = forecastedCosts.OrderByDescending(a => a.Date).Select(a => new { a.Date, a.Cost, a.Currency, a.CostUsd }),
                byServiceNames = byServiceName.OrderByDescending(a => a.Cost).Select(a => new { ServiceName = a.ItemName, a.Cost, a.Currency, a.CostUsd }),
                byLocation = byLocation.OrderByDescending(a => a.Cost).Select(a => new { Location = a.ItemName, a.Cost, a.Currency, a.CostUsd }),
                byResourceGroup = byResourceGroup.OrderByDescending(a => a.Cost).Select(a => new { ResourceGroup = a.ItemName, a.Cost, a.Currency, a.CostUsd })
            };

            return await Ok(req, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving accumulated cost.");
            return await InternalServerError(req, ex.Message);
        }
    }

    internal static bool TryResolveScope(HttpRequestData req, out Scope? scope, out string? error)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var billingAccountId = query["billingAccountId"];
        var enrollmentAccountId = query["enrollmentAccountId"];
        var resourceGroup = query["resourceGroup"];

        if (!string.IsNullOrWhiteSpace(billingAccountId) && !string.IsNullOrWhiteSpace(enrollmentAccountId))
        {
            scope = Scope.EnrollmentAccount(billingAccountId, enrollmentAccountId);
            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(billingAccountId))
        {
            scope = Scope.BillingAccount(billingAccountId);
            error = null;
            return true;
        }

        var subscriptionIdStr = query["subscriptionId"];
        if (string.IsNullOrWhiteSpace(subscriptionIdStr) || !Guid.TryParse(subscriptionIdStr, out var subscriptionId))
        {
            scope = null;
            error = "A valid 'subscriptionId' query parameter is required (or provide 'billingAccountId').";
            return false;
        }

        scope = string.IsNullOrWhiteSpace(resourceGroup)
            ? Scope.Subscription(subscriptionId)
            : Scope.ResourceGroup(subscriptionId, resourceGroup);

        error = null;
        return true;
    }

    internal static TEnum ParseEnum<TEnum>(HttpRequestData req, string key, TEnum defaultValue) where TEnum : struct, Enum
    {
        var value = System.Web.HttpUtility.ParseQueryString(req.Url.Query)[key];
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse<TEnum>(value, true, out var result)
            ? result
            : defaultValue;
    }

    internal static DateOnly? ParseDate(HttpRequestData req, string key)
    {
        var value = System.Web.HttpUtility.ParseQueryString(req.Url.Query)[key];
        return !string.IsNullOrWhiteSpace(value) && DateOnly.TryParse(value, out var date) ? date : null;
    }

    internal static bool ParseBool(HttpRequestData req, string key, bool defaultValue = false)
    {
        var value = System.Web.HttpUtility.ParseQueryString(req.Url.Query)[key];
        return !string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out var result) ? result : defaultValue;
    }

    internal static async Task<HttpResponseData> Ok(HttpRequestData req, object body)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    internal static async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        return response;
    }

    internal static async Task<HttpResponseData> InternalServerError(HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.InternalServerError);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        return response;
    }
}

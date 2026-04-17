using System.Net;
using System.Text.Json;
using AzureCostCli.CostApi;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureCostCli.Functions.Functions;

/// <summary>
/// HTTP-triggered Azure Function that returns Azure budget information for a subscription or billing scope.
/// </summary>
/// <remarks>
/// Query parameters:
///   subscriptionId   (required for subscription scope) - Azure subscription GUID
///   resourceGroup    (optional)
///   billingAccountId (optional)
/// </remarks>
public class BudgetsFunction
{
    private readonly ICostRetriever _costRetriever;
    private readonly ILogger<BudgetsFunction> _logger;

    public BudgetsFunction(ICostRetriever costRetriever, ILogger<BudgetsFunction> logger)
    {
        _costRetriever = costRetriever;
        _logger = logger;
    }

    [Function("Budgets")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "budgets")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Budgets function triggered.");

        if (!AccumulatedCostFunction.TryResolveScope(req, out var scope, out var errorMessage))
        {
            return await AccumulatedCostFunction.BadRequest(req, errorMessage!);
        }

        try
        {
            var budgets = await _costRetriever.RetrieveBudgets(false, scope!);
            return await AccumulatedCostFunction.Ok(req, budgets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving budgets.");
            return await AccumulatedCostFunction.InternalServerError(req, ex.Message);
        }
    }
}

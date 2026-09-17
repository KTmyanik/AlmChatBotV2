using AlmChatBot.Api.Models;
using AlmChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlmChatBot.Api.Controllers;

[ApiController]
[Route("api/alm/report")]
public sealed class AlmReportController(
    IAlmCashflowReportService report,
    ILogger<AlmReportController> logger) : ControllerBase
{
    [HttpGet("filters")]
    [ProducesResponseType(typeof(AlmReportFiltersDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlmReportFiltersDto>> Filters(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await report.FiltersAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ALM rapor filtreleri okunamadı.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [HttpGet("cashflow")]
    [ProducesResponseType(typeof(AlmCashflowReportDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlmCashflowReportDto>> Cashflow(
        [FromQuery] string? date,
        [FromQuery] string? approach,
        [FromQuery] string? ccy,
        [FromQuery] string? pools,
        [FromQuery] string? balanceType,
        [FromQuery] bool coreDeposits = false,
        [FromQuery] int depth = 2,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new AlmCashflowRequest
            {
                Date = date,
                Approach = string.IsNullOrWhiteSpace(approach) ? "Liquidity" : approach,
                Ccy = ccy,
                Pools = string.IsNullOrWhiteSpace(pools)
                    ? []
                    : pools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                BalanceType = string.IsNullOrWhiteSpace(balanceType) ? "TOTAL" : balanceType,
                CoreDeposits = coreDeposits,
                Depth = depth
            };
            return Ok(await report.CashflowAsync(request, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ALM nakit akış raporu üretilemedi.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [HttpGet("duration")]
    [ProducesResponseType(typeof(AlmDurationReportDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlmDurationReportDto>> Duration(
        [FromQuery] string? date,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await report.DurationAsync(date, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ALM durasyon raporu üretilemedi.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}

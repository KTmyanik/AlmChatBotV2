using AlmChatBot.Api.Configuration;
using AlmChatBot.Api.Exceptions;
using AlmChatBot.Api.Models;
using AlmChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AlmChatBot.Api.Controllers;

[ApiController]
[Route("api/alm")]
public sealed class AlmQueryController(
    IAlmOrchestratorService orchestrator,
    IOptions<QwenConfig> qwenOptions,
    ILogger<AlmQueryController> logger) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status()
    {
        var config = qwenOptions.Value;
        return Ok(new
        {
            modelName = config.ModelName,
            baseUrl = config.BaseUrl,
            hasApiKey = !string.IsNullOrWhiteSpace(config.ApiKey),
            sqlGeneration = "rules-first"
        });
    }

    [HttpPost("ask")]
    [ProducesResponseType(typeof(QueryExecutionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<QueryExecutionResultDto>> Ask(
        [FromBody] QueryRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "Question alanı zorunludur." });
        }

        try
        {
            var result = await orchestrator.AskAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (SqlGuardrailException ex)
        {
            return BadRequest(new { error = ex.Message, sql = ex.Sql });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ALM soru işlenemedi.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [HttpGet("bulletin")]
    [ProducesResponseType(typeof(AlmBulletinDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlmBulletinDto>> Bulletin(CancellationToken cancellationToken)
    {
        try
        {
            var bulletin = await orchestrator.BulletinAsync(cancellationToken);
            return Ok(bulletin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ALM bülten üretilemedi.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [HttpPost("insight")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> Insight(
        [FromBody] InsightRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || string.IsNullOrWhiteSpace(request.Sql))
        {
            return BadRequest(new { error = "Question ve Sql alanları zorunludur." });
        }

        try
        {
            var summary = await orchestrator.NarrateAsync(request, cancellationToken);
            return Ok(new { insightSummary = summary });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ALM özet üretilemedi.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}

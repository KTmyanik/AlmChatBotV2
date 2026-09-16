using System.Diagnostics;
using AlmChatBot.Api.Exceptions;
using AlmChatBot.Api.Models;

namespace AlmChatBot.Api.Services;

public interface IAlmOrchestratorService
{
    Task<QueryExecutionResultDto> AskAsync(QueryRequestDto request, CancellationToken cancellationToken);

    Task<string> NarrateAsync(InsightRequestDto request, CancellationToken cancellationToken);

    Task<AlmBulletinDto> BulletinAsync(CancellationToken cancellationToken);
}

public sealed class AlmOrchestratorService(
    IAlmSqlPlanner planner,
    IAlmAnalysisService analysis,
    IAlmBulletinService bulletin,
    IQwenClientService qwen,
    ISqlGuardrailService guardrail,
    IAlmDbService db,
    ILogger<AlmOrchestratorService> logger) : IAlmOrchestratorService
{
    public async Task<QueryExecutionResultDto> AskAsync(QueryRequestDto request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("SQL üretimi başladı: {Question}", request.Question);

        var interpreted = planner.Interpret(request.Question);
        if (interpreted.NeedsConfirmation && !request.Confirmed)
        {
            stopwatch.Stop();
            logger.LogInformation("Yazım düzeltmesi onayı bekleniyor: {Suggested}", interpreted.SuggestedQuestion);
            return new QueryExecutionResultDto
            {
                NeedsConfirmation = true,
                SuggestedQuestion = interpreted.SuggestedQuestion,
                InterpretationSummary = interpreted.InterpretationSummary,
                Corrections = interpreted.Corrections,
                Explanation = "Bunu mu demek istediniz? Onaylarsanız sorgu çalıştırılacak.",
                SqlSource = "rules",
                ExecutionDurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        var planned = interpreted.Sql;
        var source = planned is null ? "llm" : "rules";
        var llm = planned ?? await qwen.GenerateSqlAsync(request.Question, previousSql: null, validationError: null, cancellationToken);
        logger.LogInformation("SQL üretildi ({Source}, {Elapsed}ms): {Sql}", source, stopwatch.ElapsedMilliseconds, llm.Sql);

        GuardrailResult guarded;
        try
        {
            guarded = guardrail.ValidateAndRewrite(llm.Sql);
        }
        catch (SqlGuardrailException ex)
        {
            logger.LogWarning(ex, "Guardrail SQL'i reddetti, model ile yeniden denenecek.");
            source = "llm";
            llm = await qwen.GenerateSqlAsync(request.Question, llm.Sql, ex.Message, cancellationToken);
            guarded = guardrail.ValidateAndRewrite(llm.Sql);
        }

        IReadOnlyList<Dictionary<string, object?>> rows;
        try
        {
            rows = await db.QueryAsync(guarded.SafeSql, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SQL yürütme hatası, model ile yeniden denenecek.");
            source = "llm";
            llm = await qwen.GenerateSqlAsync(request.Question, guarded.SafeSql, ex.Message, cancellationToken);
            guarded = guardrail.ValidateAndRewrite(llm.Sql);
            rows = await db.QueryAsync(guarded.SafeSql, cancellationToken);
        }

        stopwatch.Stop();

        return new QueryExecutionResultDto
        {
            GeneratedSql = guarded.SafeSql,
            Explanation = llm.Explanation,
            Data = rows.ToList(),
            InsightSummary = string.Empty,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
            Assumptions = llm.Assumptions,
            Truncated = rows.Count >= SqlGuardrailService.MaxRows,
            RowCount = rows.Count,
            SqlSource = source
        };
    }

    public async Task<string> NarrateAsync(InsightRequestDto request, CancellationToken cancellationToken)
    {
        return await analysis.AnalyzeAsync(request.Question, request.Data, cancellationToken);
    }

    public Task<AlmBulletinDto> BulletinAsync(CancellationToken cancellationToken) =>
        bulletin.BuildAsync(cancellationToken);
}

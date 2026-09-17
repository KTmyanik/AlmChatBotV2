namespace AlmChatBot.Api.Models;

public sealed class QueryExecutionResultDto
{
    public string GeneratedSql { get; set; } = string.Empty;

    public string Explanation { get; set; } = string.Empty;

    public List<Dictionary<string, object?>> Data { get; set; } = [];

    public string InsightSummary { get; set; } = string.Empty;

    public long ExecutionDurationMs { get; set; }

    public List<string> Assumptions { get; set; } = [];

    public bool Truncated { get; set; }

    public int RowCount { get; set; }

    public string SqlSource { get; set; } = "llm";

    public bool NeedsConfirmation { get; set; }

    public string? SuggestedQuestion { get; set; }

    public string? InterpretationSummary { get; set; }

    public List<string> Corrections { get; set; } = [];

    public List<FollowUpSuggestionDto> FollowUps { get; set; } = [];
}

public sealed class FollowUpSuggestionDto
{
    public string Label { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;
}

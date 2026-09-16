namespace AlmChatBot.Api.Models;

public sealed class InsightRequestDto
{
    public string Question { get; set; } = string.Empty;

    public string Sql { get; set; } = string.Empty;

    public List<Dictionary<string, object?>> Data { get; set; } = [];

    public int RowCount { get; set; }
}

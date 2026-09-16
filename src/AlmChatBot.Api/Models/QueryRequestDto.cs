namespace AlmChatBot.Api.Models;

public sealed class QueryRequestDto
{
    public string Question { get; set; } = string.Empty;

    public bool Confirmed { get; set; }
}

namespace AlmChatBot.Api.Configuration;

public sealed class QwenConfig
{
    public const string SectionName = "QwenConfig";

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";

    public string ApiKey { get; set; } = "ollama";

    public string ModelName { get; set; } = "gemma:latest";
}

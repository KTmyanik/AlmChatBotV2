namespace AlmChatBot.Api.Exceptions;

public sealed class SqlGuardrailException : Exception
{
    public string? Sql { get; }

    public SqlGuardrailException(string message, string? sql = null)
        : base(message)
    {
        Sql = sql;
    }
}

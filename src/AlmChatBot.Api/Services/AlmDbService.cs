using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AlmChatBot.Api.Services;

public interface IAlmDbService
{
    Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken cancellationToken);
}

public sealed class AlmDbService(IConfiguration configuration, ILogger<AlmDbService> logger) : IAlmDbService
{
    private const int CommandTimeoutSeconds = 30;

    public async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("AlmDbReadOnly")
            ?? throw new InvalidOperationException("ConnectionStrings:AlmDbReadOnly eksik.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(
            sql,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken);

        logger.LogInformation("ALM read-only SELECT çalıştırılıyor.");
        var rows = await connection.QueryAsync(command);

        return rows.Select(ToDictionary).ToList();
    }

    private static Dictionary<string, object?> ToDictionary(dynamic row)
    {
        var source = (IDictionary<string, object>)row;
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            result[pair.Key] = Normalize(pair.Value);
        }

        return result;
    }

    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal or double or float or int or long or short or byte or bool or string or Guid => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}


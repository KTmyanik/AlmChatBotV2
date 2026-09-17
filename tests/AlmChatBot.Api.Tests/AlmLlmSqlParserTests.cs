using AlmChatBot.Api.Services;

namespace AlmChatBot.Api.Tests;

public sealed class AlmLlmSqlParserTests
{
    [Fact]
    public void Parses_truncated_explanation_if_sql_is_complete()
    {
        const string raw = """{"Sql":"SELECT 1 AS X FROM [ALM].[InternalReports]","Explanation":"Takipteki alacak bakiyesi """;

        var parsed = AlmLlmSqlParser.Parse(raw);

        Assert.Equal("SELECT 1 AS X FROM [ALM].[InternalReports]", parsed.Sql);
    }

    [Fact]
    public void Parses_well_formed_json()
    {
        const string raw = """{"Sql":"SELECT 1","Explanation":"ok","Assumptions":["a"]}""";

        var parsed = AlmLlmSqlParser.Parse(raw);

        Assert.Equal("SELECT 1", parsed.Sql);
        Assert.Equal("ok", parsed.Explanation);
        Assert.Equal("a", Assert.Single(parsed.Assumptions));
    }
}

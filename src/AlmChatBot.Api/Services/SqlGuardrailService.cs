using System.Globalization;
using AlmChatBot.Api.Exceptions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AlmChatBot.Api.Services;

public interface ISqlGuardrailService
{
    GuardrailResult ValidateAndRewrite(string sql);
}

public sealed record GuardrailResult(string SafeSql, bool TopWasInjected);

public sealed class SqlGuardrailService : ISqlGuardrailService
{
    public const int MaxRows = 200;

    private static readonly HashSet<string> AllowedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "InternalReports",
        "InternalDurationReports",
        "InternalReportMap",
        "CoreDepositRates"
    };

    public GuardrailResult ValidateAndRewrite(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new SqlGuardrailException("SQL boş olamaz.");
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors is { Count: > 0 })
        {
            var detail = string.Join("; ", errors.Select(e => $"satır {e.Line}: {e.Message}"));
            throw new SqlGuardrailException($"T-SQL ayrıştırılamadı: {detail}", sql);
        }

        if (fragment is not TSqlScript script)
        {
            throw new SqlGuardrailException("SQL betiği bekleniyor.", sql);
        }

        if (script.Batches.Count != 1)
        {
            throw new SqlGuardrailException("Yalnızca tek bir SQL batch'ine izin verilir.", sql);
        }

        var batch = script.Batches[0];
        if (batch.Statements.Count != 1)
        {
            throw new SqlGuardrailException("Yalnızca tek bir SQL ifadesine izin verilir.", sql);
        }

        if (batch.Statements[0] is not SelectStatement select)
        {
            throw new SqlGuardrailException("Yalnızca SELECT sorgularına izin verilir. DDL/DML reddedildi.", sql);
        }

        if (select.WithCtesAndXmlNamespaces?.ChangeTrackingContext is not null)
        {
            throw new SqlGuardrailException("CHANGE TRACKING ifadesine izin verilmez.", sql);
        }

        var inspector = new SelectSafetyVisitor(sql);
        select.Accept(inspector);

        var topInjected = EnsureTopLimit(select);

        var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            SqlVersion = SqlVersion.Sql160,
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = false,
            AlignClauseBodies = false
        });

        generator.GenerateScript(script, out var rewritten);
        return new GuardrailResult(rewritten.Trim(), topInjected);
    }

    private static bool EnsureTopLimit(SelectStatement select)
    {
        if (select.QueryExpression is QuerySpecification spec)
        {
            return ApplyTop(spec);
        }

        if (HasOuterTop(select.QueryExpression))
        {
            return false;
        }

        var inner = select.QueryExpression;
        select.QueryExpression = new QuerySpecification
        {
            TopRowFilter = CreateTopFilter(MaxRows),
            SelectElements = { new SelectStarExpression() },
            FromClause = new FromClause
            {
                TableReferences =
                {
                    new QueryDerivedTable
                    {
                        QueryExpression = inner,
                        Alias = new Identifier { Value = "guardrail_limited" }
                    }
                }
            }
        };

        return true;
    }

    private static bool HasOuterTop(QueryExpression expression) =>
        expression is QuerySpecification { TopRowFilter: not null };

    private static bool ApplyTop(QuerySpecification spec)
    {
        if (spec.TopRowFilter is null)
        {
            spec.TopRowFilter = CreateTopFilter(MaxRows);
            return true;
        }

        if (spec.TopRowFilter.Percent)
        {
            throw new SqlGuardrailException("TOP PERCENT ifadesine izin verilmez.");
        }

        if (spec.TopRowFilter.Expression is IntegerLiteral literal
            && int.TryParse(literal.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current)
            && current > MaxRows)
        {
            spec.TopRowFilter.Expression = new IntegerLiteral { Value = MaxRows.ToString(CultureInfo.InvariantCulture) };
            return true;
        }

        return false;
    }

    private static TopRowFilter CreateTopFilter(int rows) => new()
    {
        Expression = new IntegerLiteral { Value = rows.ToString(CultureInfo.InvariantCulture) }
    };

    private sealed class SelectSafetyVisitor(string originalSql) : TSqlFragmentVisitor
    {
        public override void Visit(SelectStatement node)
        {
            if (node.Into is not null)
            {
                throw new SqlGuardrailException("SELECT INTO ifadesine izin verilmez.", originalSql);
            }

            base.Visit(node);
        }

        public override void Visit(SelectSetVariable node)
        {
            throw new SqlGuardrailException("Değişken atamalı SELECT ifadesine izin verilmez.", originalSql);
        }

        public override void Visit(NamedTableReference node)
        {
            var obj = node.SchemaObject;
            if (obj.ServerIdentifier is not null)
            {
                throw new SqlGuardrailException("Linked server / dört parçalı isimlere izin verilmez.", originalSql);
            }

            var database = obj.DatabaseIdentifier?.Value;
            if (!string.IsNullOrEmpty(database)
                && !string.Equals(database, "IFRSStaging", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlGuardrailException($"Yalnızca IFRSStaging veritabanına izin verilir: {database}", originalSql);
            }

            var schema = obj.SchemaIdentifier?.Value;
            if (!string.IsNullOrEmpty(schema)
                && !string.Equals(schema, "ALM", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlGuardrailException($"Yalnızca [ALM] şemasına izin verilir: {schema}", originalSql);
            }

            var table = obj.BaseIdentifier?.Value;
            if (string.IsNullOrEmpty(table) || !AllowedTables.Contains(table))
            {
                throw new SqlGuardrailException($"Tablo allowlist dışında: {table}", originalSql);
            }

            QualifyAlmSchema(obj);

            base.Visit(node);
        }

        private static void QualifyAlmSchema(SchemaObjectName obj)
        {
            if (obj.SchemaIdentifier is not null)
            {
                return;
            }

            obj.Identifiers.Insert(obj.Identifiers.Count - 1, new Identifier { Value = "ALM" });
        }

        public override void Visit(OpenRowsetTableReference node) =>
            throw new SqlGuardrailException("OPENROWSET ifadesine izin verilmez.", originalSql);

        public override void Visit(OpenQueryTableReference node) =>
            throw new SqlGuardrailException("OPENQUERY ifadesine izin verilmez.", originalSql);

        public override void Visit(AdHocTableReference node) =>
            throw new SqlGuardrailException("Ad-hoc / OPENROWSET tablo kaynağına izin verilmez.", originalSql);

        public override void Visit(BulkOpenRowset node) =>
            throw new SqlGuardrailException("BULK ifadesine izin verilmez.", originalSql);

        public override void Visit(VariableTableReference node) =>
            throw new SqlGuardrailException("Tablo değişkenlerine izin verilmez.", originalSql);

        public override void Visit(SchemaObjectFunctionTableReference node) =>
            throw new SqlGuardrailException("Tablo değerli fonksiyon çağrılarına izin verilmez.", originalSql);

        public override void Visit(WaitForStatement node) =>
            throw new SqlGuardrailException("WAITFOR ifadesine izin verilmez.", originalSql);

        public override void Visit(ExecuteStatement node) =>
            throw new SqlGuardrailException("EXECUTE ifadesine izin verilmez.", originalSql);

        public override void Visit(InsertStatement node) =>
            throw new SqlGuardrailException("INSERT ifadesine izin verilmez.", originalSql);

        public override void Visit(UpdateStatement node) =>
            throw new SqlGuardrailException("UPDATE ifadesine izin verilmez.", originalSql);

        public override void Visit(DeleteStatement node) =>
            throw new SqlGuardrailException("DELETE ifadesine izin verilmez.", originalSql);

        public override void Visit(MergeStatement node) =>
            throw new SqlGuardrailException("MERGE ifadesine izin verilmez.", originalSql);

        public override void Visit(DropTableStatement node) =>
            throw new SqlGuardrailException("DROP ifadesine izin verilmez.", originalSql);

        public override void Visit(CreateTableStatement node) =>
            throw new SqlGuardrailException("CREATE TABLE ifadesine izin verilmez.", originalSql);

        public override void Visit(AlterTableStatement node) =>
            throw new SqlGuardrailException("ALTER TABLE ifadesine izin verilmez.", originalSql);

        public override void Visit(TruncateTableStatement node) =>
            throw new SqlGuardrailException("TRUNCATE ifadesine izin verilmez.", originalSql);
    }
}

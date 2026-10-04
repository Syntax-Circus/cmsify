using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Cmsify.Infrastructure.Sqlite.Persistence.JsonQueries;

internal sealed class SqliteJsonMethodCallTranslatorPlugin(ISqlExpressionFactory sql) : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new Translator(sql)];

    private sealed class Translator(ISqlExpressionFactory sql) : IMethodCallTranslator
    {
        private static readonly MethodInfo _getProperty = typeof(JsonElement).GetMethod(nameof(JsonElement.GetProperty), [typeof(string)])!;
        private static readonly MethodInfo _getString = typeof(JsonElement).GetMethod(nameof(JsonElement.GetString), Type.EmptyTypes)!;
        private const string UnsupportedValuePath = "Cmsify SQLite GetString supports only JSON strings or null";

        public SqlExpression? Translate(SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (instance is null) return null;
            if (method == _getProperty)
            {
                // JSON-quote each name as data, rather than interpolating it into SQL or a raw path.
                var name = sql.ApplyDefaultTypeMapping(arguments[0]);
                var path = sql.Add(sql.Constant("$."), Function("json_quote", [name]));
                var value = Function("json_extract", [instance, path]);
                // Keep JSON strings quoted between traversal steps. Otherwise a string containing
                // serialized JSON could incorrectly be traversed as an object on the next step.
                return sql.Function("json_quote", [value], nullable: false, argumentsPropagateNullability: [false],
                    typeof(JsonElement), instance.TypeMapping);
            }
            if (method != _getString) return null;
            var kind = Function("json_type", [instance]);
            var valueAtRoot = Function("json_extract", [instance, sql.Constant("$")]);
            // A bad native JSON path raises a descriptive SQLite error only if this CASE branch
            // is reached. Mixed kinds must not be silently cast to strings or classified as null.
            var unsupported = Function("json_extract", [sql.Constant("null"), sql.Constant(UnsupportedValuePath)]);
            return sql.Case(
                [new CaseWhenClause(sql.Equal(kind, sql.Constant("text")), valueAtRoot),
                 new CaseWhenClause(sql.OrElse(sql.IsNull(kind), sql.Equal(kind, sql.Constant("null"))), sql.Constant(null!, typeof(string), valueAtRoot.TypeMapping))],
                unsupported);
        }

        private SqlExpression Function(string name, IReadOnlyList<SqlExpression> arguments) =>
            sql.Function(name, arguments, nullable: true, argumentsPropagateNullability: arguments.Select(_ => false), typeof(string));
    }
}

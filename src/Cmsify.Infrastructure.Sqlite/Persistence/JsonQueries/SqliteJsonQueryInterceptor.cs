using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cmsify.Infrastructure.Sqlite.Persistence.JsonQueries;

internal sealed class SqliteJsonQueryInterceptor : IQueryExpressionInterceptor
{
    public static SqliteJsonQueryInterceptor Instance { get; } = new();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => new SupportedShapeVisitor().Visit(queryExpression);

    private sealed class SupportedShapeVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(JsonElement)) return base.VisitMethodCall(node);
            if (node.Method.Name == nameof(JsonElement.GetString) && node.Arguments.Count == 0)
            {
                var root = node.Object;
                var count = 0;
                while (root is MethodCallExpression property
                    && property.Method.DeclaringType == typeof(JsonElement)
                    && property.Method.Name == nameof(JsonElement.GetProperty)
                    && property.Arguments.Count == 1 && property.Arguments[0].Type == typeof(string))
                {
                    Visit(property.Arguments[0]);
                    root = property.Object;
                    count++;
                }
                if (count > 0)
                {
                    Visit(root);
                    return node;
                }
            }
            throw new NotSupportedException("Cmsify SQLite JSON queries support only GetProperty(string) object traversal ending in GetString(). Typed getters, array traversal and other JsonElement operations are unsupported.");
        }
    }
}

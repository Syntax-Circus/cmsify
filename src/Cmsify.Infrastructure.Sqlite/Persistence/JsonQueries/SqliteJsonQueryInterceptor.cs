using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cmsify.Infrastructure.Sqlite.Persistence.JsonQueries;

internal sealed class SqliteJsonQueryInterceptor : IQueryExpressionInterceptor
{
    public static SqliteJsonQueryInterceptor Instance { get; } = new();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => new SupportedShapeVisitor(eventData.Context?.Model).Visit(queryExpression);

    private sealed class SupportedShapeVisitor(IModel? model) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.DeclaringType == typeof(JsonElement) || node.Member.DeclaringType == typeof(JsonDocument))
                throw new NotSupportedException("Cmsify SQLite JSON member projections, including ValueKind, are unsupported. Project the whole mapped JSON value and inspect it explicitly after materialization.");
            return base.VisitMember(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            if (IsJsonType(node.Type) || HasMappedJsonProperties(node.Type)) throw UnsupportedComputedRoot();
            return base.VisitNew(node);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (IsJsonType(node.Type) && node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
                throw UnsupportedComputedRoot();
            return base.VisitUnary(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(JsonElement))
            {
                // Query terminal operators can return a mapped entity or its whole JSON
                // value; inspect their arguments, not their materialized result type.
                if (node.Method.DeclaringType != typeof(Queryable)
                    && node.Method.DeclaringType != typeof(EntityFrameworkQueryableExtensions)
                    && ((IsJsonType(node.Type) && !IsMappedJsonRoot(node)) || HasMappedJsonProperties(node.Type)))
                    throw UnsupportedComputedRoot();
                return base.VisitMethodCall(node);
            }
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
                if (count > 0 && IsMappedJsonRoot(root))
                {
                    Visit(root);
                    return node;
                }
            }
            throw new NotSupportedException("Cmsify SQLite JSON queries support only GetProperty(string) object traversal from a mapped JSON property ending in GetString(). Typed getters, array traversal, computed roots and other JsonElement operations are unsupported.");
        }

        private bool IsMappedJsonRoot(Expression? root)
        {
            if (root is MemberExpression { Member.Name: nameof(Nullable<JsonElement>.Value) } value
                && value.Member.DeclaringType == typeof(JsonElement?)) root = value.Expression;
            if (root is MemberExpression { Expression: ParameterExpression } property)
                return IsJsonProperty(property.Expression.Type, property.Member.Name);
            if (root is MethodCallExpression propertyAccess
                && propertyAccess.Method.DeclaringType == typeof(EF)
                && propertyAccess.Method.Name == nameof(EF.Property)
                && propertyAccess.Arguments.Count == 2
                && propertyAccess.Arguments[0] is ParameterExpression
                && propertyAccess.Arguments[1] is ConstantExpression { Value: string name })
                return IsJsonProperty(propertyAccess.Arguments[0].Type, name);
            return false;
        }

        private bool IsJsonProperty(Type entityType, string name)
        {
            var property = model?.FindEntityType(entityType)?.FindProperty(name);
            return property is not null && IsJsonType(property.ClrType);
        }

        private bool HasMappedJsonProperties(Type type) =>
            model?.FindEntityType(type)?.GetProperties().Any(property => IsJsonType(property.ClrType)) == true;

        private static bool IsJsonType(Type type) => type == typeof(JsonElement) || type == typeof(JsonElement?);

        private static NotSupportedException UnsupportedComputedRoot() => new(
            "Cmsify SQLite JSON queries do not support computed JSON producers, casts or constructed mapped-entity projection aliases. Use a direct mapped JSON property or EF.Property and perform any explicit client processing after materialization.");
    }
}

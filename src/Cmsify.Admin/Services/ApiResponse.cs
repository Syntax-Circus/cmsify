using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Admin.Services;

internal static class ApiResponse
{
    public static T Required<T>(T? value, string operation) where T : class =>
        value ?? throw new InvalidOperationException($"Cmsify API returned no payload after {operation}.");

    public static IReadOnlyList<T> ItemsOrEmpty<T>(PagedResponse<T>? page) =>
        page?.Items ?? [];
}

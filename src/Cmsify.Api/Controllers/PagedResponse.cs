namespace Cmsify.Api.Controllers;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize));
}

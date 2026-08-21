using System.Text.Json;

namespace SyntaxCircus.Cmsify.Client.Tests;

public sealed class ContractsSerializationTests
{
    [Fact]
    public void Enums_use_wire_string_values()
    {
        var json = JsonSerializer.Serialize(new { role = UserRole.Admin }, CmsifyJsonOptions.Create());

        json.ShouldContain("\"role\":\"Admin\"");
    }

    [Fact]
    public void Paged_response_calculates_total_pages()
    {
        var page = new PagedResponse<string>(["one"], 11, 2, 5);

        page.TotalPages.ShouldBe(3);
    }
}

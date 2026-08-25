using System.Text.Json;
using System.ComponentModel.DataAnnotations;

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

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Content_list_query_rejects_out_of_range_pagination(int page, int pageSize)
    {
        var query = new ContentListQuery(null, null, null, null, null, null, null, null, null, null, null, null, Page: page, PageSize: pageSize);
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(query, new ValidationContext(query), validationResults, validateAllProperties: true);

        isValid.ShouldBeFalse();
        validationResults.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Audit_query_rejects_out_of_range_pagination(int page, int pageSize)
    {
        var query = new AuditQueryRequest(null, null, null, null, null, null, null, Page: page, PageSize: pageSize);
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(query, new ValidationContext(query), validationResults, validateAllProperties: true);

        isValid.ShouldBeFalse();
        validationResults.ShouldNotBeEmpty();
    }
}

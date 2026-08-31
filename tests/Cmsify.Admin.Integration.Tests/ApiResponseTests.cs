using Cmsify.Admin.Services;

namespace Cmsify.Admin.Integration.Tests;

public sealed class ApiResponseTests
{
    [Fact]
    public void Required_ReturnsProvidedReference()
    {
        var expected = new object();

        var result = ApiResponse.Required(expected, "loading a resource");

        result.ShouldBeSameAs(expected);
    }

    [Fact]
    public void Required_ThrowsOperationSpecificExceptionForMissingPayload()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            ApiResponse.Required<object>(null, "creating a resource"));

        exception.Message.ShouldBe("Cmsify API returned no payload after creating a resource.");
    }

    [Fact]
    public void ItemsOrEmpty_ReturnsResponseItems()
    {
        IReadOnlyList<string> expected = ["first", "second"];
        var page = new PagedResponse<string>(expected, expected.Count, 1, 20);

        var result = ApiResponse.ItemsOrEmpty(page);

        result.ShouldBeSameAs(expected);
    }

    [Fact]
    public void ItemsOrEmpty_ReturnsEmptyListForMissingPage()
    {
        var result = ApiResponse.ItemsOrEmpty<string>(null);

        result.ShouldBeEmpty();
    }
}

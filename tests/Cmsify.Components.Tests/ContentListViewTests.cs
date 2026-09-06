using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentListViewTests : BunitContext
{
    private static ContentItemSummaryResponse CreateItem(string slug) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Article", ContentStatus.Draft, slug, null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public void RendersOneRowPerItem()
    {
        var items = new[] { CreateItem("first"), CreateItem("second") };

        var cut = Render<ContentListView>(parameters => parameters.Add(p => p.Items, items));

        cut.FindAll("tbody tr").Count.ShouldBe(2);
    }

    [Fact]
    public void RaisesOnEditWhenEditButtonClicked()
    {
        var item = CreateItem("first");
        ContentItemSummaryResponse? edited = null;
        var cut = Render<ContentListView>(parameters => parameters
            .Add(p => p.Items, new[] { item })
            .Add(p => p.OnEdit, EventCallback.Factory.Create<ContentItemSummaryResponse>(this, i => edited = i)));

        cut.Find(".cmsify-list-edit-button").Click();

        edited.ShouldBe(item);
    }

    [Fact]
    public void RaisesOnFilterFromFilterButton()
    {
        var filtered = false;
        var cut = Render<ContentListView>(parameters => parameters
            .Add(p => p.OnFilter, EventCallback.Factory.Create(this, () => filtered = true)));

        cut.Find(".cmsify-list-filter-button").Click();

        filtered.ShouldBeTrue();
    }

    [Fact]
    public void RendersRowActionsFragmentWhenProvided()
    {
        var item = CreateItem("first");
        var cut = Render<ContentListView>(parameters => parameters
            .Add(p => p.Items, new[] { item })
            .Add(p => p.RowActions, (RenderFragment<ContentItemSummaryResponse>)(rowItem => builder =>
                builder.AddMarkupContent(0, $"<button class=\"custom-action\">Act on {rowItem.Slug}</button>"))));

        cut.Find(".custom-action").TextContent.ShouldBe("Act on first");
    }
}

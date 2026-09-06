using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void ProjectReferenceResolves()
    {
        typeof(ContentFieldEditorValue).Assembly.GetName().Name.ShouldBe("Cmsify.Components");
    }
}

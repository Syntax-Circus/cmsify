using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;

namespace Cmsify.Core.Tests;

public sealed class TemplateGraphValidatorTests
{
    [Fact]
    public void ValidateCycles_ReturnsFailure_ForDirectCycle()
    {
        var templateId = Guid.CreateVersion7();
        var version = new TemplateVersion { TemplateId = templateId, VersionNumber = 1 };
        version.Fields.Add(new TemplateField
        {
            TemplateVersionId = version.Id,
            Key = "self",
            Label = "Self",
            CompositionMode = CompositionMode.Reference,
            TemplateId = templateId
        });

        var result = new TemplateGraphValidator().ValidateCycles(version);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCycles_ReturnsFailure_ForTransitiveCycle()
    {
        var templateAId = Guid.CreateVersion7();
        var templateBId = Guid.CreateVersion7();
        var versionA = new TemplateVersion { TemplateId = templateAId, VersionNumber = 1 };
        var versionB = new TemplateVersion { TemplateId = templateBId, VersionNumber = 1 };

        versionA.Fields.Add(new TemplateField
        {
            TemplateVersionId = versionA.Id,
            Key = "child",
            Label = "Child",
            CompositionMode = CompositionMode.Inline,
            TemplateId = templateBId,
            ReferencedTemplateVersion = versionB
        });
        versionB.Fields.Add(new TemplateField
        {
            TemplateVersionId = versionB.Id,
            Key = "parent",
            Label = "Parent",
            CompositionMode = CompositionMode.Reference,
            TemplateId = templateAId,
            ReferencedTemplateVersion = versionA
        });

        var result = new TemplateGraphValidator().ValidateCycles(versionA);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCycles_ReturnsSuccess_WhenGraphIsAcyclic()
    {
        var templateAId = Guid.CreateVersion7();
        var templateBId = Guid.CreateVersion7();
        var versionA = new TemplateVersion { TemplateId = templateAId, VersionNumber = 1 };
        var versionB = new TemplateVersion { TemplateId = templateBId, VersionNumber = 1 };

        versionA.Fields.Add(new TemplateField
        {
            TemplateVersionId = versionA.Id,
            Key = "child",
            Label = "Child",
            CompositionMode = CompositionMode.Inline,
            TemplateId = templateBId,
            ReferencedTemplateVersion = versionB
        });

        var result = new TemplateGraphValidator().ValidateCycles(versionA);

        Assert.True(result.IsValid);
    }
}

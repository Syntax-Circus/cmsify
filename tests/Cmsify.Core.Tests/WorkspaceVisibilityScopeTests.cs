using System.Collections;
using Cmsify.Core.Workspaces;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class WorkspaceVisibilityScopeTests
{
    [Fact]
    public void Factories_copy_deduplicate_and_reject_empty_ids()
    {
        var id = Guid.NewGuid();
        var source = new[] { id, id };
        var scope = WorkspaceVisibilityScope.ForWorkspaces(source);
        source[0] = Guid.NewGuid();
        scope.Kind.ShouldBe(WorkspaceVisibilityScopeKind.Restricted);
        scope.WorkspaceIds.ShouldBe(new[] { id });
        Should.Throw<NotSupportedException>(() => ((IList)scope.WorkspaceIds)[0] = Guid.NewGuid());
        Should.Throw<ArgumentException>(() => WorkspaceVisibilityScope.ForWorkspaces([Guid.Empty]));
        Should.Throw<ArgumentNullException>(() => WorkspaceVisibilityScope.ForWorkspaces(null!));
    }

    [Fact]
    public void None_is_empty_restricted()
    {
        WorkspaceVisibilityScope.None.Kind.ShouldBe(WorkspaceVisibilityScopeKind.Restricted);
        WorkspaceVisibilityScope.None.WorkspaceIds.ShouldBeEmpty();
        WorkspaceVisibilityScope.CmsManaged.Kind.ShouldBe(WorkspaceVisibilityScopeKind.CmsManaged);
        WorkspaceVisibilityScope.CmsManaged.WorkspaceIds.ShouldBeEmpty();
    }

    [Fact]
    public void Public_payload_has_no_transport_or_provider_types()
    {
        typeof(WorkspaceVisibilityScope).GetConstructors().ShouldBeEmpty();
        typeof(WorkspaceVisibilityScope).GetProperties().ShouldAllBe(property => property.SetMethod == null);
        typeof(WorkspaceVisibilityScope).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(property => property.PropertyType)
            .ShouldBe(new[] { typeof(WorkspaceVisibilityScopeKind), typeof(IReadOnlyList<Guid>) });
        typeof(IWorkspaceVisibilityScopeProvider).GetMethods().Single().ReturnType
            .ShouldBe(typeof(Task<WorkspaceVisibilityScope>));
    }
}

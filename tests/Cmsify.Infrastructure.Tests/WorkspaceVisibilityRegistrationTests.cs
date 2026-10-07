using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class WorkspaceVisibilityRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Default_is_scoped_CmsManaged_and_honors_cancellation()
    {
        var services = Register(new ServiceCollection());
        services.Single(item => item.ServiceType == typeof(IWorkspaceVisibilityScopeProvider)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var visibility = scope.ServiceProvider.GetRequiredService<IWorkspaceVisibilityScopeProvider>();
        (await visibility.ResolveAsync(Ct)).ShouldBeSameAs(WorkspaceVisibilityScope.CmsManaged);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => visibility.ResolveAsync(cancelled.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_override_survives_repeat_registration_and_is_operation_scoped(bool before)
    {
        var services = new ServiceCollection();
        if (before) services.AddScoped<IWorkspaceVisibilityScopeProvider, HostVisibility>();
        Register(services);
        if (!before) services.Replace(ServiceDescriptor.Scoped<IWorkspaceVisibilityScopeProvider, HostVisibility>());
        var descriptors = services.ToArray();
        Register(services);
        services.ShouldBe(descriptors);
        using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstVisibility = first.ServiceProvider.GetRequiredService<IWorkspaceVisibilityScopeProvider>();
        firstVisibility.ShouldBeOfType<HostVisibility>();
        firstVisibility.ShouldNotBeSameAs(second.ServiceProvider.GetRequiredService<IWorkspaceVisibilityScopeProvider>());
        (await firstVisibility.ResolveAsync(Ct)).ShouldBeSameAs(WorkspaceVisibilityScope.None);
    }

    private static ServiceCollection Register(ServiceCollection services)
    {
        services.AddCmsifyInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cmsify"] = "Host=localhost;Database=registration_only"
        }).Build(), new CmsifyInfrastructureOptions { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
        return services;
    }

    private sealed class HostVisibility : IWorkspaceVisibilityScopeProvider
    {
        public Task<WorkspaceVisibilityScope> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(WorkspaceVisibilityScope.None);
    }
}

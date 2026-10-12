using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileRegistrationTests
{
    private static ServiceCollection Services()
    {
        var services=new ServiceCollection();
        services.AddCmsifyInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Cmsify"]="Host=127.0.0.1;Database=unused;Username=unused" }).Build(),new CmsifyInfrastructureOptions { Workers=(CmsifyWorkers)0 });
        return services;
    }
    [Fact]
    public void EnabledRegistrationRequiresExplicitGuardAndRejectsConflicts()
    {
        var services=Services();
        Should.Throw<InvalidOperationException>(()=>services.AddCmsifyEmbeddedFiles(o=>o.Enabled=true));
        services.AddSingleton(Substitute.For<IEmbeddedFileAuthorizationService>());
        services.AddCmsifyEmbeddedFiles(o=>o.Enabled=true);
        services.AddCmsifyEmbeddedFiles(o=>o.Enabled=true);
        Should.Throw<InvalidOperationException>(()=>services.AddCmsifyEmbeddedFiles(o=>o.Enabled=false));
        services.Count(x=>x.ServiceType==typeof(IBeginEmbeddedFileUploadRequestHandler)).ShouldBe(1);
    }
    [Fact]
    public void RegistrationDoesNotRequirePhotosAndValidatesLeaseBounds()
    {
        var services=Services();
        Should.Throw<ArgumentException>(()=>services.AddCmsifyEmbeddedFiles(o=>o.UploadLeaseDuration=o.UploadDeadline));
        services.AddCmsifyEmbeddedFiles(_=>{});
        services.Any(x=>x.ServiceType.FullName!.Contains("IEmbeddedPhotoProcessor",StringComparison.Ordinal)).ShouldBeFalse();
    }
}

using System.Diagnostics;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Cmsify.Infrastructure.Persistence.EmbeddedFiles;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileCrashTests
{
    [Fact]
    public async Task KilledReferenceWriterLeavesDurableProtectionAndRelease()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var session=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct);
        var reference=Guid.NewGuid();var protectKey=Guid.NewGuid();var releaseKey=Guid.NewGuid();
        foreach(var boundary in new[] { "after-protect","after-release" })
        {
            var marker=Path.Combine(h.Root,boundary);var config=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var host=Path.Combine(FindRoot(),"tests","Cmsify.File.CrashHost","bin",config,"net10.0","Cmsify.File.CrashHost.dll");
            var info=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
            foreach(var arg in new[] {host,boundary,h.Database.ConnectionString,h.Root,h.WorkspaceId.ToString(),reference.ToString(),(boundary=="after-protect"?protectKey:releaseKey).ToString(),h.ActorId.ToString(),marker})info.ArgumentList.Add(arg);
            using var child=Process.Start(info)!;var stderr=child.StandardError.ReadToEndAsync(ct);var stdout=child.StandardOutput.ReadToEndAsync(ct);
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
                while(!File.Exists(marker)){if(child.HasExited)throw new InvalidOperationException($"Reference helper exited {child.ExitCode}: {await stderr}");await Task.Delay(25,timeout.Token);}
                child.Kill(true);await child.WaitForExitAsync(ct);child.ExitCode.ShouldNotBe(0);
            }
            finally { if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync(CancellationToken.None);} }
            await stdout;await stderr;
            await using var fresh=h.Database.Context();
            var repository=new EmbeddedFileReferenceRepository(fresh,new(h.Provider,"local"),Options.Create(h.Settings),[],TimeProvider.System);
            var outcome=await repository.GetOutcomeAsync(new(h.WorkspaceId,"dog-documents",reference),h.ActorId,EmbeddedFileFixture.Permit,ct);
            outcome.Value.OperationKey.ShouldBe(protectKey);outcome.Value.Files.Count.ShouldBe(1);
            outcome.Value.State.ShouldBe(boundary=="after-protect"?EmbeddedFileReferenceState.Protected:EmbeddedFileReferenceState.Released);
        }
    }
    [Theory]
    [InlineData("before-store")]
    [InlineData("after-store")]
    public async Task KilledWriterRequiresExplicitReplayAndCreatesOneReceipt(string boundary)
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var begin=h.Begin();var session=(await h.Repository().BeginAsync(begin,h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var writeKey=Guid.NewGuid();var marker=Path.Combine(h.Root,"boundary");Directory.CreateDirectory(h.Root);
        var root=FindRoot();var config=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host=Path.Combine(root,"tests","Cmsify.File.CrashHost","bin",config,"net10.0","Cmsify.File.CrashHost.dll");
        File.Exists(host).ShouldBeTrue("Release must build the real crash helper");
        var info=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        foreach(var arg in new[] {host,boundary,h.Database.ConnectionString,h.Root,h.WorkspaceId.ToString(),session.SessionId.ToString(),writeKey.ToString(),h.ActorId.ToString(),marker})info.ArgumentList.Add(arg);
        using var child=Process.Start(info)!;var stdout=child.StandardOutput.ReadToEndAsync(ct);var stderr=child.StandardError.ReadToEndAsync(ct);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while(!File.Exists(marker)) { if(child.HasExited)throw new InvalidOperationException($"Crash helper exited {child.ExitCode}: {await stderr}");await Task.Delay(25,timeout.Token); }
            child.Kill(entireProcessTree:true);await child.WaitForExitAsync(ct);child.ExitCode.ShouldNotBe(0);
        }
        finally { if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync(CancellationToken.None);} }
        await stdout;await stderr;
        await using var fresh=h.Database.Context();
        (await fresh.FileUploadReceipts.CountAsync(ct)).ShouldBe(0);
        await fresh.FileUploadSessions.Where(x=>x.Id==session.SessionId).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.LeaseUntil,DateTimeOffset.UtcNow.AddSeconds(-1)),ct);
        var outcome=await h.Repository(fresh).GetOutcomeAsync(new(h.WorkspaceId,session.SessionId),h.ActorId,EmbeddedFileFixture.Permit,ct);
        outcome.Value.State.ShouldBe(EmbeddedFileUploadState.Interrupted);
        (await fresh.FileUploadReceipts.CountAsync(ct)).ShouldBe(0);
        var replay=await h.Repository(fresh).WriteAsync(new(h.WorkspaceId,session.SessionId,writeKey,new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct);
        replay.IsSuccess.ShouldBeTrue();replay.Value.Receipt!.SourceSha256.ShouldBe(EmbeddedFileFixture.Digest);
        (await fresh.FileUploadReceipts.CountAsync(ct)).ShouldBe(1);
        (await fresh.FileUploadAttempts.CountAsync(ct)).ShouldBe(2);
    }
    private static string FindRoot()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"Cmsify.slnx")))return directory.FullName;
        throw new DirectoryNotFoundException("Cmsify solution root missing.");
    }
}

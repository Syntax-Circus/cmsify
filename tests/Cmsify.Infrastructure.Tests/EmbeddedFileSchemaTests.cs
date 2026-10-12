using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileSchemaTests
{
    [Fact]
    public async Task AdditiveMigrationRepeatsAndPreservesStandaloneAndPhotoRows()
    {
        await using var h=await EmbeddedPhotoFixture.Create();var ct=EmbeddedPhotoFixture.Ct;
        await using var db=h.Context();var migrations=(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        var fileMigration=migrations.Single(x=>x.EndsWith("_AddEmbeddedFiles",StringComparison.Ordinal));
        var previous=migrations[Array.IndexOf(migrations,fileMigration)-1];
        await db.GetService<IMigrator>().MigrateAsync(previous,ct);
        var standalone=h.Asset("ordinary/document.pdf");var photo=h.Asset("cmsify/media/photos/synthetic/original");
        db.MediaAssets.AddRange(standalone,photo);db.PhotoUploadSessions.Add(h.Session(photo));await db.SaveChangesAsync(ct);
        await db.Database.MigrateAsync(ct);await db.Database.MigrateAsync(ct);
        (await db.FileUploadSessions.CountAsync(ct)).ShouldBe(0);
        (await db.PhotoUploadSessions.CountAsync(ct)).ShouldBe(1);
        (await db.MediaAssets.CountAsync(ct)).ShouldBe(2);
    }
}

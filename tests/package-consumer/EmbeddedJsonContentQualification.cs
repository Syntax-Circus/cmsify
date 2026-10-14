using System.Text;
using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Common;

internal static partial class EmbeddedContentQualification
{
    private const int JsonDocumentBytes = 256 * 1024;
    private const string JsonContractKey = "puppies-plus.site-document.v1";

    private static async Task RunJsonDocumentsAsync(IServiceProvider services, Guid workspace, CancellationToken ct)
    {
        var contract = new EmbeddedTemplateContract(JsonContractKey, "Site document", "site-document-v1", "title",
        [
            new("title", "Title", 0, true, 1, 1, PrimitiveType.Text, ValueKind.Text, CompositionMode.Inline, false,
                JsonSerializer.SerializeToElement(new { maxLength = 200, formatHint = "plaintext" })),
            new("document", "Document", 1, true, 1, 1, PrimitiveType.Text, ValueKind.Text, CompositionMode.Inline, false,
                JsonSerializer.SerializeToElement(new { maxLength = JsonDocumentBytes, formatHint = "json" }))
        ]);
        var fingerprint = EmbeddedContractRules.Fingerprint(contract);
        var templateId = Guid.Empty;
        async Task<T> Call<T>(Guid item, EmbeddedContentOperationKind? kind, Func<IServiceProvider, Task<Result<T>>> action, int version = 1)
        {
            await using var scope = services.CreateAsyncScope();
            Bind(scope.ServiceProvider, new(workspace, fingerprint, templateId, item == Guid.Empty ? null : item,
                version, kind, Setup: kind is null, ContractKey: JsonContractKey));
            return Success(await action(scope.ServiceProvider));
        }
        var schema = await Call(Guid.Empty, null, p => p.GetRequiredService<IEnsureEmbeddedTemplateRequestHandler>().HandleAsync(new(workspace, contract), ct));
        templateId = schema.TemplateVersionId;
        var repeated = await Call(Guid.Empty, null, p => p.GetRequiredService<IEnsureEmbeddedTemplateRequestHandler>().HandleAsync(new(workspace, contract), ct));
        Require(repeated.TemplateVersionId == templateId, "JSON setup changed identity.");
        ContentVersionFieldInput[] Values(string document) => schema.Fields.Select(f => new ContentVersionFieldInput(f.FieldId,
            0, ValueKind.Text, f.Schema.Key == "title" ? "Beseme" : document, null, null, null, null, null)).ToArray();

        foreach (var shape in new[] { "repeated", "unicode", "sections" })
        {
            var document = JsonDocumentOfShape(shape);
            Require(Encoding.UTF8.GetByteCount(document) == JsonDocumentBytes, "JSON fixture byte length differs.");
            using var valid = JsonDocument.Parse(document);
            var item = Guid.NewGuid();
            var create = new CreateEmbeddedContentRequest(workspace, templateId, fingerprint, item, Guid.NewGuid(), Values(document));
            var created = await Call(item, EmbeddedContentOperationKind.ItemCreate, p => p.GetRequiredService<ICreateEmbeddedContentRequestHandler>().HandleAsync(create, ct));
            var receipt = await Call(item, EmbeddedContentOperationKind.ReceiptRead, p => p.GetRequiredService<IGetEmbeddedContentOperationReceiptRequestHandler>()
                .HandleAsync(new(workspace, item, EmbeddedWriteKind.ItemCreate, create.OperationKey, fingerprint), ct));
            Require(receipt == created.Receipt, "JSON creation receipt differs.");
            var replay = await Call(item, EmbeddedContentOperationKind.ItemCreate, p => p.GetRequiredService<ICreateEmbeddedContentRequestHandler>().HandleAsync(create, ct));
            Require(replay.Receipt == receipt, "JSON creation replay differs.");
            var read = await Call(item, EmbeddedContentOperationKind.VersionRead, p => p.GetRequiredService<IGetEmbeddedContentVersionRequestHandler>()
                .HandleAsync(new(workspace, item, 1, templateId, fingerprint, new(receipt.CommittedRevision)), ct));
            Require(read.Version.Fields.Single(f => f.Key == "document").TextValue == document, "JSON saved bytes differ.");
            var changed = document.Replace("paragraphs", "paragraphx", StringComparison.Ordinal);
            var updated = await Call(item, EmbeddedContentOperationKind.VersionUpdate, p => p.GetRequiredService<IUpdateContentVersionRequestHandler>()
                .HandleAsync(new(workspace, item, 1, new(read.Revision), null, null, Values(changed), false), ct));
            Require(updated.Version.Fields.Single(f => f.Key == "document").TextValue == changed, "JSON update differs.");
            var next = new CreateEmbeddedContentVersionRequest(workspace, item, 1, new(updated.Revision), templateId, fingerprint, Guid.NewGuid(), Values(document));
            var version = await Call(item, EmbeddedContentOperationKind.VersionCreate, p => p.GetRequiredService<ICreateEmbeddedContentVersionRequestHandler>().HandleAsync(next, ct));
            Require(version.Receipt.VersionNumber == 2 && version.Version.Version.Status == ContentStatus.Draft
                && version.Version.Version.Fields.Single(f => f.Key == "document").TextValue == document, "JSON new draft differs.");
            var versionReceipt = await Call(item, EmbeddedContentOperationKind.ReceiptRead, p => p.GetRequiredService<IGetEmbeddedContentOperationReceiptRequestHandler>()
                .HandleAsync(new(workspace, item, EmbeddedWriteKind.VersionCreate, next.OperationKey, fingerprint), ct), 2);
            Require(versionReceipt == version.Receipt, "JSON version receipt differs.");
            Require((await Call(item, EmbeddedContentOperationKind.VersionCreate, p => p.GetRequiredService<ICreateEmbeddedContentVersionRequestHandler>().HandleAsync(next, ct))).Receipt == versionReceipt,
                "JSON new-version replay differs.");
            var source = await Call(item, EmbeddedContentOperationKind.VersionRead, p => p.GetRequiredService<IGetEmbeddedContentVersionRequestHandler>()
                .HandleAsync(new(workspace, item, 1, templateId, fingerprint, new(updated.Revision)), ct));
            Require(source.Version.Fields.Single(f => f.Key == "document").TextValue == changed, "JSON new-version mutated source.");
            await using var oracle = services.CreateAsyncScope();
            var vector = (await oracle.ServiceProvider.GetRequiredService<CmsifyDbContext>().ContentItems.SingleAsync(i => i.Id == item, ct)).SearchVector;
            Require(vector is not null && vector.Contains("beseme") && !vector.Contains("paragraph"), "JSON search exclusion/title indexing differs.");
        }
        Console.WriteLine("PASS embedded JSON documents: 3 exact 256KiB shapes, persisted create/read/update/new-version, receipts/replay, unchanged source and searchable title only.");
    }

    private static string JsonDocumentOfShape(string shape)
    {
        const string prefix = "{\"paragraphs\":[\"";
        const string suffix = "\"]}";
        var size = JsonDocumentBytes - prefix.Length - suffix.Length;
        if (shape == "unicode") return prefix + new string('界', size / 3) + new string('a', size % 3) + suffix;
        if (shape == "repeated") return prefix + string.Concat(Enumerable.Repeat("word ", size / 5)) + new string('a', size % 5) + suffix;
        return JsonSerializer.Serialize(new { paragraphs = Enumerable.Range(0, 24).Select(i => new { type = "paragraph", text = "Section " + i }) }).PadRight(JsonDocumentBytes);
    }
}

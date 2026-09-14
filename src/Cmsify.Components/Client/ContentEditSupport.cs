using System.Collections.Immutable;

namespace SyntaxCircus.Cmsify.Components.Client;

public static class ContentEditSupport
{
    // Depth cap for Inline composition recursion, matching ContentController's own child-resolution
    // depth guard server-side. TemplateGraphValidator skips cycle validation entirely for IsOpen
    // fields, so this client-side guard (plus disabling ancestor-chain templates in the picker) is a
    // real safety requirement against runaway/circular recursion, not defensive theater.
    public const int MaxInlineDepth = 8;

    // Produces a new ancestor-chain set with templateId added, without requiring callers to know
    // AncestorTemplateIds is backed by ImmutableHashSet<Guid> - used wherever recursion descends one
    // level deeper (load, save, and the InlineChildContentEditor render tree all need this).
    public static IReadOnlySet<Guid> WithAncestor(IReadOnlySet<Guid> ancestors, Guid templateId) =>
        (ancestors as ImmutableHashSet<Guid> ?? ImmutableHashSet.CreateRange(ancestors)).Add(templateId);

    public static async Task LoadPickListsAsync(CmsifyClient client, Guid workspaceId, IEnumerable<TemplateFieldResponse> fields, Dictionary<Guid, PickListResponse> into)
    {
        var bindings = fields
            .Where(field => field.PrimitiveType == PrimitiveType.PickList)
            .Select(field => PickListFieldBinding.FromFieldConfig(field.FieldConfig))
            .Where(binding => binding.PickListId.HasValue && binding.RevisionId.HasValue)
            .Select(binding => (binding.PickListId!.Value, binding.RevisionId!.Value))
            .Distinct()
            .Where(binding => !into.ContainsKey(binding.Item2))
            .ToList();

        // Fire every distinct pick-list revision lookup concurrently rather than one at a time.
        var lookups = bindings
            .Select(binding => (RevisionId: binding.Item2, Task: client.PickLists.GetRevisionAsync(workspaceId, binding.Item1, binding.Item2)))
            .ToList();

        try
        {
            await Task.WhenAll(lookups.Select(l => l.Task));
        }
        catch (CmsifyApiException)
        {
        }

        foreach (var (revisionId, task) in lookups)
        {
            try
            {
                var pickList = await task;
                if (pickList is not null)
                {
                    into[revisionId] = pickList;
                }
            }
            catch (CmsifyApiException)
            {
            }
        }
    }

    public static async Task LoadReferenceOptionsAsync(CmsifyClient client, Guid workspaceId, IEnumerable<TemplateFieldResponse> fields, Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> into)
    {
        var templateIds = fields
            .SelectMany(field => field.AllowedTypes.Where(a => a.AllowedTemplateId.HasValue).Select(a => a.AllowedTemplateId!.Value)
                .Concat(field.TemplateId.HasValue ? [field.TemplateId.Value] : []))
            .Distinct()
            .Where(templateId => !into.ContainsKey(templateId))
            .ToList();

        // Every distinct referenced template's option list is independent of the others - fetch
        // them all concurrently instead of one at a time.
        var lookups = templateIds
            .Select(templateId => (TemplateId: templateId, Task: client.Content.ListAsync(workspaceId, null, templateId, null, null, null)))
            .ToList();
        await Task.WhenAll(lookups.Select(l => l.Task));

        foreach (var (templateId, task) in lookups)
        {
            into[templateId] = task.Result?.Items ?? [];
        }
    }

    // Walks a decoded ComponentInstanceValue tree (recursing into each field's own nested
    // ComponentValues, at any depth) collecting every leaf ContentFieldEditorValue that decoded a
    // Media/File asset id - so the caller can resolve them with the same concurrent-fetch,
    // CmsifyApiException-tolerant code already used for top-level Media/File fields.
    public static IEnumerable<ContentFieldEditorValue> CollectValuesWithFallbackAssets(ComponentInstanceValue instance)
    {
        foreach (var value in instance.FieldValues.Values)
        {
            if (value.FallbackMediaAssetId.HasValue || value.FallbackFileAssetId.HasValue)
            {
                yield return value;
            }

            foreach (var nested in value.ComponentValues)
            {
                foreach (var found in CollectValuesWithFallbackAssets(nested))
                {
                    yield return found;
                }
            }
        }
    }

    // Loads one Inline child content item into an editable InlineChildInstance, recursing into any
    // of its own Inline sub-fields. Mirrors ContentEditPanel.LoadContentAsync's per-field population
    // loop one level down, for a child content item rather than the top-level one being edited.
    //
    // Opening a parent for editing mints a Draft for each existing Inline child that doesn't already
    // have one (when versionNumber is null AND allowDraftCreation is true) - a deliberate product
    // choice (not incidental): the nested editor always needs something editable to bind to, and
    // minting eagerly here is far simpler than deferring it to first-edit. If this proves
    // undesirable in practice it's a follow-up, not something this pass needs to get exactly right.
    //
    // allowDraftCreation is false for read-only viewing or an explicit historical VersionNumber -
    // draft-creation requires Editor role server-side, so minting one unconditionally while merely
    // viewing (possibly as a Viewer-role user) either 403s or has an unwanted side effect of creating
    // Draft rows nobody asked for.
    //
    // depth is the authoritative recursion terminator (see MaxInlineDepth) - ancestorTemplateIds is
    // a set and stops growing once a template repeats in the chain (A -> B -> A -> B -> ...), so it
    // alone cannot bound recursion against cyclic real content data. depth always increases by
    // exactly one per recursive call regardless of repeats, guaranteeing termination.
    public static async Task<InlineChildInstance> LoadInlineChildInstanceAsync(
        CmsifyClient client, Guid workspaceId, Guid childContentItemId, int? versionNumber,
        IReadOnlySet<Guid> ancestorTemplateIds, int depth, bool allowDraftCreation, CancellationToken ct = default)
    {
        var item = await client.Content.GetAsync(workspaceId, childContentItemId, ct: ct)
            ?? throw new InvalidOperationException("Cmsify API returned no payload while loading inline child content.");

        int resolvedVersionNumber;
        if (versionNumber.HasValue)
        {
            resolvedVersionNumber = versionNumber.Value;
        }
        else
        {
            var draft = item.Versions
                .Where(v => v.Status == ContentStatus.Draft)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefault();

            if (draft is not null)
            {
                resolvedVersionNumber = draft.VersionNumber;
            }
            else
            {
                var servingVersionNumber = item.CurrentlyServingVersion?.VersionNumber
                    ?? item.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault()?.VersionNumber
                    ?? 1;
                if (allowDraftCreation)
                {
                    var created = await client.Content.CreateVersionAsync(workspaceId, childContentItemId,
                        new CreateContentVersionRequest(null, null, servingVersionNumber, null), ct);
                    resolvedVersionNumber = created?.VersionNumber ?? servingVersionNumber;
                }
                else
                {
                    resolvedVersionNumber = servingVersionNumber;
                }
            }
        }

        var version = await client.Content.GetVersionAsync(workspaceId, childContentItemId, resolvedVersionNumber, ct)
            ?? throw new InvalidOperationException("Cmsify API returned no payload while loading an inline child content version.");

        var templates = (await client.Templates.ListAsync(workspaceId, ct: ct))?.Items ?? [];
        var templateSummary = templates.FirstOrDefault(t => t.CurrentVersionId == version.TemplateVersionId);

        var instance = new InlineChildInstance
        {
            ContentItemId = childContentItemId,
            TemplateId = templateSummary?.Id,
            VersionNumber = resolvedVersionNumber,
            Slug = version.Slug,
            Locale = version.LocaleCode,
            Tags = string.Join(",", version.Tags),
            TranslationGroupId = item.TranslationGroupId,
            EffectiveStartAt = version.EffectiveStartAt,
            EffectiveEndAt = version.EffectiveEndAt,
        };

        if (templateSummary is null || depth >= MaxInlineDepth)
        {
            return instance;
        }

        var fullTemplate = await client.Templates.GetAsync(workspaceId, templateSummary.Id, ct);
        var templateVersion = fullTemplate?.CurrentVersion;
        if (templateVersion is null)
        {
            return instance;
        }

        var fieldsById = templateVersion.Fields.ToDictionary(f => f.Id);
        var componentSchemas = await ComponentSchemaResolver.ResolveAsync(
            client, workspaceId, templateVersion.Fields.Where(f => f.ComponentId.HasValue).Select(f => f.ComponentId!.Value), ct);

        var childAncestors = WithAncestor(ancestorTemplateIds, templateSummary.Id);
        var inlineLookups = new List<(ContentFieldEditorValue Value, Guid ChildContentItemId, Task<InlineChildInstance> Task)>();
        var mediaLookups = new List<(ContentFieldEditorValue Value, Guid AssetId, Task<MediaAssetResponse?> Task)>();
        var fileLookups = new List<(ContentFieldEditorValue Value, Guid AssetId, Task<MediaAssetResponse?> Task)>();

        foreach (var fieldValue in version.Fields)
        {
            var editorValue = instance.FieldValues.TryGetValue(fieldValue.FieldId, out var existing)
                ? existing
                : instance.FieldValues[fieldValue.FieldId] = new ContentFieldEditorValue();
            var field = fieldsById.GetValueOrDefault(fieldValue.FieldId);

            if (field?.CompositionMode == CompositionMode.Inline && fieldValue.ChildContentItemId is { } nestedChildId)
            {
                inlineLookups.Add((editorValue, nestedChildId, LoadInlineChildInstanceAsync(
                    client, workspaceId, nestedChildId, null, childAncestors, depth + 1, allowDraftCreation, ct)));
                continue;
            }

            editorValue.TextValue = fieldValue.TextValue;
            editorValue.BoolValue = fieldValue.BoolValue ?? false;
            editorValue.ChildContentItemId = fieldValue.ChildContentItemId;
            if (!string.IsNullOrWhiteSpace(fieldValue.TextValue))
            {
                editorValue.MultiValues = [.. editorValue.MultiValues, fieldValue.TextValue];
            }

            if (fieldValue.ValueKind == ValueKind.Component && fieldValue.JsonValue.HasValue && field?.ComponentId is { } componentId)
            {
                var componentInstance = ComponentValueSerializer.Deserialize(fieldValue.JsonValue.Value, componentId, componentSchemas);
                editorValue.ComponentValues = [.. editorValue.ComponentValues, componentInstance];

                foreach (var nestedValue in CollectValuesWithFallbackAssets(componentInstance))
                {
                    if (nestedValue.FallbackMediaAssetId is { } nestedMediaId)
                    {
                        mediaLookups.Add((nestedValue, nestedMediaId, client.Media.GetAsync(workspaceId, nestedMediaId, ct: ct)));
                    }
                    if (nestedValue.FallbackFileAssetId is { } nestedFileId)
                    {
                        fileLookups.Add((nestedValue, nestedFileId, client.Media.GetAsync(workspaceId, nestedFileId, ct: ct)));
                    }
                }
            }
            if (fieldValue.MediaAssetId.HasValue)
            {
                mediaLookups.Add((editorValue, fieldValue.MediaAssetId.Value, client.Media.GetAsync(workspaceId, fieldValue.MediaAssetId.Value, ct: ct)));
            }
            if (fieldValue.FileAssetId.HasValue)
            {
                fileLookups.Add((editorValue, fieldValue.FileAssetId.Value, client.Media.GetAsync(workspaceId, fieldValue.FileAssetId.Value, ct: ct)));
            }
        }

        // A single failed grandchild load must not take down the whole load - each lookup is awaited
        // and handled independently (mirroring the media/file fault-tolerance below), and a failure
        // becomes a LoadFailed placeholder that still carries the known ContentItemId, so a later
        // save preserves this link instead of silently dropping it (see SaveInlineFieldAsync).
        try
        {
            await Task.WhenAll(inlineLookups.Select(l => l.Task));
        }
        catch (CmsifyApiException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        foreach (var (editorValue, nestedChildId, task) in inlineLookups)
        {
            try
            {
                editorValue.ChildInstances = [.. editorValue.ChildInstances, await task];
            }
            catch (Exception ex) when (ex is CmsifyApiException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                editorValue.ChildInstances = [.. editorValue.ChildInstances, new InlineChildInstance { ContentItemId = nestedChildId, LoadFailed = true }];
            }
        }

        try
        {
            await Task.WhenAll(mediaLookups.Select(l => l.Task).Concat(fileLookups.Select(l => l.Task)));
        }
        catch (CmsifyApiException)
        {
        }

        foreach (var (editorValue, assetId, task) in mediaLookups)
        {
            try
            {
                editorValue.SelectedMediaAsset = await task;
            }
            catch (CmsifyApiException)
            {
                editorValue.FallbackMediaAssetId = assetId;
            }
        }
        foreach (var (editorValue, assetId, task) in fileLookups)
        {
            try
            {
                editorValue.SelectedFileAsset = await task;
            }
            catch (CmsifyApiException)
            {
                editorValue.FallbackFileAssetId = assetId;
            }
        }

        return instance;
    }

    // Saves every non-deleted instance of one Inline field, children before parents, and returns one
    // ContentFieldValueRequest(ChildContent) per surviving instance for the caller to append to the
    // parent's own field-value list. No transactional multi-item save endpoint exists server-side, so
    // this sequencing is deliberately best-effort-with-safe-ordering:
    //   1. Depth-first, children before parents: each instance's own Inline children are saved (via
    //      the recursive call below) before the instance itself is created/updated. Pre-flight
    //      occurrence-count validation across the WHOLE top-level instance tree happens once, in the
    //      caller, before any Inline field's writes begin - not here (see InlineChildValidation and
    //      ContentEditPanel.SaveAsync).
    //   2. On any failure, this method throws immediately - callers must not attempt the parent's own
    //      save, and instances already saved before the failure remain persisted (a retry is then
    //      idempotent for them, since they're no longer "new").
    //   3. This method never deletes a MarkedForDeletion instance found directly in `instances` - the
    //      caller must do that only after ITS OWN (parent) save succeeds, so an aborted parent save
    //      never leaves an already-deleted id that the parent still (would have) referenced. Those
    //      instances are left untouched in the input list for the caller to find and delete (see
    //      DeleteInlineInstanceRecursivelyAsync). MarkedForDeletion instances found one level deeper
    //      (nested inside a surviving instance's own Inline sub-fields) ARE deleted here, but only
    //      after that surviving instance's own create/update call below has succeeded - mirroring the
    //      same rule one recursion level down.
    //
    // ancestorTemplateIds/depth mirror the load-side guard: depth is the authoritative terminator
    // (an ancestor set alone cannot bound recursion against cyclic real content, since it stops
    // growing once a template repeats in the chain) and is threaded one deeper on every recursive
    // call regardless of repeats.
    public static async Task<IReadOnlyList<ContentFieldValueRequest>> SaveInlineFieldAsync(
        CmsifyClient client, Guid workspaceId, TemplateFieldResponse field, IList<InlineChildInstance> instances,
        IReadOnlySet<Guid> ancestorTemplateIds, int depth, CancellationToken ct = default)
    {
        if (depth >= MaxInlineDepth)
        {
            throw new InvalidOperationException(
                $"'{field.Label}' exceeds the maximum inline nesting depth ({MaxInlineDepth}) and cannot be saved. This usually indicates circular inline content.");
        }

        var requests = new List<ContentFieldValueRequest>();
        var order = field.Order;

        foreach (var instance in instances)
        {
            if (instance.MarkedForDeletion)
            {
                // Deletion is the caller's responsibility, after its own save succeeds.
                continue;
            }

            if (instance.LoadFailed)
            {
                // This instance's own content failed to load client-side - there is no field data to
                // save, but the link must be preserved (not silently dropped), or this field's whole
                // ChildContent row set would be rebuilt from `instances` minus this one, orphaning
                // every healthy sibling of the same field too.
                if (instance.ContentItemId is { } preservedId)
                {
                    requests.Add(new ContentFieldValueRequest(field.Id, order, ValueKind.ChildContent, null, null, null, null, preservedId, null));
                    order++;
                }
                continue;
            }

            if (instance.TemplateId is not { } templateId)
            {
                // Not-yet-committed slot (picker still open, no TemplateId chosen): nothing to save yet.
                continue;
            }

            var template = await client.Templates.GetAsync(workspaceId, templateId, ct)
                ?? throw new InvalidOperationException("Cmsify API returned no payload while resolving an inline child's template.");
            var templateVersion = template.CurrentVersion
                ?? throw new InvalidOperationException($"Template '{template.Name}' has no published version to save inline content against.");

            var componentSchemas = await ComponentSchemaResolver.ResolveAsync(
                client, workspaceId, templateVersion.Fields.Where(f => f.ComponentId.HasValue).Select(f => f.ComponentId!.Value), ct);

            var childAncestors = WithAncestor(ancestorTemplateIds, templateId);
            var pendingDeletions = new List<(IList<InlineChildInstance> List, InlineChildInstance Instance)>();

            var childValues = new List<ContentFieldValueRequest>();
            foreach (var childField in templateVersion.Fields.OrderBy(f => f.Order))
            {
                var childValue = instance.FieldValues.TryGetValue(childField.Id, out var cv) ? cv : new ContentFieldEditorValue();

                if (childField.ComponentId.HasValue)
                {
                    if (!componentSchemas.TryGetValue(childField.ComponentId.Value, out var schema) || schema.CurrentVersion is null)
                    {
                        throw new InvalidOperationException($"Component field '{childField.Label}' schema could not be resolved.");
                    }
                    foreach (var componentInstance in childValue.ComponentValues)
                    {
                        var serialized = ComponentValueSerializer.Serialize(componentInstance, childField.ComponentId.Value, componentSchemas);
                        childValues.Add(new ContentFieldValueRequest(childField.Id, childField.Order, ValueKind.Component, null, null, null, null, null, serialized));
                    }
                    continue;
                }

                if (childField.CompositionMode == CompositionMode.Inline)
                {
                    // Children before parents: this recursive call fully saves (or throws for) this
                    // instance's own Inline children before the instance itself is saved below.
                    var nestedRequests = await SaveInlineFieldAsync(client, workspaceId, childField, childValue.ChildInstances, childAncestors, depth + 1, ct);
                    childValues.AddRange(nestedRequests);

                    // A grandchild-level MarkedForDeletion instance is deleted only after THIS
                    // instance's own create/update below succeeds - never before, for the same reason
                    // the top-level caller waits for its own save (see DeleteMarkedInlineChildrenAsync).
                    foreach (var toDelete in childValue.ChildInstances.Where(i => i.MarkedForDeletion && i.ContentItemId.HasValue).ToList())
                    {
                        pendingDeletions.Add((childValue.ChildInstances, toDelete));
                    }
                    continue;
                }

                var isChildComposition = childField.TemplateId.HasValue || childField.IsOpen || childField.AllowedTypes.Any(a => a.AllowedTemplateId.HasValue);
                if (isChildComposition)
                {
                    if (childValue.ChildContentItemId is { } referencedId)
                    {
                        childValues.Add(new ContentFieldValueRequest(childField.Id, childField.Order, ValueKind.ChildContent, null, null, null, null, referencedId, null));
                    }
                    continue;
                }

                if (childField.PrimitiveType == PrimitiveType.PickList)
                {
                    var binding = PickListFieldBinding.FromFieldConfig(childField.FieldConfig);
                    if (binding.Multiple)
                    {
                        var index = 0;
                        foreach (var selectedValue in childValue.MultiValues)
                        {
                            childValues.Add(new ContentFieldValueRequest(childField.Id, childField.Order + index, ValueKind.PickList, selectedValue, null, null, null, null, null));
                            index++;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(childValue.TextValue))
                    {
                        childValues.Add(new ContentFieldValueRequest(childField.Id, childField.Order, ValueKind.PickList, childValue.TextValue, null, null, null, null, null));
                    }
                    continue;
                }

                childValues.Add(new ContentFieldValueRequest(
                    childField.Id,
                    childField.Order,
                    childField.PrimitiveType switch
                    {
                        PrimitiveType.Boolean => ValueKind.Boolean,
                        PrimitiveType.Media => ValueKind.Media,
                        PrimitiveType.File => ValueKind.File,
                        PrimitiveType.Markdown => ValueKind.Markdown,
                        PrimitiveType.RichText => ValueKind.RichText,
                        PrimitiveType.Link => ValueKind.Link,
                        PrimitiveType.Quote => ValueKind.Quote,
                        PrimitiveType.Separator => ValueKind.Separator,
                        _ => ValueKind.Text
                    },
                    childValue.TextValue,
                    childValue.BoolValue,
                    childField.PrimitiveType == PrimitiveType.Media ? childValue.SelectedMediaAsset?.Id ?? childValue.FallbackMediaAssetId : null,
                    childField.PrimitiveType == PrimitiveType.File ? childValue.SelectedFileAsset?.Id ?? childValue.FallbackFileAssetId : null,
                    null,
                    null));
            }

            var tagList = (instance.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (instance.ContentItemId is { } existingChildId)
            {
                var childVersionNumber = instance.VersionNumber ?? 1;
                _ = await client.Content.UpdateVersionAsync(workspaceId, existingChildId, childVersionNumber,
                    new UpdateContentVersionRequest(instance.EffectiveStartAt, instance.EffectiveEndAt, childValues), ct)
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while updating an inline child's content version.");

                // Same ETag-refresh reasoning as ContentEditPanel.SaveAsync: the version PUT above
                // bumps the item's UpdatedAt server-side, invalidating the item ETag captured at load
                // time. A GET here refreshes CmsifyClient's internally tracked ETag for this item's
                // URI before the item PUT below reuses it as If-Match.
                _ = await client.Content.GetAsync(workspaceId, existingChildId, ct: ct);

                _ = await client.Content.UpdateAsync(workspaceId, existingChildId,
                    new UpdateContentItemRequest(string.IsNullOrWhiteSpace(instance.Slug) ? null : instance.Slug, instance.Locale, instance.TranslationGroupId, tagList), ct)
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while updating an inline child's content item.");
            }
            else
            {
                var created = await client.Content.CreateAsync(workspaceId,
                    new CreateContentItemRequest(templateVersion.Id, string.IsNullOrWhiteSpace(instance.Slug) ? null : instance.Slug, instance.Locale, instance.TranslationGroupId, tagList, childValues), ct)
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while creating inline child content.");

                instance.ContentItemId = created.Id;
                instance.VersionNumber = created.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault()?.VersionNumber
                    ?? created.CurrentlyServingVersion?.VersionNumber ?? 1;

                // Prime the SDK's per-URI ETag cache for this child's version sub-resource. CreateAsync's
                // response came from POST /content (the collection) - never a GET/PUT against the
                // version's own URI - so without this, a second save of this same child later in the
                // same session would send its UpdateVersionAsync PUT with no If-Match header and get
                // rejected with 412, with no recovery path (that 412 doesn't match the reload-and-retry
                // handling meant for the parent's own save conflict).
                _ = await client.Content.GetVersionAsync(workspaceId, instance.ContentItemId.Value, instance.VersionNumber.Value, ct);
            }

            // Only now that THIS instance's own save has succeeded is it safe to delete any
            // MarkedForDeletion instances nested inside its own Inline sub-fields - recursively, so a
            // marked grandchild's own further descendants are cleaned up too (no server-side cascade
            // delete exists for Inline children at any depth).
            foreach (var (list, toDelete) in pendingDeletions)
            {
                await DeleteInlineInstanceRecursivelyAsync(client, workspaceId, toDelete, ct);
                list.Remove(toDelete);
            }

            requests.Add(new ContentFieldValueRequest(field.Id, order, ValueKind.ChildContent, null, null, null, null, instance.ContentItemId, null));
            order++;
        }

        return requests;
    }

    // Deletes one Inline child instance and, recursively, every one of its own nested Inline
    // descendants first - the instance is being wholly removed, so anything inside it becomes
    // unreachable garbage regardless of whether the user explicitly marked it for deletion, and no
    // server-side cascade delete exists to clean that up. A never-persisted instance (no
    // ContentItemId) has nothing to delete. Only ChildInstances populated by Inline-mode loading are
    // ever walked here - Reference-mode fields never populate ChildInstances, so this is safe to run
    // over every FieldValue unconditionally without re-resolving the instance's own template.
    public static async Task DeleteInlineInstanceRecursivelyAsync(
        CmsifyClient client, Guid workspaceId, InlineChildInstance instance, CancellationToken ct = default)
    {
        if (instance.ContentItemId is not { } contentItemId)
        {
            return;
        }

        foreach (var value in instance.FieldValues.Values)
        {
            foreach (var nested in value.ChildInstances.ToList())
            {
                await DeleteInlineInstanceRecursivelyAsync(client, workspaceId, nested, ct);
            }
        }

        // Same ETag-refresh reasoning as ContentEditPanel.SaveAsync's pre-item-PUT refresh and
        // SaveInlineFieldAsync's post-version-PUT refresh: loading this child (see
        // LoadInlineChildInstanceAsync) may have minted a Draft version via CreateVersionAsync for a
        // child that had none, which bumps the item's UpdatedAt (and therefore its ETag) server-side
        // with no fresh ETag ever returned to the client for that side effect. The SDK's cached ETag
        // for this item's URI can therefore be stale by the time this delete runs (which may be much
        // later, after the whole parent save has succeeded) - a GET here refreshes it immediately
        // before DeleteAsync reuses it as If-Match, otherwise this deterministically 412s with no
        // recovery path (a 412 response carries no fresh ETag to retry with).
        _ = await client.Content.GetAsync(workspaceId, contentItemId, ct: ct);

        await client.Content.DeleteAsync(workspaceId, contentItemId, ct);
    }
}

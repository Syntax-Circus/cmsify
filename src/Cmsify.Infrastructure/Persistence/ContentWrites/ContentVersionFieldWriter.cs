using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.ContentWrites;

public sealed class ContentVersionFieldWriter(CmsifyDbContext dbContext, IContentValidator contentValidator)
{
    public async Task<string?> ApplyAsync(ContentVersion version, TemplateVersion templateVersion, IReadOnlyList<ContentVersionFieldInput> fields, CancellationToken ct)
    {
        // Explicit removal (rather than relying on cascade-delete orphan detection from .Clear()
        // alone) mirrors the API's template-upgrade pattern and is required for correctness once
        // this method is called against an already-tracked version (UpdateVersion): without it, the
        // old rows are left behind as stale duplicates alongside the newly-added ones.
        if (version.FieldValues.Count > 0)
        {
            dbContext.ContentVersionFieldValues.RemoveRange(version.FieldValues);
            version.FieldValues.Clear();
        }

        var fieldPickLists = templateVersion.Fields
            .Where(field => field.PrimitiveType == PrimitiveType.PickList)
            .Select(field => (field.Id, PickListId: GetPickListId(field), RevisionId: GetPickListRevisionId(field)))
            .Where(x => x.PickListId.HasValue)
            .ToDictionary(x => x.Id);
        var pickListIds = fieldPickLists.Values.Select(x => x.PickListId!.Value).Distinct().ToArray();
        var currentLabels = await dbContext.PickLists.AsNoTracking().Include(list => list.Options)
            .Where(list => pickListIds.Contains(list.Id))
            .ToDictionaryAsync(list => list.Id, list => list.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);
        var revisionIds = fieldPickLists.Values.Select(x => x.RevisionId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var revisionLabels = await dbContext.PickListRevisions.AsNoTracking().Include(revision => revision.Options)
            .Where(revision => revisionIds.Contains(revision.Id))
            .ToDictionaryAsync(revision => revision.Id, revision => revision.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);

        // Repeated values of one field (component instances, multi-selects) are positioned by Order.
        // If a client sent duplicates (older editors wrote the same Order for every instance), renumber
        // them from the smallest Order in submitted sequence so position survives reads and duplication.
        var orders = new int[fields.Count];
        foreach (var group in fields.Select((input, index) => (input, index)).GroupBy(x => x.input.FieldId))
        {
            var members = group.ToList();
            var distinct = members.Select(x => x.input.Order).Distinct().Count() == members.Count;
            var start = members.Min(x => x.input.Order);
            for (var position = 0; position < members.Count; position++)
            {
                orders[members[position].index] = distinct ? members[position].input.Order : start + position;
            }
        }

        for (var inputIndex = 0; inputIndex < fields.Count; inputIndex++)
        {
            var input = fields[inputIndex];
            var displayLabel = input.ValueKind == ValueKind.PickList && input.TextValue is not null && fieldPickLists.TryGetValue(input.FieldId, out var binding)
                ? (binding.RevisionId.HasValue && revisionLabels.TryGetValue(binding.RevisionId.Value, out var versionedOptions) ? versionedOptions : currentLabels.GetValueOrDefault(binding.PickListId!.Value))?.GetValueOrDefault(input.TextValue)
                : null;

            var fieldValue = new ContentVersionFieldValue
            {
                ContentVersionId = version.Id,
                FieldId = input.FieldId,
                Order = orders[inputIndex],
                ValueKind = input.ValueKind,
                TextValue = input.TextValue,
                DisplayLabel = displayLabel,
                BoolValue = input.BoolValue,
                MediaAssetId = input.MediaAssetId,
                FileAssetId = input.FileAssetId,
                ChildContentItemId = input.ChildContentItemId,
                JsonValue = input.JsonValue?.Clone()
            };
            // Explicitly track as Added: version may already be tracked (e.g. on UpdateVersion, where
            // the parent ContentVersion was loaded, not newly constructed), and both this entity's PK
            // and the store default are set client-side (EntityBase.Id), so EF's graph painter cannot
            // infer "Added" from navigation-collection membership alone - without this, it treats the
            // child as "Modified" and issues an UPDATE against a row that doesn't exist yet, which
            // fails with 0 rows affected (DbUpdateConcurrencyException).
            //
            // When version is already tracked, DbSet.Add below performs automatic relationship fixup
            // and appends fieldValue to version.FieldValues itself (matching ContentVersionId to the
            // tracked parent); when version is not yet tracked (Create/CreateVersion, where it's added
            // to the context later), no such fixup happens and the explicit Add is required to build
            // the graph. Guard against double-adding into the navigation collection in the former case.
            dbContext.ContentVersionFieldValues.Add(fieldValue);
            if (!version.FieldValues.Contains(fieldValue))
            {
                version.FieldValues.Add(fieldValue);
            }
        }

        var validation = contentValidator.Validate(version, templateVersion);
        if (!validation.IsValid)
        {
            return string.Join(" ", validation.Errors.Select(error => error.ErrorMessage));
        }

        return await ValidateAsync(version, templateVersion, ct);
    }

    private static Guid? GetPickListId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }

    private static Guid? GetPickListRevisionId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistRevisionId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }

    public async Task<string?> ValidateAsync(ContentVersion version, TemplateVersion templateVersion, CancellationToken ct)
    {
        var bindings = new Dictionary<Guid, (string Key, Guid RevisionId, bool Multiple)>();
        foreach (var field in templateVersion.Fields.Where(field => field.PrimitiveType == PrimitiveType.PickList))
        {
            if (!TryGetPickListBinding(field.FieldConfig, out var revisionId, out var multiple))
            {
                return $"Field '{field.Key}' must bind a PickList revision.";
            }

            bindings[field.Id] = (field.Key, revisionId, multiple);
        }

        var valuesByRevision = new Dictionary<Guid, HashSet<string>>();
        if (bindings.Count > 0)
        {
            var optionValues = await dbContext.PickListRevisionOptions.AsNoTracking()
                .Where(option => bindings.Values.Select(binding => binding.RevisionId).Contains(option.PickListRevisionId))
                .Select(option => new { option.PickListRevisionId, option.Value })
                .ToListAsync(ct);
            valuesByRevision = optionValues
                .GroupBy(option => option.PickListRevisionId)
                .ToDictionary(group => group.Key, group => group.Select(option => option.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        foreach (var group in version.FieldValues.Where(value => bindings.ContainsKey(value.FieldId)).GroupBy(value => value.FieldId))
        {
            var binding = bindings[group.Key];
            if (!binding.Multiple && group.Count() > 1)
            {
                return $"Field '{binding.Key}' allows only one PickList selection.";
            }

            if (!valuesByRevision.TryGetValue(binding.RevisionId, out var allowedValues))
            {
                return $"Field '{binding.Key}' references an unavailable PickList revision.";
            }

            foreach (var value in group)
            {
                if (string.IsNullOrWhiteSpace(value.TextValue) || !allowedValues.Contains(value.TextValue))
                {
                    return $"Field '{binding.Key}' contains a value that is not in its PickList revision.";
                }
            }
        }

        var revisionValueCache = valuesByRevision;
        foreach (var field in templateVersion.Fields.Where(field => field.ComponentId.HasValue))
        {
            foreach (var value in version.FieldValues.Where(value => value.FieldId == field.Id && value.JsonValue is not null))
            {
                if (await ValidateComponentPickListValuesAsync(field.ComponentId!.Value, value.JsonValue!.Value, revisionValueCache, ct) is { } componentError)
                {
                    return $"Field '{field.Key}': {componentError}";
                }
            }
        }

        return null;
    }

    private async Task<string?> ValidateComponentPickListValuesAsync(Guid componentId, JsonElement value, Dictionary<Guid, HashSet<string>> revisionValueCache, CancellationToken ct)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return "component values must be JSON objects.";
        }

        var component = await dbContext.Components.AsNoTracking()
            .Include(candidate => candidate.Versions).ThenInclude(candidate => candidate.Fields)
            .FirstOrDefaultAsync(candidate => candidate.Id == componentId && !candidate.IsDeleted, ct);
        if (component is null)
        {
            return "references an unavailable component schema.";
        }

        var version = component.Versions.FirstOrDefault(candidate => candidate.Id == component.CurrentVersionId && candidate.Status == TemplateVersionStatus.Published && !candidate.IsDeleted);
        if (version is null)
        {
            return "references an unavailable component schema.";
        }

        foreach (var field in version.Fields)
        {
            if (!value.TryGetProperty(field.Key, out var property))
            {
                if (field.IsRequired)
                {
                    return $"component field '{field.Key}' is required.";
                }
                continue;
            }

            if (field.PrimitiveType == PrimitiveType.PickList)
            {
                if (!TryGetPickListBinding(field.FieldConfig, out var revisionId, out var multiple))
                {
                    return $"component field '{field.Key}' must bind a PickList revision.";
                }

                if (!revisionValueCache.TryGetValue(revisionId, out var allowedValues))
                {
                    allowedValues = (await dbContext.PickListRevisionOptions.AsNoTracking()
                        .Where(option => option.PickListRevisionId == revisionId)
                        .Select(option => option.Value)
                        .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    revisionValueCache[revisionId] = allowedValues;
                }

                var submittedValues = property.ValueKind == JsonValueKind.Array
                    ? property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToArray()
                    : property.ValueKind == JsonValueKind.String ? [property.GetString()] : [];
                if (submittedValues.Length == 0 || (!multiple && submittedValues.Length != 1) || submittedValues.Any(item => string.IsNullOrWhiteSpace(item) || !allowedValues.Contains(item)))
                {
                    return $"component field '{field.Key}' contains a value that is not in its PickList revision.";
                }
            }

            if (field.NestedComponentId.HasValue)
            {
                var nestedValues = property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : [property];
                foreach (var nestedValue in nestedValues)
                {
                    if (await ValidateComponentPickListValuesAsync(field.NestedComponentId.Value, nestedValue, revisionValueCache, ct) is { } nestedError)
                    {
                        return nestedError;
                    }
                }
            }
        }

        return null;
    }

    private static bool TryGetPickListBinding(JsonElement? fieldConfig, out Guid revisionId, out bool multiple)
    {
        revisionId = Guid.Empty;
        multiple = false;
        if (fieldConfig is not { ValueKind: JsonValueKind.Object } config
            || !config.TryGetProperty("picklistRevisionId", out var revision)
            || revision.ValueKind != JsonValueKind.String
            || !Guid.TryParse(revision.GetString(), out revisionId))
        {
            return false;
        }

        if (config.TryGetProperty("multiple", out var multipleValue) && multipleValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            multiple = multipleValue.GetBoolean();
        }

        return true;
    }

}

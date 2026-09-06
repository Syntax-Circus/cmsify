namespace SyntaxCircus.Cmsify.Components;

public sealed class ContentFieldEditorValue
{
    public string? TextValue { get; set; }
    public bool BoolValue { get; set; }
    public Guid? ChildContentItemId { get; set; }
    public MediaAssetResponse? SelectedMediaAsset { get; set; }
    public MediaAssetResponse? SelectedFileAsset { get; set; }
    public Guid? FallbackMediaAssetId { get; set; }
    public Guid? FallbackFileAssetId { get; set; }
    public IReadOnlyList<string> MultiValues { get; set; } = [];
    public IReadOnlyList<string> ComponentValues { get; set; } = [];
}

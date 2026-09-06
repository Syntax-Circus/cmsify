using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Admin.Services;

public static class ContentWorkflowActions
{
    public static (bool Enabled, string? DisabledReason) SubmitState(ContentStatus status) =>
        status == ContentStatus.Draft ? (true, null) : (false, $"Submit requires Draft status (current: {status}).");

    public static (bool Enabled, string? DisabledReason) ApproveState(ContentStatus status) =>
        status == ContentStatus.Review ? (true, null) : (false, $"Approve requires Review status (current: {status}).");

    public static (bool Enabled, string? DisabledReason) RejectState(ContentStatus status, UserRole currentRole)
    {
        if (currentRole < UserRole.TemplateAdmin)
        {
            return (false, "Reject requires Template Admin role or higher.");
        }

        return status == ContentStatus.Review ? (true, null) : (false, $"Reject requires Review status (current: {status}).");
    }

    public static (bool Enabled, string? DisabledReason) PublishState(ContentStatus status, UserRole currentRole)
    {
        if (status == ContentStatus.Approved)
        {
            return (true, null);
        }

        if (currentRole >= UserRole.Admin && status is ContentStatus.Draft or ContentStatus.Review)
        {
            return (true, null);
        }

        return (false, $"Publish requires Approved status (current: {status}).");
    }

    public static (bool Enabled, string? DisabledReason) ArchiveState(ContentStatus status) =>
        status == ContentStatus.Published ? (true, null) : (false, $"Archive requires Published status (current: {status}).");

    public static (bool Enabled, string? DisabledReason) RestoreState(ContentStatus status, UserRole currentRole)
    {
        if (currentRole < UserRole.Editor)
        {
            return (false, "Restore requires Editor role or higher.");
        }

        return status == ContentStatus.Archived ? (true, null) : (false, $"Restore requires Archived status (current: {status}).");
    }
}

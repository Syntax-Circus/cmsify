namespace Cmsify.Admin.Auth;

public static class CmsifyAuthClaims
{
    public const string ApiToken = "cmsify:api_token";
    public const string ApiTokenExpiresAt = "cmsify:api_token_expires_at";
    public const string IsSuperAdmin = "cmsify:super_admin";
    public const string MustChangePassword = "cmsify:must_change_password";
}

using SyntaxCircus.Cmsify;

var cmsify = new CmsifyClient(new CmsifyClientOptions
{
    BaseUrl = new Uri(Environment.GetEnvironmentVariable("CMSIFY_API_URL")!),
    ApiToken = Environment.GetEnvironmentVariable("CMSIFY_API_TOKEN")
});

var workspaces = await cmsify.Workspaces.ListAsync();
Console.WriteLine($"Found {workspaces.Count} workspace(s).");

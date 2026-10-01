using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Cmsify.Contracts;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class AuthLoginTests : IAsyncLifetime
{
    private const string AdminEmail = "admin@example.test";
    private const string AdminPassword = "change-this-temporary-password";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", AdminEmail);
        Environment.SetEnvironmentVariable("Seed__Admin__Password", AdminPassword);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Email", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Password", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", null);
    }

    [Fact]
    public async Task Login_Succeeds_AndLogsInformation()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Information && entry.Message.StartsWith("Login succeeded.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_FailureReasons_AllReturnIdentical401_AndLogWarningWithoutSecrets()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();
        // Force the app (and migrations/seed) to start.
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var hash = BCrypt.Net.BCrypt.HashPassword("user-password", 4);
            db.Users.AddRange(
                new User { Email = "inactive@example.test", DisplayName = "Inactive", PasswordHash = hash, IsActive = false },
                new User { Email = "deleted@example.test", DisplayName = "Deleted", PasswordHash = hash, IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var attempts = new (string Email, string Password, string Reason)[]
        {
            ("nobody@example.test", "whatever", "UserNotFound"),
            ("deleted@example.test", "user-password", "UserDeleted"),
            ("inactive@example.test", "user-password", "UserInactive"),
            (AdminEmail, "wrong-password-value", "BadPassword")
        };

        var bodies = new List<string>();
        foreach (var (email, password, _) in attempts)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            // traceId legitimately differs per request; everything else must be identical.
            bodies.Add($"{problem.RootElement.GetProperty("type")}|{problem.RootElement.GetProperty("title")}|{problem.RootElement.GetProperty("status")}");
        }

        Assert.Single(bodies.Distinct());

        var warnings = logs.Entries.Where(entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("Login failed.", StringComparison.Ordinal)).ToList();
        Assert.Equal(attempts.Length, warnings.Count);
        foreach (var (email, _, reason) in attempts)
        {
            Assert.Contains(warnings, warning => warning.Message.Contains($"Reason={reason}", StringComparison.Ordinal) && warning.Message.Contains($"Email={email}", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("wrong-password-value", StringComparison.Ordinal) || entry.Message.Contains("$2a$", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dead-session-token-value")]
    [InlineData("cmsify_deadbeefdeadbeef")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln")]
    public async Task Login_Succeeds_WhenRequestCarriesStaleBearerToken(string staleToken)
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", staleToken);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_PrefersActiveUser_WhenSoftDeletedUserSharesEmail()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // Email uniqueness is filtered on is_deleted = false, so a deleted row and a live row can share an address.
        // Insert the deleted row first so a naive unfiltered lookup would find it.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            db.Users.Add(new User { Email = "reused@example.test", DisplayName = "Old", PasswordHash = BCrypt.Net.BCrypt.HashPassword("old-user-password", 4), IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.Users.Add(new User { Email = "reused@example.test", DisplayName = "New", PasswordHash = BCrypt.Net.BCrypt.HashPassword("new-user-password", 4), IsActive = true });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("reused@example.test", "new-user-password"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static readonly HashSet<string> RejectedPasswordCases = ["leading-space", "trailing-space", "tab"];

    public static TheoryData<string, string> AwkwardPasswords => new()
    {
        { "bang", "pass!word1" },
        { "dollar", "pa$$word$1" },
        { "ampersand", "pass&word&1" },
        { "plus", "pass+word+1" },
        { "percent", "pass%20word%1" },
        { "equals", "pass=word=1" },
        { "hash", "pass#word#1" },
        { "question", "pass?word?1" },
        { "backslash", "pass\\word\\1" },
        { "double-quote", "pass\"word\"1" },
        { "single-quote", "pass'word'1" },
        { "angle-brackets", "<pass>word<1>" },
        { "leading-space", "  leading-space-pw" },
        { "trailing-space", "trailing-space-pw  " },
        { "internal-spaces", "internal  space pw" },
        { "tab", "tab\tseparated\tpw" },
        { "emoji-non-bmp", "pw-\U0001F600\U0001F680-end" },
        { "nfc-precomposed", "caf\u00e9-r\u00e9sum\u00e9-pw" },
        { "nfd-decomposed", "cafe\u0301-re\u0301sume\u0301-pw" },
        { "mixed", "p@$$ w0rd!&+=%\\\"'<>" },
        { "over-72-bytes", new string('a', 70) + "-tail-differs-XYZ" },
    };

    [Theory]
    [MemberData(nameof(AwkwardPasswords))]
    public async Task ChangePassword_ThenLogin_RoundTripsExactPassword(string name, string newPassword)
    {
        await using var factory = CreateFactory(new CapturingLoggerProvider());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), ct);
        login.EnsureSuccessStatusCode();
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>(ct))!;
        using var change = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest(AdminPassword, newPassword))
        };
        change.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.Token);
        var changeResponse = await client.SendAsync(change, ct);

        if (RejectedPasswordCases.Contains(name))
        {
            Assert.Equal(HttpStatusCode.BadRequest, changeResponse.StatusCode);
            using var problem = System.Text.Json.JsonDocument.Parse(await changeResponse.Content.ReadAsStringAsync(ct));
            Assert.Equal(Cmsify.Core.Validation.PasswordRules.ValidationMessage, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal("https://cmsify.dev/errors/validation-failed", problem.RootElement.GetProperty("type").GetString());
            // Hash unchanged: the old password still works and the rejected one does not.
            Assert.True((await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), ct)).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, newPassword), ct)).StatusCode);
            return;
        }

        changeResponse.EnsureSuccessStatusCode();

        var relogin = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, newPassword), ct);
        Assert.True(relogin.IsSuccessStatusCode, $"[{name}] exact password must log in after change-password, got {(int)relogin.StatusCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), ct)).StatusCode);

        // Nothing may normalize on the way in.
        var normalized = newPassword.IsNormalized(System.Text.NormalizationForm.FormC)
            ? newPassword.Normalize(System.Text.NormalizationForm.FormD)
            : newPassword.Normalize(System.Text.NormalizationForm.FormC);
        if (normalized != newPassword)
        {
            var other = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, normalized), ct);
            TestContext.Current.SendDiagnosticMessage($"OBSERVATION [{name}]: login with the other Unicode normalization form returned {(int)other.StatusCode}");
            Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        }

        if (System.Text.Encoding.UTF8.GetByteCount(newPassword) > 72)
        {
            var truncatedVariant = newPassword[..72] + "-completely-different-suffix";
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, truncatedVariant), ct);
            TestContext.Current.SendDiagnosticMessage($"OBSERVATION [{name}]: different password sharing first 72 bytes returned {(int)response.StatusCode} (200 = BCrypt truncation)");
        }
    }

    [Fact]
    public void BCrypt_PasswordsLongerThan72Bytes_AreTruncated_DocumentedBehavior()
    {
        var first = new string('a', 72) + "-first-suffix";
        var second = new string('a', 72) + "-second-suffix";
        var hash = BCrypt.Net.BCrypt.HashPassword(first, 4);

        // Documents (does not endorse) the library behavior: only the first 72 bytes are significant.
        Assert.True(BCrypt.Net.BCrypt.Verify(second, hash), "BCrypt.Net did NOT truncate at 72 bytes");
    }

    [Fact]
    public async Task Login_StillWorks_ForExistingPasswordThatViolatesSetRules()
    {
        await using var factory = CreateFactory(new CapturingLoggerProvider());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), ct)).EnsureSuccessStatusCode();
        const string legacyPassword = " legacy\u00A0padded password ";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            db.Users.Add(new User { Email = "legacy@example.test", DisplayName = "Legacy", PasswordHash = BCrypt.Net.BCrypt.HashPassword(legacyPassword, 4) });
            await db.SaveChangesAsync(ct);
        }

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("legacy@example.test", legacyPassword), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateUserAndResetPassword_RejectInvalidTemporaryPasswords_WithoutChangingHash()
    {
        await using var factory = CreateFactory(new CapturingLoggerProvider());
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), ct);
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Token);

        var create = await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("new@example.test", "New", UserRole.Editor, "bad password with trailing space ", false, null, null), ct);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("new@example.test", "New", UserRole.Editor, "valid temporary pass", false, null, null), ct);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var userId = (await ok.Content.ReadFromJsonAsync<TempPasswordResponse>(ct))!.UserId;

        var reset = await client.PostAsJsonAsync($"/api/v1/users/{userId}/reset-password", new ResetPasswordRequest("zero\u200Bwidth temp password"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.True((await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("new@example.test", "valid temporary pass"), ct)).IsSuccessStatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(CapturingLoggerProvider logs)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogger<Cmsify.Api.Controllers.AuthController>>(new CapturingLogger<Cmsify.Api.Controllers.AuthController>(logs))));

    private sealed record LogEntry(LogLevel Level, string Category, string Message);

    private sealed class CapturingLoggerProvider
    {
        public ConcurrentQueue<LogEntry> Queue { get; } = new();

        public IReadOnlyCollection<LogEntry> Entries => Queue.ToArray();
    }

    private sealed class CapturingLogger<T>(CapturingLoggerProvider provider) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => provider.Queue.Enqueue(new LogEntry(logLevel, typeof(T).Name, formatter(state, exception)));
    }
}

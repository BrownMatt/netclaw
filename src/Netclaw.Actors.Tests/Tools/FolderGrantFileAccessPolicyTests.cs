// -----------------------------------------------------------------------
// <copyright file="FolderGrantFileAccessPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Grant enforcement tests for <see cref="ScopedFileAccessPolicy"/>: a
/// granted root authorizes absolute paths for first-party file tools,
/// default-deny holds outside every grant, Public audience keeps its
/// session-directory-only guarantee, symlink escapes fail the grant, and
/// the hard-deny surface stays authoritative inside a granted root.
/// </summary>
public sealed class FolderGrantFileAccessPolicyTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly string _grantDir;
    private readonly string _siblingDir;
    private readonly NetclawPaths _paths;

    public FolderGrantFileAccessPolicyTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "sessions", "grant-session");
        _grantDir = Path.Combine(_dir.Path, "granted", "alpha");
        _siblingDir = Path.Combine(_dir.Path, "granted", "beta");
        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(_grantDir);
        Directory.CreateDirectory(_siblingDir);
        _paths = new NetclawPaths(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private static ToolConfig RootsOnlyConfig()
    {
        // Scoped modes with no configured roots: without a grant, every
        // absolute path outside the session directory is denied, so any
        // authorization observed in these tests comes from the grant list.
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots
        };
        toolConfig.AudienceProfiles.Personal.WriteFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots
        };
        return toolConfig;
    }

    private ToolInvocationContext CreateContext(
        TrustAudience audience,
        IReadOnlyList<string> grantedFolders,
        bool interactiveApproval = false)
        => TestToolExecutionContext.CreateBound("signalr/grant-session", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            ChannelType = "signalr",
            GrantedFolders = grantedFolders,
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(interactiveApproval)
        }).Invocation;

    [Fact]
    public void Write_under_granted_root_is_allowed()
    {
        var policy = new ScopedFileAccessPolicy(RootsOnlyConfig(), _paths);
        var context = CreateContext(TrustAudience.Personal, [_grantDir]);
        var target = Path.Combine(_grantDir, "notes", "new.md");

        var allowed = policy.TryResolveWritePath(target, context, out _, out var error);

        Assert.True(allowed, error);
    }

    [Fact]
    public void Read_under_granted_root_is_allowed_without_interactive_reach()
    {
        var policy = new ScopedFileAccessPolicy(RootsOnlyConfig(), _paths);
        var context = CreateContext(TrustAudience.Personal, [_grantDir]);
        var target = Path.Combine(_grantDir, "src", "App.cs");

        var allowed = policy.TryResolveReadPath(target, context, out _, out var error);

        Assert.True(allowed, error);
    }

    [Fact]
    public void Sibling_path_outside_grant_stays_denied()
    {
        var policy = new ScopedFileAccessPolicy(RootsOnlyConfig(), _paths);
        var context = CreateContext(TrustAudience.Personal, [_grantDir]);
        var target = Path.Combine(_siblingDir, "secrets.txt");

        Assert.False(policy.TryResolveWritePath(target, context, out _, out _));
        Assert.False(policy.TryResolveReadPath(target, context, out _, out _));
    }

    [Fact]
    public void Without_grant_the_same_path_is_denied()
    {
        var policy = new ScopedFileAccessPolicy(RootsOnlyConfig(), _paths);
        var context = CreateContext(TrustAudience.Personal, []);
        var target = Path.Combine(_grantDir, "notes", "new.md");

        Assert.False(policy.TryResolveWritePath(target, context, out _, out _));
    }

    [Fact]
    public void Public_audience_ignores_grants()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = CreateContext(TrustAudience.Public, [_grantDir]);
        var target = Path.Combine(_grantDir, "file.txt");

        Assert.False(policy.TryResolveReadPath(target, context, out _, out _));
        Assert.False(policy.TryResolveWritePath(target, context, out _, out _));
    }

    [Fact]
    public void Autonomous_session_honors_grants_inside_its_zone()
    {
        // Default Personal profile is Mode.All; a non-interactive session is
        // clamped to the autonomous zone, which must include granted roots.
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = CreateContext(TrustAudience.Personal, [_grantDir], interactiveApproval: false);
        var inside = Path.Combine(_grantDir, "out.md");
        var outside = Path.Combine(_siblingDir, "out.md");

        Assert.True(policy.TryResolveWritePath(inside, context, out _, out var error), error);
        Assert.False(policy.TryResolveWritePath(outside, context, out _, out _));
    }

    [Fact]
    public void Symlink_escape_from_granted_root_is_denied()
    {
        var outsideTarget = Path.Combine(_dir.Path, "outside-target");
        Directory.CreateDirectory(outsideTarget);
        var link = Path.Combine(_grantDir, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outsideTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links are not available in this environment.");
            return;
        }

        var policy = new ScopedFileAccessPolicy(RootsOnlyConfig(), _paths);
        var context = CreateContext(TrustAudience.Personal, [_grantDir]);
        var escaping = Path.Combine(link, "passwd");

        var allowed = policy.TryResolveReadPath(escaping, context, out _, out var error);

        Assert.False(allowed);
        Assert.Contains("symlink", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hard_denied_file_inside_granted_root_stays_denied()
    {
        // Precedence proof at the tool level: the grant authorizes the root,
        // and the hard-deny list still blocks the file inside it.
        var secretDir = Path.Combine(_grantDir, ".ssh");
        Directory.CreateDirectory(secretDir);
        var secretFile = Path.Combine(secretDir, "id_ed25519");
        await File.WriteAllTextAsync(secretFile, "PRIVATE KEY", TestContext.Current.CancellationToken);

        var tool = new FileReadTool(RootsOnlyConfig(), _paths, new ToolPathPolicy([secretDir]));
        var context = TestToolExecutionContext.CreateBound("signalr/grant-session", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr",
            GrantedFolders = [_grantDir],
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false)
        });

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", secretFile),
            context,
            CancellationToken.None);

        Assert.StartsWith("Error", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE KEY", result, StringComparison.Ordinal);

        // The grant itself still works for a non-denied file in the same root.
        var okFile = Path.Combine(_grantDir, "readme.md");
        await File.WriteAllTextAsync(okFile, "fine", TestContext.Current.CancellationToken);
        var okResult = await tool.ExecuteAsync(
            ToolInput.Create("Path", okFile),
            context,
            CancellationToken.None);
        Assert.Equal("fine", okResult);
    }
}

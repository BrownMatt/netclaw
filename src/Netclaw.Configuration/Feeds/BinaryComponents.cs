// -----------------------------------------------------------------------
// <copyright file="BinaryComponents.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration.Feeds;

/// <summary>
/// The closed set of release component names. A component name is also the
/// binary file name (plus <c>.exe</c> on Windows), the archive name prefix,
/// and the <see cref="BinaryAsset.Component"/> value in the release manifest.
/// The publish script, the manifest generator, and the install scripts carry
/// the same names; change them together.
/// </summary>
public static class BinaryComponents
{
    public const string Cli = "netclaw";
    public const string Daemon = "netclawd";
    public const string Gui = "netclaw-gui";

    /// <summary>
    /// Components every install carries. <c>netclaw update</c> always installs
    /// these for the host platform.
    /// </summary>
    public static IReadOnlyList<string> Core { get; } = [Cli, Daemon];

    /// <summary>
    /// True for a component an operator opts into. <c>netclaw update</c>
    /// installs an optional component only when it is already present, so an
    /// update never widens the install footprint on its own.
    /// </summary>
    public static bool IsOptional(string component)
        => string.Equals(component, Gui, StringComparison.Ordinal);
}

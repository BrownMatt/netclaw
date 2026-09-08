// -----------------------------------------------------------------------
// <copyright file="ISessionLifecycleObserver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Observer for session activation, deactivation, and output events.
/// Implemented by infrastructure services (e.g. session catalog) that need to
/// track all sessions regardless of channel type.
/// </summary>
public interface ISessionLifecycleObserver
{
    /// <summary>
    /// Called when a session becomes active through pipeline materialization.
    /// </summary>
    void OnSessionActivated(SessionId sessionId, ChannelType channelType);

    /// <summary>
    /// Called for every <see cref="SessionOutput"/> emitted by the session,
    /// whether or not a client is attached. Runs synchronously on the session
    /// actor's thread at the emit point, exactly once per output.
    /// Implementations must be fast and must not throw.
    /// </summary>
    void OnOutput(SessionOutput output);

    /// <summary>
    /// Called when the owning session actor deactivates and is about to stop.
    /// </summary>
    void OnSessionDeactivated(SessionId sessionId);
}

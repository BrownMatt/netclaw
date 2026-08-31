// -----------------------------------------------------------------------
// <copyright file="SessionConnectionMap.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// The session a connection left when it bound elsewhere or disconnected.
/// <see cref="SessionNowEmpty"/> tells the caller whether that session lost
/// its last connection — the signal that gates session shutdown.
/// </summary>
internal readonly record struct SessionDetachment(SessionId SessionId, bool SessionNowEmpty);

/// <summary>
/// Thread-safe mapping between SignalR connection IDs and session IDs.
/// A connection is attached to at most one session; a session accepts many
/// concurrent connections (e.g. the GUI plus a CLI client). A session ends
/// only when its last connection detaches.
/// </summary>
internal sealed class SessionConnectionMap
{
    private readonly object _gate = new();
    private readonly Dictionary<SessionId, HashSet<SignalRConnectionId>> _sessionToConnections = [];
    private readonly Dictionary<SignalRConnectionId, SessionId> _connectionToSession = [];

    /// <summary>
    /// Binds a newly created session to a connection. Returns the detachment
    /// for the session this connection previously occupied, if any.
    /// </summary>
    public SessionDetachment? BindNewSession(SessionId sessionId, SignalRConnectionId connectionId)
    {
        lock (_gate)
        {
            var detachment = DetachConnectionInternal(connectionId);
            AddInternal(sessionId, connectionId);
            return detachment;
        }
    }

    /// <summary>
    /// Attaches an existing session to a connection. Other connections already
    /// on that session stay attached. Returns the detachment for the session
    /// this connection previously occupied, if any.
    /// </summary>
    public SessionDetachment? AttachSession(SessionId sessionId, SignalRConnectionId connectionId)
    {
        lock (_gate)
        {
            var detachment = DetachConnectionInternal(connectionId);
            AddInternal(sessionId, connectionId);
            return detachment;
        }
    }

    public bool IsAttached(SignalRConnectionId connectionId, SessionId sessionId)
    {
        lock (_gate)
        {
            return _connectionToSession.TryGetValue(connectionId, out var attachedSessionId)
                && attachedSessionId.Equals(sessionId);
        }
    }

    public int GetConnectionCount(SessionId sessionId)
    {
        lock (_gate)
        {
            return _sessionToConnections.TryGetValue(sessionId, out var connections)
                ? connections.Count
                : 0;
        }
    }

    public bool TryGetSessionForConnection(SignalRConnectionId connectionId, out SessionId sessionId)
    {
        lock (_gate)
        {
            if (_connectionToSession.TryGetValue(connectionId, out var foundSessionId))
            {
                sessionId = foundSessionId;
                return true;
            }

            sessionId = default;
            return false;
        }
    }

    /// <summary>
    /// Removes a disconnected connection. Returns the detachment for the
    /// session it was attached to, if any.
    /// </summary>
    public SessionDetachment? Disconnect(SignalRConnectionId connectionId)
    {
        lock (_gate)
            return DetachConnectionInternal(connectionId);
    }

    /// <summary>
    /// Detaches every connection from a session (delete teardown). Returns
    /// the connections that were attached so the caller can notify them.
    /// </summary>
    public IReadOnlyList<SignalRConnectionId> RemoveSession(SessionId sessionId)
    {
        lock (_gate)
        {
            if (!_sessionToConnections.Remove(sessionId, out var connections))
                return [];

            foreach (var connection in connections)
                _connectionToSession.Remove(connection);

            return [.. connections];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessionToConnections.Clear();
            _connectionToSession.Clear();
        }
    }

    private void AddInternal(SessionId sessionId, SignalRConnectionId connectionId)
    {
        if (!_sessionToConnections.TryGetValue(sessionId, out var connections))
        {
            connections = [];
            _sessionToConnections[sessionId] = connections;
        }

        connections.Add(connectionId);
        _connectionToSession[connectionId] = sessionId;
    }

    private SessionDetachment? DetachConnectionInternal(SignalRConnectionId connectionId)
    {
        if (!_connectionToSession.Remove(connectionId, out var sessionId))
            return null;

        var nowEmpty = false;
        if (_sessionToConnections.TryGetValue(sessionId, out var connections))
        {
            connections.Remove(connectionId);
            if (connections.Count == 0)
            {
                _sessionToConnections.Remove(sessionId);
                nowEmpty = true;
            }
        }

        return new SessionDetachment(sessionId, nowEmpty);
    }
}

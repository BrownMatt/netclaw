// -----------------------------------------------------------------------
// <copyright file="SessionConnectionMapTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SessionConnectionMapTests
{
    [Fact]
    public void BindNewSession_adds_bidirectional_mapping()
    {
        var map = new SessionConnectionMap();
        var sessionId = new SessionId("signalr/session-1");
        var connectionId = SignalRConnectionId.Create("connection-1");

        var detachment = map.BindNewSession(sessionId, connectionId);

        Assert.Null(detachment);
        Assert.True(map.IsAttached(connectionId, sessionId));
        Assert.Equal(1, map.GetConnectionCount(sessionId));
        Assert.True(map.TryGetSessionForConnection(connectionId, out var mappedSession));
        Assert.Equal(sessionId, mappedSession);
    }

    [Fact]
    public void BindNewSession_reports_the_orphaned_previous_session()
    {
        var map = new SessionConnectionMap();
        var firstSession = new SessionId("signalr/session-1");
        var secondSession = new SessionId("signalr/session-2");
        var connectionId = SignalRConnectionId.Create("connection-1");

        map.BindNewSession(firstSession, connectionId);
        var detachment = map.BindNewSession(secondSession, connectionId);

        Assert.NotNull(detachment);
        Assert.Equal(firstSession, detachment.Value.SessionId);
        Assert.True(detachment.Value.SessionNowEmpty);
        Assert.True(map.IsAttached(connectionId, secondSession));
        Assert.Equal(0, map.GetConnectionCount(firstSession));
    }

    [Fact]
    public void BindNewSession_previous_session_stays_alive_with_other_connections()
    {
        var map = new SessionConnectionMap();
        var sharedSession = new SessionId("signalr/session-1");
        var newSession = new SessionId("signalr/session-2");
        var firstConnection = SignalRConnectionId.Create("connection-1");
        var secondConnection = SignalRConnectionId.Create("connection-2");

        map.BindNewSession(sharedSession, firstConnection);
        map.AttachSession(sharedSession, secondConnection);
        var detachment = map.BindNewSession(newSession, secondConnection);

        Assert.NotNull(detachment);
        Assert.Equal(sharedSession, detachment.Value.SessionId);
        Assert.False(detachment.Value.SessionNowEmpty);
        Assert.True(map.IsAttached(firstConnection, sharedSession));
        Assert.Equal(1, map.GetConnectionCount(sharedSession));
    }

    [Fact]
    public void AttachSession_keeps_other_connections_attached()
    {
        var map = new SessionConnectionMap();
        var sessionId = new SessionId("signalr/session-1");
        var firstConnection = SignalRConnectionId.Create("connection-1");
        var secondConnection = SignalRConnectionId.Create("connection-2");

        map.BindNewSession(sessionId, firstConnection);
        var detachment = map.AttachSession(sessionId, secondConnection);

        Assert.Null(detachment);
        Assert.True(map.IsAttached(firstConnection, sessionId));
        Assert.True(map.IsAttached(secondConnection, sessionId));
        Assert.Equal(2, map.GetConnectionCount(sessionId));
    }

    [Fact]
    public void Disconnect_of_one_connection_keeps_the_session_for_the_other()
    {
        var map = new SessionConnectionMap();
        var sessionId = new SessionId("signalr/session-1");
        var firstConnection = SignalRConnectionId.Create("connection-1");
        var secondConnection = SignalRConnectionId.Create("connection-2");

        map.BindNewSession(sessionId, firstConnection);
        map.AttachSession(sessionId, secondConnection);
        var detachment = map.Disconnect(secondConnection);

        Assert.NotNull(detachment);
        Assert.Equal(sessionId, detachment.Value.SessionId);
        Assert.False(detachment.Value.SessionNowEmpty);
        Assert.True(map.IsAttached(firstConnection, sessionId));
        Assert.False(map.IsAttached(secondConnection, sessionId));
    }

    [Fact]
    public void Disconnect_of_last_connection_empties_the_session()
    {
        var map = new SessionConnectionMap();
        var sessionId = new SessionId("signalr/session-1");
        var connectionId = SignalRConnectionId.Create("connection-1");

        map.BindNewSession(sessionId, connectionId);
        var detachment = map.Disconnect(connectionId);

        Assert.NotNull(detachment);
        Assert.Equal(sessionId, detachment.Value.SessionId);
        Assert.True(detachment.Value.SessionNowEmpty);
        Assert.Equal(0, map.GetConnectionCount(sessionId));
        Assert.False(map.TryGetSessionForConnection(connectionId, out _));
    }

    [Fact]
    public void Disconnect_of_unknown_connection_returns_no_detachment()
    {
        var map = new SessionConnectionMap();

        var detachment = map.Disconnect(SignalRConnectionId.Create("connection-1"));

        Assert.Null(detachment);
    }
}

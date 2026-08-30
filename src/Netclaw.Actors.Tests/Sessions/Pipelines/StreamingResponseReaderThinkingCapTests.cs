// -----------------------------------------------------------------------
// <copyright file="StreamingResponseReaderThinkingCapTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions.Pipelines;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

/// <summary>
/// Fake-stream coverage for the per-response thinking cap in
/// <see cref="StreamingResponseReader"/> (turn-loop-governance): the breach
/// stops consumption, everything read so far is preserved, a disabled cap
/// changes nothing, and text content never counts toward the cap.
/// </summary>
public sealed class StreamingResponseReaderThinkingCapTests
{
    private static ChatResponseUpdate Thinking(string text)
        => new() { Role = ChatRole.Assistant, Contents = [new TextReasoningContent(text)] };

    private static ChatResponseUpdate Text(string text)
        => new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    [Fact]
    public async Task Breach_stops_consumption_and_flags_result()
    {
        var chunk = new string('t', 100);
        var client = new ScriptedStreamClient([
            Thinking(chunk), Thinking(chunk), Thinking(chunk), Thinking(chunk), Thinking(chunk)
        ]);

        var result = await StreamingResponseReader.ReadAsync(
            client, [new AiChatMessage(ChatRole.User, "go")], options: null,
            (_, _, _) => { }, thinkingCapChars: 300, TestContext.Current.CancellationToken);

        Assert.True(result.ThinkingCapBreached);
        // Breach fires when the accumulated count reaches the cap: 3 chunks in,
        // the remaining 2 are never consumed.
        Assert.Equal(3, client.YieldedCount);
        Assert.Equal(300, result.Diagnostics.ThinkingChars);
    }

    [Fact]
    public async Task Breach_preserves_all_content_read_so_far()
    {
        var client = new ScriptedStreamClient([
            Text("prefix"), Thinking(new string('a', 200)), Thinking(new string('b', 200))
        ]);

        var result = await StreamingResponseReader.ReadAsync(
            client, [new AiChatMessage(ChatRole.User, "go")], options: null,
            (_, _, _) => { }, thinkingCapChars: 300, TestContext.Current.CancellationToken);

        Assert.True(result.ThinkingCapBreached);
        var thinkingChars = result.Response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextReasoningContent>()
            .Sum(c => c.Text?.Length ?? 0);
        Assert.Equal(400, thinkingChars);
        Assert.Contains("prefix", result.Response.Text);
    }

    [Fact]
    public async Task Disabled_cap_consumes_everything()
    {
        var chunk = new string('t', 100);
        var client = new ScriptedStreamClient([
            Thinking(chunk), Thinking(chunk), Thinking(chunk), Thinking(chunk), Text("done")
        ]);

        var result = await StreamingResponseReader.ReadAsync(
            client, [new AiChatMessage(ChatRole.User, "go")], options: null,
            (_, _, _) => { }, thinkingCapChars: 0, TestContext.Current.CancellationToken);

        Assert.False(result.ThinkingCapBreached);
        Assert.Equal(5, client.YieldedCount);
        Assert.Equal(400, result.Diagnostics.ThinkingChars);
        Assert.Equal("done", result.Response.Text);
    }

    [Fact]
    public async Task Text_content_never_counts_toward_the_cap()
    {
        var client = new ScriptedStreamClient([
            Text(new string('x', 1000)), Text(new string('y', 1000)), Text("end")
        ]);

        var result = await StreamingResponseReader.ReadAsync(
            client, [new AiChatMessage(ChatRole.User, "go")], options: null,
            (_, _, _) => { }, thinkingCapChars: 300, TestContext.Current.CancellationToken);

        Assert.False(result.ThinkingCapBreached);
        Assert.Equal(3, client.YieldedCount);
        Assert.Equal(2003, result.Diagnostics.TextChars);
    }

    [Fact]
    public async Task Breach_split_across_delta_boundaries_counts_accumulated_chars()
    {
        // 299 + 2: the second delta crosses the cap mid-delta; the whole delta
        // is retained (the reader caps between updates, not inside one).
        var client = new ScriptedStreamClient([
            Thinking(new string('a', 299)), Thinking("bb"), Thinking("never-read")
        ]);

        var result = await StreamingResponseReader.ReadAsync(
            client, [new AiChatMessage(ChatRole.User, "go")], options: null,
            (_, _, _) => { }, thinkingCapChars: 300, TestContext.Current.CancellationToken);

        Assert.True(result.ThinkingCapBreached);
        Assert.Equal(2, client.YieldedCount);
        Assert.Equal(301, result.Diagnostics.ThinkingChars);
    }

    private sealed class ScriptedStreamClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public int YieldedCount { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                YieldedCount++;
                yield return update;
                await Task.Yield();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming only.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

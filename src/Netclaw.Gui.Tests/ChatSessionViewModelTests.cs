// -----------------------------------------------------------------------
// <copyright file="ChatSessionViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Gui.ViewModels;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Gui.Tests;

/// <summary>
/// Chat pane state machine tests: output events → history blocks, delta
/// streaming with snapshot replacement, collapsed sections, approval
/// resolve-once, activity transitions, and the usage line.
/// </summary>
public sealed class ChatSessionViewModelTests
{
    private static readonly SessionId Session = new("signalr/chat-test");

    private readonly List<(string CallId, string Key)> _responses = [];

    private ChatSessionViewModel CreateViewModel()
        => new(
            Session.Value,
            (callId, key) =>
            {
                _responses.Add((callId, key));
                return Task.CompletedTask;
            },
            new ModelSelectorViewModel((_, _) => Task.CompletedTask));

    private static TextDeltaOutput Delta(string text) => new(text) { SessionId = Session };

    private static TextOutput Text(string text) => new(text) { SessionId = Session };

    private static ToolCallOutput ToolCall(string callId, string tool) => new()
    {
        SessionId = Session,
        CallId = new ToolCallId(callId),
        ToolName = new ToolName(tool),
        ArgumentsJson = """{"path":"a.txt"}"""
    };

    private static ToolResultOutput ToolResult(string callId, string tool, string result) => new()
    {
        SessionId = Session,
        CallId = new ToolCallId(callId),
        ToolName = new ToolName(tool),
        Result = result
    };

    private static ToolInteractionRequest Interaction(string callId) => new()
    {
        SessionId = Session,
        Kind = "approval",
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("shell_execute"),
        DisplayText = "Run `rm -rf ./tmp`?",
        Options =
        [
            new ToolInteractionOption(new ApprovalOptionKey("approve_once"), "Once"),
            new ToolInteractionOption(new ApprovalOptionKey("deny"), "Deny")
        ]
    };

    [Fact]
    public void Each_output_kind_produces_the_matching_block()
    {
        var vm = CreateViewModel();

        vm.OnMessageSent("hello");
        vm.HandleOutput(new ThinkingDeltaOutput("thinking...") { SessionId = Session });
        vm.HandleOutput(ToolCall("c1", "file_read"));
        vm.HandleOutput(ToolResult("c1", "file_read", "contents"));
        vm.HandleOutput(Interaction("c2"));
        vm.HandleOutput(Text("done"));
        vm.HandleOutput(new ErrorOutput { SessionId = Session, Message = "boom" });

        Assert.Collection(vm.Blocks,
            b => Assert.IsType<UserMessageBlockViewModel>(b),
            b => Assert.IsType<ThinkingBlockViewModel>(b),
            b => Assert.IsType<ToolCallBlockViewModel>(b),
            b => Assert.IsType<ApprovalCardViewModel>(b),
            b => Assert.IsType<AssistantMessageBlockViewModel>(b),
            b => Assert.IsType<ErrorBlockViewModel>(b));
    }

    [Fact]
    public void Deltas_render_live_after_flush()
    {
        var vm = CreateViewModel();

        vm.HandleOutput(Delta("Hel"));
        vm.HandleOutput(Delta("lo "));
        vm.HandleOutput(Delta("world"));
        vm.FlushStreamingDeltas();

        var block = Assert.IsType<AssistantMessageBlockViewModel>(Assert.Single(vm.Blocks));
        Assert.Equal("Hello world", block.Content);
        Assert.True(block.IsStreaming);
    }

    [Fact]
    public void Final_snapshot_replaces_accumulated_deltas_without_duplication()
    {
        var vm = CreateViewModel();

        vm.HandleOutput(Delta("Hel"));
        vm.FlushStreamingDeltas();
        vm.HandleOutput(Delta("lo (draft that differs)"));
        vm.HandleOutput(Text("Hello world"));
        vm.FlushStreamingDeltas();

        var block = Assert.IsType<AssistantMessageBlockViewModel>(Assert.Single(vm.Blocks));
        Assert.Equal("Hello world", block.Content);
        Assert.False(block.IsStreaming);
    }

    [Fact]
    public void Thinking_and_tool_sections_start_collapsed_and_share_one_section()
    {
        var vm = CreateViewModel();

        vm.HandleOutput(new ThinkingOutput("hidden reasoning") { SessionId = Session });
        vm.HandleOutput(ToolCall("c1", "file_read"));
        vm.HandleOutput(ToolResult("c1", "file_read", "the result"));

        var thinking = Assert.IsType<ThinkingBlockViewModel>(vm.Blocks[0]);
        Assert.False(thinking.IsExpanded);
        Assert.Equal("hidden reasoning", thinking.Content);

        // One block carries the call AND its result.
        var tool = Assert.IsType<ToolCallBlockViewModel>(vm.Blocks[1]);
        Assert.False(tool.IsExpanded);
        Assert.Equal("Tool: file_read", tool.Header);
        Assert.Contains("a.txt", tool.ArgumentsJson);
        Assert.Equal("the result", tool.Result);
        Assert.Equal(2, vm.Blocks.Count);
    }

    [Fact]
    public async Task Approval_card_resolves_once_and_rejects_a_second_response()
    {
        var vm = CreateViewModel();
        vm.HandleOutput(Interaction("c2"));

        var card = Assert.IsType<ApprovalCardViewModel>(Assert.Single(vm.Blocks));
        Assert.True(card.CanRespond);

        await card.RespondCommand.ExecuteAsync(card.Options[0]);

        Assert.True(card.IsResolved);
        Assert.False(card.CanRespond);
        Assert.Equal("Once", card.ResolvedLabel);
        Assert.Equal(("c2", "approve_once"), Assert.Single(_responses));

        // A second response is a no-op.
        await card.RespondCommand.ExecuteAsync(card.Options[1]);
        Assert.Single(_responses);
        Assert.Equal("Once", card.ResolvedLabel);
    }

    [Fact]
    public void Activity_transitions_follow_the_event_stream()
    {
        var vm = CreateViewModel();
        Assert.Equal(ActivityState.Idle, vm.Activity);

        vm.OnMessageSent("go");
        Assert.Equal(ActivityState.Waiting, vm.Activity);

        vm.HandleOutput(Delta("Hi"));
        Assert.Equal(ActivityState.Responding, vm.Activity);

        vm.HandleOutput(ToolCall("c1", "file_read"));
        Assert.Equal(ActivityState.RunningTool, vm.Activity);
        Assert.Equal("Running tool: file_read", vm.ActivityText);

        vm.HandleOutput(ToolResult("c1", "file_read", "ok"));
        Assert.Equal(ActivityState.Waiting, vm.Activity);

        vm.HandleOutput(Interaction("c2"));
        Assert.Equal(ActivityState.ApprovalRequired, vm.Activity);

        vm.HandleOutput(new TurnCompleted { SessionId = Session, TurnNumber = new TurnNumber(1) });
        Assert.Equal(ActivityState.Idle, vm.Activity);
    }

    [Fact]
    public void Usage_line_uses_the_spec_format()
    {
        var vm = CreateViewModel();
        Assert.Equal(string.Empty, vm.UsageText);

        vm.HandleOutput(new UsageOutput
        {
            SessionId = Session,
            InputTokens = 1200,
            OutputTokens = 300,
            ContextWindowTokens = 262144
        });

        Assert.Equal("in=1200 out=300 (0.5% ctx)", vm.UsageText);
    }

    [Fact]
    public void Usage_line_stays_blank_when_no_values_arrive()
    {
        var vm = CreateViewModel();

        vm.HandleOutput(new UsageOutput { SessionId = Session });

        Assert.Equal(string.Empty, vm.UsageText);
        Assert.DoesNotContain("0", vm.UsageText);
    }

    [Fact]
    public void Replay_renders_recent_messages_as_blocks()
    {
        var vm = CreateViewModel();

        vm.LoadReplay(new SessionJoined
        {
            SessionId = Session,
            TurnCount = 2,
            RecentMessages =
            [
                new ChatMessageDto("user", "first question"),
                new ChatMessageDto("assistant", "first answer"),
                new ChatMessageDto("user", "second question")
            ]
        });

        Assert.Collection(vm.Blocks,
            b => Assert.Equal("first question", Assert.IsType<UserMessageBlockViewModel>(b).Text),
            b => Assert.Equal("first answer", Assert.IsType<AssistantMessageBlockViewModel>(b).Content),
            b => Assert.Equal("second question", Assert.IsType<UserMessageBlockViewModel>(b).Text));
    }

    [Fact]
    public void Replay_hydrates_grant_chips_from_the_join_snapshot()
    {
        var vm = CreateViewModel();

        vm.LoadReplay(new SessionJoined
        {
            SessionId = Session,
            GrantedFolders = ["/home/user/projects/alpha", "/home/user/projects/beta"]
        });

        Assert.Equal(
            ["/home/user/projects/alpha", "/home/user/projects/beta"],
            vm.GrantedFolders);
    }

    [Fact]
    public void Replay_does_not_replace_live_local_history()
    {
        var vm = CreateViewModel();
        vm.OnMessageSent("message the replay window does not carry yet");

        // A re-join lands after a send-failure recovery. The truncated replay
        // must not clear the just-sent user block; grants still hydrate.
        vm.LoadReplay(new SessionJoined
        {
            SessionId = Session,
            RecentMessages = [new ChatMessageDto("user", "old question")],
            GrantedFolders = ["/home/user/projects/alpha"]
        });

        var block = Assert.IsType<UserMessageBlockViewModel>(Assert.Single(vm.Blocks));
        Assert.Equal("message the replay window does not carry yet", block.Text);
        Assert.Equal(["/home/user/projects/alpha"], vm.GrantedFolders);
    }

    [Fact]
    public void Replay_marks_the_active_model_from_the_join_snapshot()
    {
        var vm = CreateViewModel();

        vm.LoadReplay(new SessionJoined
        {
            SessionId = Session,
            ModelOverrideProvider = "local-ollama",
            ModelOverrideId = "qwen3:30b"
        });

        Assert.Equal("qwen3:30b", vm.ModelSelector.ActiveLabel);
    }

    [Fact]
    public void Model_override_events_update_the_selector()
    {
        var vm = CreateViewModel();

        vm.HandleOutput(new ModelOverrideOutput
        {
            SessionId = Session,
            Provider = "local-ollama",
            ModelId = "qwen3:30b"
        });
        Assert.Equal("qwen3:30b", vm.ModelSelector.ActiveLabel);

        vm.HandleOutput(new ModelOverrideOutput { SessionId = Session });
        Assert.Equal(ModelSelectorViewModel.DefaultLabel, vm.ModelSelector.ActiveLabel);
    }

    [Fact]
    public void Folder_grant_events_reconcile_the_chip_list()
    {
        var vm = CreateViewModel();

        vm.HandleOutput(new FolderGrantOutput
        {
            SessionId = Session,
            Path = "/a",
            IsGranted = true,
            GrantedFolders = ["/a", "/b"]
        });
        Assert.Equal(["/a", "/b"], vm.GrantedFolders);

        vm.HandleOutput(new FolderGrantOutput
        {
            SessionId = Session,
            Path = "/a",
            IsGranted = false,
            GrantedFolders = ["/b"]
        });
        Assert.Equal(["/b"], vm.GrantedFolders);
    }
}

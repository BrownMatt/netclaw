// -----------------------------------------------------------------------
// <copyright file="ModelSelectorViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.SignalR;
using Netclaw.Client;
using Netclaw.Gui.ViewModels;
using Xunit;

namespace Netclaw.Gui.Tests;

/// <summary>
/// Model dropdown state machine: catalog states, tri-state warnings, and
/// ack-driven active marking (no optimistic flip, nack keeps the previous
/// selection and surfaces the reason).
/// </summary>
public sealed class ModelSelectorViewModelTests
{
    private static ModelCatalogResponseDto Catalog(params ModelCatalogProviderDto[] providers)
        => new([.. providers]);

    private static ModelCatalogProviderDto OkProvider(params ModelCatalogEntryDto[] models)
        => new("local-ollama", "ollama", Ok: true, Error: null, [.. models]);

    [Fact]
    public void Catalog_load_builds_options_with_tri_state_warnings()
    {
        var vm = new ModelSelectorViewModel((_, _) => Task.CompletedTask);

        vm.LoadCatalog(Catalog(OkProvider(
            new ModelCatalogEntryDto("tools-yes", "supported", 32_768),
            new ModelCatalogEntryDto("tools-no", "unsupported", null),
            new ModelCatalogEntryDto("tools-unknown", "unknown", null))));

        Assert.Equal(4, vm.Options.Count); // default + 3 models
        Assert.True(vm.Options[0].IsDefault);
        Assert.True(vm.Options[0].IsActive);
        Assert.Null(Assert.Single(vm.Options, o => o.ModelId == "tools-yes").Warning);
        Assert.Equal("No tool support", Assert.Single(vm.Options, o => o.ModelId == "tools-no").Warning);
        Assert.Equal("Tool support unknown", Assert.Single(vm.Options, o => o.ModelId == "tools-unknown").Warning);
        Assert.Null(vm.CatalogError);
    }

    [Fact]
    public void Failed_provider_is_visible_and_does_not_hide_the_rest()
    {
        var vm = new ModelSelectorViewModel((_, _) => Task.CompletedTask);

        vm.LoadCatalog(Catalog(
            OkProvider(new ModelCatalogEntryDto("m1", "supported", null)),
            new ModelCatalogProviderDto("cloud", "openrouter", Ok: false, "connection refused", [])));

        Assert.Contains(vm.Options, o => o.ModelId == "m1");
        Assert.NotNull(vm.CatalogError);
        Assert.Contains("cloud", vm.CatalogError);
        Assert.Contains("connection refused", vm.CatalogError);
    }

    [Fact]
    public void Catalog_request_failure_shows_the_failure_state()
    {
        var vm = new ModelSelectorViewModel((_, _) => Task.CompletedTask);
        vm.BeginCatalogLoad();

        vm.SetCatalogError("Model catalog unavailable: daemon unreachable");

        Assert.False(vm.IsLoading);
        Assert.Contains("unavailable", vm.CatalogError);
        // The default option stays — never an empty list pretending no models exist.
        Assert.Single(vm.Options);
        Assert.True(vm.Options[0].IsDefault);
    }

    [Fact]
    public async Task Selection_marks_active_only_after_the_ack()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (string? Provider, string? ModelId)? applied = null;
        var vm = new ModelSelectorViewModel(async (provider, modelId) =>
        {
            await gate.Task;
            applied = (provider, modelId);
        });
        vm.LoadCatalog(Catalog(OkProvider(new ModelCatalogEntryDto("qwen3:30b", "supported", null))));
        var option = Assert.Single(vm.Options, o => o.ModelId == "qwen3:30b");

        var selection = vm.SelectCommand.ExecuteAsync(option);

        // Before the ack: no optimistic flip.
        Assert.False(option.IsActive);
        Assert.Equal(ModelSelectorViewModel.DefaultLabel, vm.ActiveLabel);

        gate.SetResult();
        await selection;

        Assert.True(option.IsActive);
        Assert.Equal("qwen3:30b", vm.ActiveLabel);
        Assert.Equal(("local-ollama", "qwen3:30b"), applied);
    }

    [Fact]
    public async Task Rejected_selection_keeps_the_previous_active_and_surfaces_the_reason()
    {
        var vm = new ModelSelectorViewModel((_, _) =>
            throw new HubException("Model 'qwen3:30b' is not available from provider 'local-ollama'."));
        vm.LoadCatalog(Catalog(OkProvider(new ModelCatalogEntryDto("qwen3:30b", "supported", null))));
        var option = Assert.Single(vm.Options, o => o.ModelId == "qwen3:30b");

        await vm.SelectCommand.ExecuteAsync(option);

        Assert.False(option.IsActive);
        Assert.True(vm.Options[0].IsActive); // default stays active
        Assert.Contains("not available", vm.SelectionError);
    }

    [Fact]
    public async Task Default_entry_clears_the_override()
    {
        (string? Provider, string? ModelId)? applied = null;
        var vm = new ModelSelectorViewModel((provider, modelId) =>
        {
            applied = (provider, modelId);
            return Task.CompletedTask;
        });
        vm.LoadCatalog(Catalog(OkProvider(new ModelCatalogEntryDto("qwen3:30b", "supported", null))));
        vm.ApplyOverride("local-ollama", "qwen3:30b");

        await vm.SelectCommand.ExecuteAsync(vm.Options[0]);

        Assert.Equal((null, null), applied);
        Assert.True(vm.Options[0].IsActive);
        Assert.Equal(ModelSelectorViewModel.DefaultLabel, vm.ActiveLabel);
    }

    [Fact]
    public async Task Selecting_the_already_active_option_is_a_no_op()
    {
        var calls = 0;
        var vm = new ModelSelectorViewModel((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await vm.SelectCommand.ExecuteAsync(vm.Options[0]);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Daemon_reported_override_survives_a_catalog_reload()
    {
        var vm = new ModelSelectorViewModel((_, _) => Task.CompletedTask);
        vm.ApplyOverride("local-ollama", "qwen3:30b");

        vm.LoadCatalog(Catalog(OkProvider(new ModelCatalogEntryDto("qwen3:30b", "supported", null))));

        Assert.True(Assert.Single(vm.Options, o => o.ModelId == "qwen3:30b").IsActive);
        Assert.Equal("qwen3:30b", vm.ActiveLabel);
    }
}

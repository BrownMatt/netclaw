// -----------------------------------------------------------------------
// <copyright file="ModelSelectorViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netclaw.Client;

namespace Netclaw.Gui.ViewModels;

/// <summary>
/// One dropdown entry. The default entry (null provider/model) clears the
/// override and returns the session to the configured main model.
/// </summary>
public sealed partial class ModelOptionViewModel : ObservableObject
{
    public ModelOptionViewModel(string? provider, string? modelId, string label, string? warning)
    {
        Provider = provider;
        ModelId = modelId;
        Label = label;
        Warning = warning;
    }

    public string? Provider { get; }

    public string? ModelId { get; }

    public string Label { get; }

    /// <summary>
    /// Tool-support warning text; null when the model supports tool calls.
    /// An unknown report is labeled unknown — never rendered as unsupported.
    /// </summary>
    public string? Warning { get; }

    public bool HasWarning => Warning is not null;

    public bool IsDefault => Provider is null;

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// The model dropdown for the active session: catalog entries per provider,
/// the active-model marking, tool-support warnings, and the switch action.
/// Active marking follows only daemon confirmations (the set/clear ack and
/// <c>model_override</c> events) — never an optimistic local flip, so the UI
/// cannot claim a model the daemon rejected.
/// </summary>
public sealed partial class ModelSelectorViewModel : ObservableObject
{
    public const string DefaultLabel = "Default (configured model)";

    private readonly Func<string?, string?, Task> _applySelection;

    private string? _activeProvider;
    private string? _activeModelId;

    /// <param name="applySelection">
    /// Sends the selection to the daemon: (provider, modelId) sets the
    /// override; (null, null) clears it. Must throw on rejection.
    /// </param>
    public ModelSelectorViewModel(Func<string?, string?, Task> applySelection)
    {
        _applySelection = applySelection;
        Options.Add(new ModelOptionViewModel(null, null, DefaultLabel, warning: null) { IsActive = true });
    }

    public ObservableCollection<ModelOptionViewModel> Options { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Visible catalog failure state. Set when the catalog request fails or
    /// when a provider's probe failed — the dropdown never renders an empty
    /// list as if no models exist.
    /// </summary>
    [ObservableProperty]
    private string? _catalogError;

    /// <summary>The daemon's rejection reason for the last failed switch.</summary>
    [ObservableProperty]
    private string? _selectionError;

    /// <summary>Dropdown button text: the active model, or the default label.</summary>
    [ObservableProperty]
    private string _activeLabel = DefaultLabel;

    public void BeginCatalogLoad()
    {
        IsLoading = true;
        CatalogError = null;
    }

    public void LoadCatalog(ModelCatalogResponseDto catalog)
    {
        IsLoading = false;

        var failed = new List<string>();
        var options = new List<ModelOptionViewModel>
        {
            new(null, null, DefaultLabel, warning: null)
        };

        foreach (var provider in catalog.Providers)
        {
            if (!provider.Ok)
            {
                failed.Add($"{provider.ProviderKey}: {provider.Error}");
                continue;
            }

            foreach (var model in provider.Models)
            {
                options.Add(new ModelOptionViewModel(
                    provider.ProviderKey,
                    model.Id,
                    model.Id,
                    WarningFor(model.ToolSupport)));
            }
        }

        Options.Clear();
        foreach (var option in options)
            Options.Add(option);

        CatalogError = failed.Count > 0
            ? $"Provider probe failed — {string.Join("; ", failed)}"
            : null;

        MarkActive();
    }

    public void SetCatalogError(string message)
    {
        IsLoading = false;
        CatalogError = message;
    }

    /// <summary>
    /// Applies the daemon-reported override state (from the join snapshot or
    /// a <c>model_override</c> event). Null values mean no override.
    /// </summary>
    public void ApplyOverride(string? provider, string? modelId)
    {
        _activeProvider = provider;
        _activeModelId = modelId;
        MarkActive();
    }

    [RelayCommand]
    private async Task SelectAsync(ModelOptionViewModel option)
    {
        if (option.IsActive)
            return;

        SelectionError = null;
        try
        {
            await _applySelection(option.Provider, option.ModelId);
        }
        catch (Exception ex)
        {
            // Rejection: keep the previous active marking and surface the
            // daemon's reason — routing did not change.
            SelectionError = ex.Message;
            return;
        }

        // The ack is the daemon's confirmation; the model_override event that
        // follows re-applies the same state idempotently.
        ApplyOverride(option.Provider, option.ModelId);
    }

    private void MarkActive()
    {
        foreach (var option in Options)
        {
            option.IsActive = option.IsDefault
                ? _activeModelId is null
                : string.Equals(option.Provider, _activeProvider, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(option.ModelId, _activeModelId, StringComparison.Ordinal);
        }

        ActiveLabel = _activeModelId ?? DefaultLabel;
    }

    private static string? WarningFor(string toolSupport) => toolSupport switch
    {
        "supported" => null,
        "unsupported" => "No tool support",
        _ => "Tool support unknown"
    };
}

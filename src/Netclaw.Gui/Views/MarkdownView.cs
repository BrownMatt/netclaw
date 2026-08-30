// -----------------------------------------------------------------------
// <copyright file="MarkdownView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Netclaw.Gui.Views;

/// <summary>
/// Read-only markdown renderer for assistant blocks: an AvaloniaEdit editor
/// with the TextMate markdown grammar. The grammar registry loads lazily
/// once and is shared across every block.
/// </summary>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly Lazy<RegistryOptions> Registry =
        new(() => new RegistryOptions(ThemeName.DarkPlus));

    private readonly TextEditor _editor;

    public MarkdownView()
    {
        _editor = new TextEditor
        {
            IsReadOnly = true,
            WordWrap = true,
            ShowLineNumbers = false,
            Background = Avalonia.Media.Brushes.Transparent,
            Foreground = Avalonia.Media.Brushes.Gainsboro,
            FontFamily = new Avalonia.Media.FontFamily("Segoe UI,Inter,sans-serif"),
            FontSize = 14,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _editor.Options.AllowScrollBelowDocument = false;

        try
        {
            var registry = Registry.Value;
            var installation = _editor.InstallTextMate(registry);
            installation.SetGrammar(registry.GetScopeByLanguageId("markdown"));
        }
        catch (Exception ex)
        {
            // Grammar assets failed to load: keep the text readable in plain
            // form and make the degradation visible to a developer run.
            System.Diagnostics.Debug.WriteLine($"TextMate markdown grammar unavailable: {ex}");
        }

        Content = _editor;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            _editor.Document.Text = change.GetNewValue<string?>() ?? string.Empty;
    }
}

﻿using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Avalon.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynPad.Controls;
using RoslynPad.Editor;
using RoslynPad.Build;
using RoslynPad.UI;
using System.Windows.Data;
using System.Globalization;
using ICSharpCode.AvalonEdit.Folding;

namespace RoslynPad;

public partial class DocumentView : IDisposable
{
    private readonly SynchronizationContext? _syncContext;
    private readonly MarkerMargin _errorMargin;
    private OpenDocumentViewModel? _viewModel;
    private IResultObject? _contextMenuResultObject;

    public DocumentView()
    {
        InitializeComponent();

        _errorMargin = new MarkerMargin { Visibility = Visibility.Collapsed, MarkerImage = TryFindResource("ExceptionMarker") as ImageSource, Width = 10 };
        Editor.TextArea.LeftMargins.Insert(0, _errorMargin);
        Editor.PreviewMouseWheel += EditorPreviewMouseWheel;
        Editor.TextArea.Caret.PositionChanged += CaretOnPositionChanged;
        Editor.TextArea.SelectionChanged += EditorSelectionChanged;

        _syncContext = SynchronizationContext.Current;

        DataContextChanged += OnDataContextChanged;
        foldingManager = ICSharpCode.AvalonEdit.Folding.FoldingManager.Install(Editor.TextArea);
        Editor.TextChanged += EditorTextChanged;
    }

    FoldingManager foldingManager;
    BraceFoldingStrategy foldingStrategy = new BraceFoldingStrategy();
    private void EditorTextChanged(object? sender, EventArgs e)
    {
        if (foldingManager == null) return;
        foldingStrategy.UpdateFoldings(foldingManager, Editor.Document);
    }
    public OpenDocumentViewModel ViewModel => _viewModel.NotNull();

    private void EditorSelectionChanged(object? sender, EventArgs e) 
        => ViewModel.SelectedText = Editor.SelectedText;

    private void CaretOnPositionChanged(object? sender, EventArgs eventArgs)
    {
        Ln.Text = Editor.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture);
        Col.Text = Editor.TextArea.Caret.Column.ToString(CultureInfo.InvariantCulture);
    }

    private void EditorPreviewMouseWheel(object? sender, MouseWheelEventArgs args)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.MainViewModel.EditorFontSize += args.Delta > 0 ? 1 : -1;
            args.Handled = true;
        }
    }
    
    private void ResultTreePreviewMouseWheel(object? sender, MouseWheelEventArgs args)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var fontSize = ResultTree.FontSize + (args.Delta > 0 ? 1 : -1);
            if (!MainViewModelBase.IsValidFontSize(fontSize))
            {
                return;
            }

            ResultTree.FontSize = fontSize;
            args.Handled = true;
        }
    }

    private async void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs args)
    {
        _viewModel = (OpenDocumentViewModel)args.NewValue;
        BindingOperations.EnableCollectionSynchronization(_viewModel.Results, _viewModel.Results);

        _viewModel.ResultsAvailable += ResultsAvailable;
        _viewModel.ReadInput += OnReadInput;
        _viewModel.NuGet.PackageInstalled += NuGetOnPackageInstalled;

        _viewModel.EditorFocus += (o, e) => Editor.Focus();
        _viewModel.DocumentUpdated += (o, e) => Dispatcher.InvokeAsync(() => Editor.RefreshHighlighting());

        _viewModel.MainViewModel.EditorFontSizeChanged += EditorFontSizeChanged;
        Editor.FontSize = _viewModel.MainViewModel.EditorFontSize;

        var documentText = await _viewModel.LoadTextAsync().ConfigureAwait(true);

        var documentId = await Editor.InitializeAsync(_viewModel.MainViewModel.RoslynHost, new ClassificationHighlightColors(),
            _viewModel.WorkingDirectory, documentText, _viewModel.SourceCodeKind).ConfigureAwait(true);

        _viewModel.Initialize(documentId, OnError,
            () => new TextSpan(Editor.SelectionStart, Editor.SelectionLength),
            this);

        Editor.Document.TextChanged += (o, e) => _viewModel.OnTextChanged();
    }

    private void OnReadInput()
    {
        var textBox = new TextBox();

        var dialog = new TaskDialog
        {
            Header = "Console Input",
            Content = textBox,
            Background = Brushes.White,
        };

        textBox.Loaded += (o, e) => textBox.Focus();

        textBox.KeyDown += (o, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TaskDialog.CancelCommand.Execute(null, dialog);
            }
        };

        dialog.ShowInline(this);

        ViewModel.SendInput(textBox.Text);
    }

    private void ResultsAvailable()
    {
        ViewModel.ResultsAvailable -= ResultsAvailable;

        _syncContext?.Post(o => ResultPaneRow.Height = new GridLength(1, GridUnitType.Star), null);
    }

    private void OnError(ExceptionResultObject? e)
    {
        if (e != null)
        {
            _errorMargin.Visibility = Visibility.Visible;
            _errorMargin.LineNumber = e.LineNumber;
            _errorMargin.Message = "Exception: " + e.Message;
        }
        else
        {
            _errorMargin.Visibility = Visibility.Collapsed;
        }
    }

    private void EditorFontSizeChanged(double fontSize)
    {
        Editor.FontSize = fontSize;
    }

    private void NuGetOnPackageInstalled(PackageData package)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var text = $"#r \"nuget: {package.Id}, {package.Version}\"{Environment.NewLine}";
            Editor.Document.Insert(0, text, AnchorMovementType.Default);
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.T:
                    e.Handled = true;
                    NuGetSearch.Focus();
                    break;
            }
        }
    }

    private void Editor_OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(Editor.Focus, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_viewModel?.MainViewModel != null)
        {
            _viewModel.MainViewModel.EditorFontSizeChanged -= EditorFontSizeChanged;
        }
    }

    private void ResultTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                CopyAllResultsToClipboard(withChildren: true);
            }
            else
            {
                CopyToClipboard(e.OriginalSource);
            }
        }
        else if (e.Key == Key.Enter)
        {
            TryJumpToLine(e.OriginalSource);
        }
    }

    private void ResultTreeDoubleClick(object? sender, MouseButtonEventArgs e)
    {
        TryJumpToLine(e.OriginalSource);
    }

    private void TryJumpToLine(object source)
    {
        if ((source as FrameworkElement)?.DataContext is not CompilationErrorResultObject result) return;

        Editor.TextArea.Caret.Line = result.Line;
        Editor.TextArea.Caret.Column = result.Column;
        Editor.ScrollToLine(result.Line);

        _ = Dispatcher.InvokeAsync(Editor.Focus);
    }

    private void CopyCommand(object? sender, ExecutedRoutedEventArgs e)
    {
        CopyToClipboard(e.OriginalSource);
    }

    private void CopyClick(object? sender, RoutedEventArgs e)
    {
        CopyToClipboard(sender);
    }

    private void CopyToClipboard(object? sender)
    {
        var result = (sender as FrameworkElement)?.DataContext as IResultObject ??
                    _contextMenuResultObject;

        if (result != null)
        {
            Clipboard.SetText(ReferenceEquals(sender, CopyValueWithChildren) ? result.ToString() : result.Value);
        }
    }

    private void CopyAllClick(object? sender, RoutedEventArgs e)
    {
        var withChildren = ReferenceEquals(sender, CopyAllValuesWithChildren);

        CopyAllResultsToClipboard(withChildren);
    }

    private void CopyAllResultsToClipboard(bool withChildren)
    {
        var builder = new StringBuilder();
        foreach (var result in ViewModel.Results)
        {
            if (withChildren)
            {
                result.WriteTo(builder);
                builder.AppendLine();
            }
            else
            {
                builder.AppendLine(result.Value);
            }
        }

        if (builder.Length > 0)
        {
            Clipboard.SetText(builder.ToString());
        }
    }

    private void ResultTree_OnContextMenuOpening(object? sender, ContextMenuEventArgs e)
    {
        // keyboard-activated
        if (e.CursorLeft < 0 || e.CursorTop < 0)
        {
            _contextMenuResultObject = ResultTree.SelectedItem as IResultObject;
        }
        else
        {
            _contextMenuResultObject = (e.OriginalSource as FrameworkElement)?.DataContext as IResultObject;
        }

        var isResult = _contextMenuResultObject != null;
        CopyValue.IsEnabled = isResult;
        CopyValueWithChildren.IsEnabled = isResult;
    }

    private void SearchTerm_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ViewModel.NuGet.Packages?.Any() == true)
        {
            if (!ViewModel.NuGet.IsPackagesMenuOpen)
            {
                ViewModel.NuGet.IsPackagesMenuOpen = true;
            }
            RootNuGetMenu.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Editor.Focus();
        }
    }

    private void ScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        HeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ILViewerTab.IsSelected && ILViewerTab.Content == null)
        {
            var ilViewer = new ILViewer();
            ilViewer.SetBinding(TextElement.FontSizeProperty,
                nameof(_viewModel.MainViewModel) + "." + nameof(_viewModel.MainViewModel.EditorFontSize));
            ilViewer.SetBinding(ILViewer.TextProperty, nameof(_viewModel.ILText));
            ILViewerTab.Content = ilViewer;
        }
    }
}

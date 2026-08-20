// WinPlasma.Spotlight — Views/SpotlightWindow.xaml.cs
// The overlay search window. Pre-created at startup, shown/hidden instantly.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinPlasma.Spotlight.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WinPlasma.Spotlight.Views;

public sealed partial class SpotlightWindow : Window
{
    private readonly SearchService _search;
    private CancellationTokenSource? _searchCts;
    private bool _isVisible;

    // Win32 constants
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public SpotlightWindow(SearchService search)
    {
        InitializeComponent();
        _search = search;
        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Fullscreen transparent window (covers entire screen — click-outside detection)
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        // Size to primary monitor work area
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.MoveAndResize(new RectInt32(
            display.WorkArea.X, display.WorkArea.Y,
            display.WorkArea.Width, display.WorkArea.Height));

        // Tool window style (no taskbar button)
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        // Use system backdrop
        SystemBackdrop = new Microsoft.UI.Xaml.Media.TransparentBackdrop();

        // Start hidden
        AppWindow.Hide();
        _isVisible = false;
    }

    public void ShowOverlay()
    {
        SearchBox.Text = string.Empty;
        ResultsList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;

        AppWindow.Show();
        _isVisible = true;

        // Focus the search box
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    public void HideOverlay()
    {
        AppWindow.Hide();
        _isVisible = false;
        _searchCts?.Cancel();
    }

    public bool IsOverlayVisible => _isVisible;

    // ── Search ────────────────────────────────────────────────────────────────

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        ClearButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrEmpty(query))
        {
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        // Cancel any previous search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        SearchSpinner.Visibility = Visibility.Visible;
        SearchSpinner.IsActive = true;
        ResultsList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;

        try
        {
            // Debounce — wait 150ms before searching to avoid hammering on every keystroke
            await Task.Delay(150, token);

            var results = await _search.SearchAsync(query, token);

            if (token.IsCancellationRequested) return;

            ResultsList.ItemsSource = results;
            ResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Cancelled — a newer search is running
        }
        finally
        {
            SearchSpinner.IsActive = false;
            SearchSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HideOverlay();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // Launch first result
            if (ResultsList.Items.Count > 0 && ResultsList.Items[0] is SearchResult result)
            {
                SearchService.Launch(result);
                HideOverlay();
            }
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            ResultsList.Focus(FocusState.Keyboard);
            if (ResultsList.Items.Count > 0)
                ResultsList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResult result)
        {
            SearchService.Launch(result);
            HideOverlay();
        }
    }

    private void DismissOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Dismiss if clicking outside the search card
        var point = e.GetCurrentPoint(SearchCard);
        if (point.Position.X < 0 || point.Position.Y < 0 ||
            point.Position.X > SearchCard.ActualWidth ||
            point.Position.Y > SearchCard.ActualHeight)
        {
            HideOverlay();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

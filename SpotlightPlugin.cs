// WinPlasma.Spotlight — SpotlightPlugin.cs
// IPlugin entry point. Registers global hotkey (Alt+Space) and manages the overlay window.

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using WinPlasma.SDK;
using WinPlasma.SDK.Models;
using WinPlasma.Spotlight.Services;
using WinPlasma.Spotlight.Views;

namespace WinPlasma.Spotlight;

/// <summary>
/// Win Plasma Spotlight Search plugin.
/// Registers Alt+Space global hotkey and shows/hides the search overlay.
/// </summary>
public sealed class SpotlightPlugin : IPlugin
{
    public string Id          => "com.winplasma.spotlight";
    public string Name        => "Spotlight Search";
    public string Version     => "1.0.0";
    public string Author      => "WinPlasma";
    public string Description => "macOS-style app, file, and settings search. Press Alt+Space.";

    private WinPlasmaContext? _context;
    private SearchService? _search;
    private SpotlightWindow? _window;
    private Thread? _hotkeyThread;
    private volatile bool _running;

    // Hotkey registration
    private const int HOTKEY_ID = 0xBEEF;
    private const int MOD_ALT = 0x0001;
    private const int MOD_WIN = 0x0008;
    private const int VK_SPACE = 0x20;

    public async Task InitializeAsync(WinPlasmaContext context)
    {
        _context = context;
        _context.Logger.LogInfo("Spotlight: Initialized.");
        await Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        _context?.Logger.LogInfo("Spotlight: Starting...");

        _search = new SearchService();
        await _search.InitializeAsync(); // Pre-warm app cache

        // Create the overlay window (must be on UI thread with message pump)
        // The PluginHost provides a WinUI thread context
        _window = new SpotlightWindow(_search);

        // Start the hotkey listener on its own thread (Win32 message pump)
        _running = true;
        _hotkeyThread = new Thread(HotkeyMessageLoop) { IsBackground = true, Name = "SpotlightHotkey" };
        _hotkeyThread.Start();

        _context?.Logger.LogInfo("Spotlight: Started. Hotkey: Alt+Space");
    }

    public Task StopAsync()
    {
        _context?.Logger.LogInfo("Spotlight: Stopping...");
        _running = false;

        try { _window?.Close(); } catch { }
        _window = null;

        // Post WM_QUIT to the hotkey thread's message pump
        PostThreadMessage(_hotkeyThread is not null ? (uint)_hotkeyThread.ManagedThreadId : 0,
            0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);

        _context?.Logger.LogInfo("Spotlight: Stopped.");
        return Task.CompletedTask;
    }

    public Task<PluginSettingsSchema> GetSettingsSchemaAsync()
    {
        var schema = new PluginSettingsSchema
        {
            Fields =
            [
                new() { Key = "hotkey", Label = "Hotkey", FieldType = SettingsFieldType.Text,
                        Description = "Keyboard shortcut to open Spotlight (default: Alt+Space)",
                        DefaultValue = JsonValue.Create("Alt+Space") },
                new() { Key = "theme", Label = "Theme", FieldType = SettingsFieldType.Select,
                        Options = ["system", "dark", "light"],
                        DefaultValue = JsonValue.Create("system") },
                new() { Key = "searchWeb", Label = "Show web search", FieldType = SettingsFieldType.Bool,
                        DefaultValue = JsonValue.Create(true) },
                new() { Key = "webEngine", Label = "Web search engine", FieldType = SettingsFieldType.Select,
                        Options = ["google", "bing", "duckduckgo"],
                        DefaultValue = JsonValue.Create("google") }
            ]
        };
        return Task.FromResult(schema);
    }

    public Task ApplySettingsAsync(JsonObject settings) => Task.CompletedTask;

    // ── Win32 hotkey message loop ─────────────────────────────────────────────

    private void HotkeyMessageLoop()
    {
        // Get our thread's Win32 thread ID for the message pump
        var threadId = GetCurrentThreadId();

        // Register Alt+Space
        if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_ALT, VK_SPACE))
        {
            _context?.Logger.LogWarning($"Spotlight: Failed to register Alt+Space hotkey. " +
                $"Try changing the hotkey in settings.");
        }

        // Win32 message pump
        while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == 0x0312 /* WM_HOTKEY */ && msg.wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleOverlay();
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
    }

    private void ToggleOverlay()
    {
        if (_window is null) return;

        // Marshal to UI thread
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window.IsOverlayVisible)
                _window.HideOverlay();
            else
                _window.ShowOverlay();
        });
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point pt;
    }
}

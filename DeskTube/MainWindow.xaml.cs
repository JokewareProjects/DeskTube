using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Text.RegularExpressions;

namespace DeskTube
{
  public partial class MainWindow : Window
  {
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private WindowState _previousWindowState = WindowState.Normal;
    private WindowStyle _previousWindowStyle = WindowStyle.SingleBorderWindow;
    private Rect _previousBounds;
    private bool _isMiniPlayer = false;

    private static readonly string AppDataFolder = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "DeskTube");

    private static readonly string StateFilePath = Path.Combine(AppDataFolder, "window-state.json");
    private static readonly string UserDataFolder = Path.Combine(AppDataFolder, "WebView2Data");

    private record SavedWindowState(double Left, double Top, double Width, double Height, bool IsMaximized);

    public MainWindow()
    {
      InitializeComponent();
      RestoreWindowState();

      SourceInitialized += MainWindow_SourceInitialized;
      Closing += MainWindow_Closing;
      InitializeWebViewAsync();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
      IntPtr hwnd = new WindowInteropHelper(this).Handle;

      int background = 0x000F0F0F; // #0F0F0F
      int textColor = 0x00FFFFFF;  // White text

      DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref background, sizeof(int));
      DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));

      // Listen for second-instance launch signal to bring this window to the front
      HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == App.WM_SHOW_DESKTUBE)
      {
        if (WindowState == WindowState.Minimized)
        {
          WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = _isMiniPlayer; // Keep Topmost only if Mini-Player is active
        handled = true;
      }
      return IntPtr.Zero;
    }

    private void RestoreWindowState()
    {
      try
      {
        if (!File.Exists(StateFilePath)) return;

        var json = File.ReadAllText(StateFilePath);
        var state = JsonSerializer.Deserialize<SavedWindowState>(json);
        if (state == null || state.Width < 200 || state.Height < 200) return;

        bool isOnScreen =
          state.Left + state.Width > SystemParameters.VirtualScreenLeft + 50 &&
          state.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
          state.Top >= SystemParameters.VirtualScreenTop &&
          state.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

        if (isOnScreen)
        {
          WindowStartupLocation = WindowStartupLocation.Manual;
          Left = state.Left;
          Top = state.Top;
        }

        Width = state.Width;
        Height = state.Height;

        if (state.IsMaximized)
        {
          WindowState = WindowState.Maximized;
        }
      }
      catch
      {
        // Fall back to XAML defaults if state file is missing or unreadable
      }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
      try
      {
        Directory.CreateDirectory(AppDataFolder);

        bool isFullscreen = WindowStyle == WindowStyle.None;
        Rect boundsToSave = (_isMiniPlayer || isFullscreen)
          ? _previousBounds
          : (WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds);

        bool wasMaximized = (_isMiniPlayer || isFullscreen)
          ? _previousWindowState == WindowState.Maximized
          : WindowState == WindowState.Maximized;

        var state = new SavedWindowState(
          boundsToSave.Left,
          boundsToSave.Top,
          boundsToSave.Width,
          boundsToSave.Height,
          wasMaximized);

        File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state));
      }
      catch
      {
        // Prevent file I/O errors from blocking app exit
      }
    }

    private async void InitializeWebViewAsync()
    {
      // Eliminate startup white flash
      webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 15, 15);

      // Initialize WebView2 with persistent UserDataFolder so login stays saved
      Directory.CreateDirectory(UserDataFolder);
      var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
      await webView.EnsureCoreWebView2Async(env);

      // Open external links in default browser
      webView.CoreWebView2.NewWindowRequested += (sender, args) =>
      {
        args.Handled = true;
        Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
      };

      
      // Custom dark scrollbars + Alt + P Mini-Player shortcut
      await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
        window.addEventListener('DOMContentLoaded', () => {
          const style = document.createElement('style');
          style.textContent = `
            ::-webkit-scrollbar {
              width: 10px;
              height: 10px;
              background: #0F0F0F;
            }
            ::-webkit-scrollbar-track {
              background: #0F0F0F;
            }
            ::-webkit-scrollbar-thumb {
              background: #333333;
              border-radius: 5px;
              border: 2px solid #0F0F0F;
            }
            ::-webkit-scrollbar-thumb:hover {
              background: #555555;
            }
            ::-webkit-scrollbar-corner {
              background: #0F0F0F;
            }
          `;
          document.head.appendChild(style);
        });

        window.addEventListener('keydown', function(e) {
          if (e.altKey && e.code === 'KeyP') {
            e.preventDefault();
            window.chrome.webview.postMessage('toggle-mini-player');
          }
        }, true);
      ");

      webView.CoreWebView2.WebMessageReceived += (sender, args) =>
      {
        if (args.TryGetWebMessageAsString() == "toggle-mini-player")
        {
          ToggleMiniPlayer();
        }
      };

      // Handle true fullscreen toggle (F key or player fullscreen button)
      webView.CoreWebView2.ContainsFullScreenElementChanged += (sender, args) =>
      {
        if (webView.CoreWebView2.ContainsFullScreenElement)
        {
          if (_isMiniPlayer) ToggleMiniPlayer();

          _previousWindowState = WindowState;
          _previousWindowStyle = WindowStyle;
          _previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

          if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;

          WindowStyle = WindowStyle.None;
          WindowState = WindowState.Maximized;
        }
        else
        {
          WindowStyle = _previousWindowStyle;
          WindowState = _previousWindowState;
        }
      };

      // Navigate to YouTube after environment & scripts are ready
      webView.CoreWebView2.Navigate("https://youtube.com");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
      if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt &&
          (e.Key == Key.P || e.SystemKey == Key.P))
      {
        e.Handled = true;
        ToggleMiniPlayer();
        return;
      }
      base.OnPreviewKeyDown(e);
    }

    private async void ToggleMiniPlayer()
    {
      if (webView.CoreWebView2 == null) return;

      if (!_isMiniPlayer)
      {
        _previousWindowState = WindowState;
        _previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

        if (WindowState == WindowState.Maximized)
          WindowState = WindowState.Normal;

        Topmost = true;
        Width = 480;
        Height = 302;
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 20;

        string enablePipCss = @"
          (function() {
            let style = document.getElementById('wpf-mini-player-style');
            if (!style) {
              style = document.createElement('style');
              style.id = 'wpf-mini-player-style';
              style.innerHTML = `
                ytd-masthead, #secondary, #below, #comments, tp-yt-app-drawer { display: none !important; }
                ytd-app { --ytd-masthead-height: 0px !important; }
                #page-manager { margin-top: 0 !important; }
                #movie_player {
                  position: fixed !important;
                  top: 0 !important; left: 0 !important;
                  width: 100vw !important; height: 100vh !important;
                  z-index: 99999 !important;
                }
              `;
              document.head.appendChild(style);
            }
          })();";
        await webView.CoreWebView2.ExecuteScriptAsync(enablePipCss);

        _isMiniPlayer = true;
      }
      else
      {
        Topmost = false;
        Left = _previousBounds.Left;
        Top = _previousBounds.Top;
        Width = _previousBounds.Width;
        Height = _previousBounds.Height;
        WindowState = _previousWindowState;

string disablePipCss = "document.getElementById('wpf-mini-player-style')?.remove();";
        await webView.CoreWebView2.ExecuteScriptAsync(disablePipCss);
        _isMiniPlayer = false;
      }
    }
  }
}

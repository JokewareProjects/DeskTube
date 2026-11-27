using System.Runtime.InteropServices;
using System.Windows;

namespace DeskTube
{
  public partial class App : Application
  {
    private static Mutex? _mutex;
    private const string MutexName = "DeskTube_SingleInstance_Mutex";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);

    private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
    public static readonly uint WM_SHOW_DESKTUBE = RegisterWindowMessage("WM_SHOW_DESKTUBE_WINDOW");

    protected override void OnStartup(StartupEventArgs e)
    {
      _mutex = new Mutex(true, MutexName, out bool isNewInstance);

      if (!isNewInstance)
      {
        // Signal the existing DeskTube window to come to the front, then close this duplicate
        PostMessage(HWND_BROADCAST, WM_SHOW_DESKTUBE, IntPtr.Zero, IntPtr.Zero);
        Shutdown();
        return;
      }

      base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
      _mutex?.ReleaseMutex();
      _mutex?.Dispose();
      base.OnExit(e);
    }
  }
}

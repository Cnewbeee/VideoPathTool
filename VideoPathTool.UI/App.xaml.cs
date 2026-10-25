using System.Windows;
using System.Windows.Threading;

namespace VideoPathTool.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Never let a background frame crash the whole window silently: the
        // scheduler already catches its own errors, this is the last resort.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                MessageBox.Show(ex.ToString(), "未处理的异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "界面异常", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

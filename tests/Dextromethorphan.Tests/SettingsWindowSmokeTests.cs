using System.Windows;
using Dextromethorphan.App;

namespace Dextromethorphan.Tests;

public sealed class SettingsWindowSmokeTests
{
    [Fact]
    public async Task AudioProfileEditorParsesOnStaThread()
    {
        var completion =
            new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? application = null;
            SettingsWindow? window = null;
            try
            {
                application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                application.Resources.MergedDictionaries.Add(
                    new ResourceDictionary
                    {
                        Source = new Uri(
                            "pack://application:,,,/Dextromethorphan;component/UI/Styles/Theme.xaml",
                            UriKind.Absolute)
                    });
                window = new SettingsWindow();
                Assert.Equal(
                    "Dextromethorphan settings",
                    window.Title);
                var tabs = Assert.IsType<
                    System.Windows.Controls.TabControl>(
                    window.Content);
                Assert.True(tabs.Items.Count >= 2);
                var audio = Assert.IsType<
                    System.Windows.Controls.TabItem>(
                    tabs.Items[0]);
                Assert.Equal("Audio", audio.Header);
                Assert.NotNull(audio.Content);
                completion.SetResult(null);
            }
            catch (Exception exception)
            {
                completion.SetResult(exception);
            }
            finally
            {
                window?.Close();
                application?.Shutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var error = await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);
        Assert.Null(error);
    }

}

using System.Windows;
using Dextromethorphan.App;

namespace Dextromethorphan.Tests;

public sealed class SettingsWindowSmokeTests
{
    [Fact]
    public async Task EverySettingsSectionMaterializesOnStaThread()
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
                window = new SettingsWindow
                {
                    DataContext = new SettingsBindingProbe()
                };
                Assert.Equal(
                    "Dextromethorphan settings",
                    window.Title);
                var frame = Assert.IsType<
                    System.Windows.Controls.Border>(window.Content);
                var layout = Assert.IsType<
                    System.Windows.Controls.Grid>(frame.Child);
                var tabs = Assert.Single(layout.Children.OfType<
                    System.Windows.Controls.TabControl>());
                var expectedHeaders = new[]
                {
                    "Audio", "Playback", "Library", "Metadata", "Lyrics",
                    "Appearance", "Views", "Diagnostics", "Data", "Shortcuts",
                    "About"
                };
                Assert.Equal(expectedHeaders.Length, tabs.Items.Count);
                Assert.Equal(
                    expectedHeaders,
                    tabs.Items.OfType<System.Windows.Controls.TabItem>()
                        .Select(item => item.Header?.ToString()));
                window.Show();
                tabs.ApplyTemplate();
                var selectedContentHost = Assert.IsType<
                    System.Windows.Controls.ContentPresenter>(
                    tabs.Template.FindName(
                        "PART_SelectedContentHost",
                        tabs));
                foreach (var tab in tabs.Items.OfType<System.Windows.Controls.TabItem>())
                {
                    Assert.NotNull(tab.Content);
                    tabs.SelectedItem = tab;
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.True(tab.IsSelected);
                    Assert.Same(tab.Content, selectedContentHost.Content);
                    Assert.True(selectedContentHost.IsVisible);
                    Assert.True(
                        System.Windows.Media.VisualTreeHelper.GetChildrenCount(
                            selectedContentHost) > 0);
                }
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

    private sealed class SettingsBindingProbe
    {
        public double ReplayGainAnalysisProgress => 42;
    }
}

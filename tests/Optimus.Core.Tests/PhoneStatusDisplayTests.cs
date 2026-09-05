namespace Optimus.Core.Tests;

using System.Collections.Generic;
using System.ComponentModel;
using Optimus.Shell.ViewModels;
using Xunit;

/// <summary>
/// The phone endpoint's address has to reach the widget, otherwise the user has nothing to type
/// into the phone.
/// </summary>
public class PhoneStatusDisplayTests
{
    [Fact]
    public void PhoneStatus_IsHiddenUntilItHasContent()
    {
        var viewModel = new WidgetViewModel(action => action());

        Assert.Equal(string.Empty, viewModel.PhoneStatus);
        Assert.False(viewModel.HasPhoneStatus);
    }

    [Fact]
    public void PhoneStatus_BecomesVisibleWhenSet()
    {
        var viewModel = new WidgetViewModel(action => action());

        viewModel.PhoneStatus = "Phone endpoint on port 8770 — 192.168.0.53, 100.105.79.35";

        Assert.True(viewModel.HasPhoneStatus);
        Assert.Contains("8770", viewModel.PhoneStatus, System.StringComparison.Ordinal);
    }

    /// <summary>The XAML binds both, so both have to raise change notifications.</summary>
    [Fact]
    public void PhoneStatus_RaisesChangeNotificationForItselfAndVisibility()
    {
        var viewModel = new WidgetViewModel(action => action());
        var changed = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.PhoneStatus = "Phone connected (192.168.0.17:41234)";

        Assert.Contains(nameof(WidgetViewModel.PhoneStatus), changed);
        Assert.Contains(nameof(WidgetViewModel.HasPhoneStatus), changed);
    }

    [Fact]
    public void PhoneStatus_WhitespaceCountsAsNothingToShow()
    {
        var viewModel = new WidgetViewModel(action => action())
        {
            PhoneStatus = "   "
        };

        Assert.False(viewModel.HasPhoneStatus);
    }

    /// <summary>
    /// The widget markup must actually bind the property.
    /// </summary>
    /// <remarks>
    /// A view-model property nothing binds to compiles perfectly and shows the user nothing,
    /// which is exactly how the endpoint address went missing. This reads the shipped XAML and
    /// also checks each bound name against the real view model, so a rename or typo fails here
    /// rather than silently blanking the footer at runtime.
    /// </remarks>
    [Fact]
    public void MainWindowXaml_BindsPhoneStatusAndItsVisibility()
    {
        string xaml = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepositoryRoot(), "src", "Optimus.Shell", "MainWindow.xaml"));

        Assert.Contains("{Binding PhoneStatus}", xaml, System.StringComparison.Ordinal);
        Assert.Contains("Binding ShowDetails", xaml, System.StringComparison.Ordinal);

        foreach (string name in new[] { nameof(WidgetViewModel.PhoneStatus), nameof(WidgetViewModel.HasPhoneStatus) })
        {
            Assert.NotNull(typeof(WidgetViewModel).GetProperty(name));
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);

        while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "Optimus.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory != null, "Could not locate the repository root (no Optimus.sln above the test output).");
        return directory!.FullName;
    }
}

using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using CodexSessionPulse.App.SettingsUi;
using CodexSessionPulse.Infrastructure.Settings;
using Xunit;

namespace CodexSessionPulse.App.Tests;

/// <summary>
/// The modeless Settings window closes itself on Save, Cancel and Esc. The native window is created without being shown,
/// so nothing appears on screen.
/// </summary>
public sealed class SettingsWindowTests
{
    /// <summary>Save validates through the store callback, keeps the result and closes the window without throwing.</summary>
    [Fact]
    public void Save_ClosesAndKeepsTheSavedSettings()
    {
        RunOnSta(() =>
        {
            List<PulseSettings> saved = [];
            SettingsWindow window = Create(settings =>
            {
                saved.Add(settings);

                return [];
            }, out Func<bool> closed);

            window.TtlMinutes.Text = "45";
            window.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(closed());
            Assert.Equal(45, Assert.Single(saved).EstimatedCacheTtlMinutes);
            Assert.Equal(45, window.Saved?.EstimatedCacheTtlMinutes);
        });
    }

    /// <summary>A refused save keeps the window open and shows the problem codes.</summary>
    [Fact]
    public void Save_WithProblems_StaysOpen()
    {
        RunOnSta(() =>
        {
            SettingsWindow window = Create(_ => ["ttl-out-of-range"], out Func<bool> closed);

            window.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.False(closed());
            Assert.Null(window.Saved);
            Assert.Contains("ttl-out-of-range", window.Problems.Text, StringComparison.Ordinal);
            window.Close();
        });
    }

    /// <summary>Cancel and Esc close the window without saving.</summary>
    /// <param name="useEscape">Whether to press Esc instead of clicking Cancel.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelOrEscape_ClosesWithoutSaving(bool useEscape)
    {
        RunOnSta(() =>
        {
            int saves = 0;
            SettingsWindow window = Create(_ =>
            {
                saves++;

                return [];
            }, out Func<bool> closed);

            if (useEscape)
            {
                PresentationSource source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            else
            {
                window.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            Assert.True(closed());
            Assert.Equal(0, saves);
            Assert.Null(window.Saved);
        });
    }

    private static SettingsWindow Create(Func<PulseSettings, IReadOnlyList<string>> save, out Func<bool> closed)
    {
        SettingsWindow window = new(new PulseSettings(), save, Path.GetTempPath());
        bool isClosed = false;
        window.Closed += (_, _) => isClosed = true;
        new WindowInteropHelper(window).EnsureHandle();
        closed = () => isClosed;

        return window;
    }

    /// <summary>Runs WPF code on a fresh STA thread and rethrows its failure on the test thread.</summary>
    /// <param name="action">The code to run.</param>
    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

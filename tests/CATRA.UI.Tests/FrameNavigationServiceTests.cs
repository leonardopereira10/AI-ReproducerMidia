using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using CATRA.UI.Navigation;
using FluentAssertions;
using Xunit;

namespace CATRA.UI.Tests;

/// <summary>
/// Tests the view-model disposal contract used by
/// <see cref="FrameNavigationService"/> when a page is navigated away from
/// (ST-19 follow-up leak fix). A real WPF <see cref="Frame"/> cannot run
/// headless, so the extracted <see cref="FrameNavigationService.TryDisposeDataContext"/>
/// helper is exercised directly: it must dispose a disposable DataContext
/// exactly once, ignore non-disposable/non-page content, and never let a
/// throwing VM break navigation. WPF elements require an STA thread, so each
/// test body runs on a dedicated STA thread (xUnit facts are MTA).
/// </summary>
public sealed class FrameNavigationServiceTests
{
    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("boom");
    }

    /// <summary>Runs the test body on an STA thread (WPF elements require it).</summary>
    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    [Fact]
    public void TryDisposeDataContext_PageWithDisposableVm_DisposesOnce() => RunOnSta(() =>
    {
        var vm = new CountingDisposable();
        var page = new Page { DataContext = vm };

        FrameNavigationService.TryDisposeDataContext(page).Should().BeTrue();

        vm.DisposeCalls.Should().Be(1);
    });

    [Fact]
    public void TryDisposeDataContext_FrameworkElementWithDisposableVm_Disposes() => RunOnSta(() =>
    {
        var vm = new CountingDisposable();
        var element = new FrameworkElement { DataContext = vm };

        FrameNavigationService.TryDisposeDataContext(element).Should().BeTrue();

        vm.DisposeCalls.Should().Be(1);
    });

    [Fact]
    public void TryDisposeDataContext_NonDisposableVm_ReturnsFalseAndDoesNotThrow() => RunOnSta(() =>
    {
        var page = new Page { DataContext = new object() };

        FrameNavigationService.TryDisposeDataContext(page).Should().BeFalse();
    });

    [Fact]
    public void TryDisposeDataContext_NullDataContext_ReturnsFalse() => RunOnSta(() =>
    {
        var page = new Page();

        FrameNavigationService.TryDisposeDataContext(page).Should().BeFalse();
    });

    [Fact]
    public void TryDisposeDataContext_NullContent_ReturnsFalse()
    {
        // No WPF element involved: safe to assert on the MTA test thread.
        FrameNavigationService.TryDisposeDataContext(null).Should().BeFalse();
    }

    [Fact]
    public void TryDisposeDataContext_NonPageContent_ReturnsFalse()
    {
        FrameNavigationService.TryDisposeDataContext("not a page").Should().BeFalse();
    }

    [Fact]
    public void TryDisposeDataContext_ThrowingVm_SwallowsExceptionSoNavigationNeverCrashes() => RunOnSta(() =>
    {
        var page = new Page { DataContext = new ThrowingDisposable() };

        var act = () => FrameNavigationService.TryDisposeDataContext(page);

        act.Should().NotThrow("a faulty VM must never break navigation");
        FrameNavigationService.TryDisposeDataContext(page).Should().BeFalse();
    });
}

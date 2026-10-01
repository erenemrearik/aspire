// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class TerminalsTests : DashboardTestContext
{
    [Fact]
    public void InitialConnection_DoesNotRedirectBeforeSnapshotLoads()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true, whenConnected: connected.Task,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("shell");
        Assert.Equal("http://localhost/terminals/resource/shell", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Empty(cut.FindComponents<TerminalView>());
        connected.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("shell", cut.FindComponent<TerminalView>().Instance.ResourceName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Selector_OnlyTerminalResourcesAndIndividualReplicas(bool isDesktop)
    {
        var client = CreateClient(
            ModelTestHelpers.CreateResource("ordinary"),
            TerminalSetupHelpers.CreateTerminalResource("shell-1", displayName: "shell", replicaCount: 2),
            TerminalSetupHelpers.CreateTerminalResource("shell-2", displayName: "shell", replicaIndex: 1, replicaCount: 2, state: KnownResourceState.Waiting),
            TerminalSetupHelpers.CreateTerminalResource("hidden", hidden: true));
        TerminalsSetupHelpers.SetupPage(this, client);
        var dialogProvider = Render<CascadingValue<ViewportInformation>>(builder => builder
            .Add(p => p.Value, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false))
            .AddChildContent<FluentDialogProvider>());
        var cut = RenderPage("shell-2", isDesktop);
        if (!isDesktop)
        {
            cut.Find(".mobile-toolbar").Click();
            dialogProvider.WaitForAssertion(() => Assert.Single(dialogProvider.FindComponents<ResourceSelect>()));
        }
        var selector = isDesktop ? cut.FindComponent<ResourceSelect>().Instance : dialogProvider.FindComponent<ResourceSelect>().Instance;
        Assert.False(selector.CanSelectGrouping);
        Assert.Equal(new string?[] { null, "shell-1", "shell-2" }, selector.Resources!.Select(r => r.Id?.InstanceId));
        Assert.All(selector.Resources!, r => Assert.NotNull(r.Id));
        var terminal = cut.FindComponent<TerminalView>().Instance;
        Assert.Equal("shell", terminal.ResourceName);
        Assert.Equal(1, terminal.ReplicaIndex);
        Assert.True(terminal.ShowOpenInWindow);
        Assert.NotNull(terminal.ResourceIcon);
        Assert.Empty(cut.FindComponents<LogViewer>());
    }

    [Theory]
    [InlineData(KnownResourceState.Waiting)]
    [InlineData(KnownResourceState.Starting)]
    [InlineData(KnownResourceState.Finished)]
    public void TerminalResources_NotRunning_RemainSelectable(KnownResourceState state)
    {
        TerminalsSetupHelpers.SetupPage(this, CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell", state: state)));
        var cut = RenderPage("shell");
        Assert.Equal("shell", cut.FindComponent<TerminalView>().Instance.ResourceName);
        Assert.Equal($"shell ({state})", Assert.Single(cut.FindComponent<ResourceSelect>().Instance.Resources!).Name);
    }

    [Theory]
    [InlineData(null, "first")]
    [InlineData("last", "last")]
    [InlineData("deleted", "first")]
    public void BaseRoute_RestoresSelectionOrSelectsFirst(string? storedResource, string expected)
    {
        var storage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.TerminalsPageState && storedResource is not null
                ? (true, new Terminals.TerminalsPageState(storedResource)) : (false, null)
        };
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("first"), TerminalSetupHelpers.CreateTerminalResource("last")), storage);
        var cut = RenderPage(null);
        Assert.Equal($"http://localhost/terminals/resource/{expected}", Services.GetRequiredService<NavigationManager>().Uri);
        // bUnit does not run the Router when NavigateTo changes the address.
        cut.Render(builder => builder.Add(p => p.ResourceName, expected));
        Assert.Equal(expected, cut.FindComponent<TerminalView>().Instance.ResourceName);
    }

    [Fact]
    public void ExplicitRoute_TakesPrecedenceOverSavedSelection()
    {
        Terminals.TerminalsPageState? saved = null;
        var storage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.TerminalsPageState
                ? (true, new Terminals.TerminalsPageState("first")) : (false, null),
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.TerminalsPageState)
                {
                    saved = Assert.IsType<Terminals.TerminalsPageState>(value);
                }
            }
        };
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("first"), TerminalSetupHelpers.CreateTerminalResource("last")), storage);
        Assert.Equal("last", RenderPage("last").FindComponent<TerminalView>().Instance.ResourceName);
        Assert.Equal("last", saved?.SelectedResource);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void UnavailablePage_RedirectsToRoot(bool enabled, bool readOnly, bool hasTerminal)
    {
        var client = new TestDashboardClient(isEnabled: enabled, isReadOnly: readOnly,
            initialResources: hasTerminal ? [TerminalSetupHelpers.CreateTerminalResource("shell")] : [],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("shell");
        Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Empty(cut.FindComponents<TerminalView>());
    }

    [Fact]
    public async Task HiddenOnlyResources_StayAvailableAndCanBeRevealed()
    {
        TerminalsSetupHelpers.SetupPage(this, CreateClient(TerminalSetupHelpers.CreateTerminalResource("hidden", hidden: true)));
        var cut = RenderPage(null);
        Assert.Equal("http://localhost/terminals", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Empty(cut.FindComponent<ResourceSelect>().Instance.Resources!);
        Assert.Equal(Resources.TerminalStrings.TerminalsNoVisibleResources, cut.Find("[role='status']").TextContent);
        var options = cut.FindComponents<AspireMenuButton>().Single(b => b.Instance.Title == Resources.TerminalStrings.TerminalsSettings);
        var showHidden = Assert.Single(options.Instance.ItemsProvider(), i => i.Text == Resources.ControlsStrings.ShowHiddenResources);
        await cut.InvokeAsync(showHidden.OnClick!);
        Assert.Equal("hidden", Assert.Single(cut.FindComponent<ResourceSelect>().Instance.Resources!).Id!.InstanceId);
        Assert.Equal("hidden", cut.FindComponent<TerminalView>().Instance.ResourceName);
    }

    [Fact]
    public async Task ResourceUpdates_PreserveViewerThenFallbackAndRedirect()
    {
        var updates = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var first = TerminalSetupHelpers.CreateTerminalResource("first");
        var last = TerminalSetupHelpers.CreateTerminalResource("last");
        var client = new TestDashboardClient(isEnabled: true, initialResources: [first, last], resourceChannelProvider: () => updates);
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("last");
        var viewer = cut.FindComponent<TerminalView>().Instance;
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert,
            TerminalSetupHelpers.CreateTerminalResource("last", state: KnownResourceState.Finished))]);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("last (Finished)", cut.Instance.PageViewModel.SelectedResource!.Name);
            Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
        });
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Delete, last)]);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("first", cut.FindComponent<TerminalView>().Instance.ResourceName);
            Assert.Equal("http://localhost/terminals/resource/first", Services.GetRequiredService<NavigationManager>().Uri);
        });
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("first"))]);
        cut.WaitForAssertion(() => Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri));
    }

    [Fact]
    public async Task SelectingResource_PersistsAndReconnectsExistingViewer()
    {
        Terminals.TerminalsPageState? saved = null;
        var storage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.TerminalsPageState)
                {
                    saved = Assert.IsType<Terminals.TerminalsPageState>(value);
                }
            }
        };
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("first"), TerminalSetupHelpers.CreateTerminalResource("last")), storage);
        var cut = RenderPage("first");
        var viewer = cut.FindComponent<TerminalView>().Instance;
        var selector = cut.FindComponent<ResourceSelect>();
        await cut.InvokeAsync(() => selector.Instance.SelectedResourceChanged.InvokeAsync(selector.Instance.Resources!.Last()));
        Assert.Equal("last", saved?.SelectedResource);
        Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
        Assert.Equal("last", viewer.ResourceName);
        Assert.Single(JSInterop.Invocations, i => i.Identifier == "reconnectTerminal");
    }

    [Theory]
    [InlineData("", "shell", "shell", 0)]
    [InlineData("/aspire/nested", "shell", "shell", 0)]
    [InlineData("", "terminal #1/?%+", "terminal%20%231%2F%3F%25%2B", 2)]
    public async Task OpenWindow_CarriesFontAndKeepsInlineViewer(string pathBase, string name, string escapedName, int replicaIndex)
    {
        Services.AddSingleton<NavigationManager>(new TestNavigationManager($"http://localhost{pathBase}/"));
        TerminalsSetupHelpers.SetupPage(this, CreateClient(TerminalSetupHelpers.CreateTerminalResource(name, replicaIndex, replicaIndex + 1)), pathBase: pathBase);
        var cut = RenderPage(name);
        var viewer = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => viewer.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FontPx = 17
        }));
        var open = cut.Find(".terminal-titlebar .terminal-open-window");
        Assert.Equal($"http://localhost{pathBase}/terminal-window/resource/{escapedName}/{replicaIndex}?fontSize=17",
            open.GetAttribute("data-terminal-window-url"));
        Assert.Equal(Resources.TerminalStrings.TerminalToolbarOpenInWindow, open.GetAttribute("aria-label"));
        var launcher = TerminalSetupHelpers.GetWindowLauncher(this, cut);
        await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync($"resource:{name}:{replicaIndex}", "opened"));
        Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
    }

    [Fact]
    public async Task Disposal_CancelsResourceWatchWithoutClosingProducer()
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell"));
        client.OnResourceSubscriptionDisposed = () => disposed.TrySetResult();
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("shell");
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        await disposed.Task.WaitAsync(DefaultWaitTimeout);
        Assert.Empty(client.ClosedTerminals);
    }

    private static TestDashboardClient CreateClient(params ResourceViewModel[] resources)
        => new(isEnabled: true, initialResources: resources,
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());

    private IRenderedComponent<Terminals> RenderPage(string? resourceName, bool isDesktop = true)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(new Uri(new Uri(navigation.BaseUri), DashboardUrls.TerminalsUrl(resourceName).TrimStart('/')).AbsoluteUri);
        return Render<Terminals>(builder => builder.Add(p => p.ResourceName, resourceName).Add(p => p.ViewportInformation, viewport));
    }
}

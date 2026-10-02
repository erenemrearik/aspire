// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Shared;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase
{
    private const string DashboardRunsHelpUrl = "https://aka.ms/aspire/run-persistence";

    private static readonly Icon s_checkmarkIcon = new Icons.Regular.Size16.Checkmark();
    private static readonly Icon s_historyIcon = new Icons.Regular.Size16.History();
    private static readonly Icon s_liveIcon = new Icons.Filled.Size12.Play();
    private static readonly Icon s_helpIcon = new Icons.Regular.Size16.QuestionCircle();
    private static readonly Icon s_pinIcon = new Icons.Regular.Size16.Pin();
    private static readonly Icon s_pinnedIcon = new Icons.Filled.Size16.Pin();
    private readonly string _runMenuItemIdPrefix = $"dashboard-run-{Guid.NewGuid():N}";

    private string RunSelectTitle => Loc[nameof(LayoutResources.DashboardRunSelectTitle)];
    // The button names the menu while the live view is shown, and the recording's date otherwise. The
    // accessible label always states what is being viewed so "Recordings" isn't mistaken for the selection.
    private string ButtonText => SelectedRunIsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectRecordings)]
        : SelectedRunText;
    private string RunSelectAccessibleLabel => Loc[nameof(LayoutResources.DashboardRunSelectAccessibleLabel), SelectedRunText];
    private string SelectedRunText => SelectedRunIsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectCurrent)]
        : FormatHelpers.FormatDateTime(TimeProvider, SelectedRunStartedAtUtc.UtcDateTime);

    [Parameter, EditorRequired]
    public required string SelectedRunId { get; set; }

    [Parameter]
    public bool SelectedRunIsCurrent { get; set; }

    [Parameter]
    public DateTimeOffset SelectedRunStartedAtUtc { get; set; }

    [Parameter]
    public EventCallback<string?> SelectedRunIdChanged { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IDashboardRunStore RunStore { get; init; }

    [Inject]
    public required ILogger<DashboardRunSelect> Logger { get; init; }

    private IList<MenuButtonItem> LoadRuns()
    {
        var runs = GetSortedRuns(RunStore.GetRuns());

        var menuItems = new List<MenuButtonItem>();
        var hasRecordings = false;
        foreach (var run in runs)
        {
            if (!run.IsCurrent && !hasRecordings)
            {
                hasRecordings = true;
                if (menuItems.Count > 0)
                {
                    menuItems.Add(new MenuButtonItem { IsDivider = true });
                }
                menuItems.Add(new MenuButtonItem
                {
                    Id = $"{_runMenuItemIdPrefix}-recordings-header",
                    IsGroupHeader = true,
                    Text = Loc[nameof(LayoutResources.DashboardRunSelectRecordings)]
                });
            }

            var isCompatible = run.IsCompatible;
            var menuItem = new MenuButtonItem
            {
                Id = $"{_runMenuItemIdPrefix}-{Uri.EscapeDataString(run.RunId)}",
                RenderKey = run.RunId,
                Text = FormatRunOption(run),
                Description = FormatRunDuration(run),
                Role = MenuItemRole.Radio,
                Checked = string.Equals(run.RunId, SelectedRunId, StringComparison.Ordinal),
                Icon = s_checkmarkIcon,
                StartIcon = run.IsCurrent ? s_liveIcon : null,
                StartIconColor = Color.Success,
                IsDisabled = !isCompatible,
                Tooltip = isCompatible ? null : Loc[nameof(LayoutResources.DashboardRunSelectIncompatibleTooltip)].Value,
                SecondaryActionIcon = run.IsPinned ? s_pinnedIcon : s_pinIcon,
                SecondaryActionAriaLabel = Loc[run.IsPinned
                    ? nameof(LayoutResources.DashboardRunSelectUnpin)
                    : nameof(LayoutResources.DashboardRunSelectPin)],
                IsSecondaryActionSelected = run.IsPinned,
                OnSecondaryActionClick = () =>
                {
                    SetRunPinned(run, !run.IsPinned);
                    return Task.CompletedTask;
                },
                OnClick = () => SelectedRunIdChanged.InvokeAsync(run.IsCurrent ? null : run.RunId)
            };
            menuItems.Add(menuItem);
        }

        menuItems.Add(new MenuButtonItem { IsDivider = true });
        menuItems.Add(MenuButtonItem.CreateExternalLink(
            Loc[nameof(LayoutResources.DashboardRunSelectHelp)],
            DashboardRunsHelpUrl,
            s_helpIcon,
            tooltip: Loc[nameof(LayoutResources.DashboardRunSelectHelpTooltip)]));

        return menuItems;
    }

    internal static List<DashboardRunDescriptor> GetSortedRuns(IReadOnlyList<DashboardRunDescriptor> storedRuns)
    {
        var runs = new List<DashboardRunDescriptor>(storedRuns.Count);
        foreach (var run in storedRuns)
        {
            if (!run.IsPruned && (run.IsSelectable || !run.IsCompatible))
            {
                runs.Add(run);
            }
        }
        runs.Sort(CompareRuns);

        return runs;
    }

    private static int CompareRuns(DashboardRunDescriptor left, DashboardRunDescriptor right)
    {
        var result = right.IsCurrent.CompareTo(left.IsCurrent);
        if (result == 0)
        {
            result = right.IsPinned.CompareTo(left.IsPinned);
        }
        if (result == 0)
        {
            result = right.StartedAtUtc.CompareTo(left.StartedAtUtc);
        }
        if (result == 0)
        {
            result = string.Compare(left.RunId, right.RunId, StringComparison.Ordinal);
        }

        return result;
    }

    private void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
    {
        try
        {
            RunStore.SetRunPinned(run, isPinned);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to update the pinned state of dashboard run '{RunId}'.", run.RunId);
        }
    }

    private string FormatRunOption(DashboardRunDescriptor run)
    {
        if (run.IsCurrent)
        {
            return Loc[nameof(LayoutResources.DashboardRunSelectCurrent)];
        }

        // Recordings always show their date, unlike the header button, so recordings from different
        // days remain distinguishable in the list.
        return FormatHelpers.FormatDateTime(TimeProvider, run.StartedAtUtc.UtcDateTime);
    }

    internal static string? FormatRunDuration(DashboardRunDescriptor run)
    {
        // Runs that didn't shut down cleanly have no end time, so their duration is unknown.
        if (run.IsCurrent || run.EndedAtUtc is not { } endedAtUtc || endedAtUtc < run.StartedAtUtc)
        {
            return null;
        }

        // Round to whole seconds: sub-second precision is noise for a run's length.
        var duration = TimeSpan.FromSeconds(Math.Round((endedAtUtc - run.StartedAtUtc).TotalSeconds));
        return DurationFormatter.FormatDuration(duration, CultureInfo.CurrentCulture);
    }
}

#pragma warning disable CS0618 // Terminal.Gui 2.5 TextView provides a read-only scrollable text surface.
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using DeviceRadar.Identification;
using DeviceRadar.Inventory;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace DeviceRadar.Tui;

internal static class RadarApp
{
    private const string SchemeName = "DeviceRadarLight";
    private static void Theme() => SchemeManager.AddScheme(SchemeName, new Scheme
    {
        Normal = new TgAttribute(new Color(35, 41, 49), new Color(250, 251, 252)),
        Focus = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
        HotFocus = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
        Active = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
        HotActive = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
        Editable = new TgAttribute(new Color(35, 41, 49), new Color(255, 255, 255))
    });
    private static Button Button(string title, int x = 0, int y = 0) => new()
    {
        Title = title,
        X = x,
        Y = y,
        ShadowStyle = ShadowStyles.None
    };
    private static void Fit(Dialog d, int height)
    {
        d.Width = Dim.Func(
            _ => Math.Min(100, Math.Max(1, (d.App?.Driver?.Cols ?? 80) - 4)));
        d.Height = Dim.Func(
            _ => Math.Min(height, Math.Max(1, (d.App?.Driver?.Rows ?? 24) - 4)));
    }
    private static Dialog Dialog(string title, int height)
    {
        var d = new Dialog { Title = title, SchemeName = SchemeName, BorderStyle = LineStyle.Single };
        Fit(d, height);
        return d;
    }
    private static void CloseButton(Dialog d, string title) => d.AddButton(Button(title));
    private static View Scroll(int x, int y, int height, int contentHeight)
    {
        var v = new View { X = x, Y = y, Width = Dim.Fill(2), Height = height, CanFocus = true };
        v.SetContentHeight(contentHeight);
        _ = v.VerticalScrollBar;
        v.KeyDown += (_, key) =>
        {
            if (key == Key.PageDown)
            {
                v.ScrollVertical(Math.Max(1, v.Viewport.Height - 1));
                key.Handled = true;
            }
            else if (key == Key.PageUp)
            {
                v.ScrollVertical(-Math.Max(1, v.Viewport.Height - 1));
                key.Handled = true;
            }
        };
        return v;
    }
    private sealed record DeviceRow(DeviceSnapshot Device, string DisplayName, string Marker)
    {
        public override string ToString() =>
            $"{(Device.IsPresent ? "●" : "○")} {UiText.Safe(DisplayName)}  ·  {UiText.Safe(Device.Connection)} {Marker}";
    }
    private sealed record HistoryRow(HistoryEntry Entry, string Location, string Reason)
    {
        public override string ToString() =>
            $"{Entry.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {Location}  {UiText.Safe(Entry.Status)}";
    }
    private sealed record CandidateRow(IdentificationCandidate Candidate)
    {
        public override string ToString() =>
            $"{UiText.Safe(Candidate.Name)}  ·  {UiText.Safe(Candidate.Source)}  ·  {Candidate.Confidence}";
    }
    private sealed record EditorRow(NamingCapability Capability, CheckBox Check, TextField Text);

    public static int Run()
    {
        Theme();
        using IApplication app = Application.Create().Init(DriverRegistry.Names.ANSI);
        var inventory = new InventoryService();
        var naming = new NamingService();
        var state = new PortableState();
        var loaded = state.LoadSettings();
        var ui = new UiText(loaded.Settings.Language);
        var identify = new IdentificationService(Runtime.BaseDirectory);
        var main = new Main(app, inventory, naming, state, ui, identify, loaded);
        using var watcher = inventory.Watch(items => app.Invoke(() => main.Update(items)),
                                            error => app.Invoke(() => main.Error(error)));
        app.Run(main.Window);
        return 0;
    }

    private sealed class Main
    {
        private readonly IApplication app;
        private readonly InventoryService inventory;
        private readonly NamingService naming;
        private readonly PortableState state;
        private readonly UiText ui;
        private readonly IdentificationService identify;
        private IReadOnlyList<DeviceSnapshot> devices = [];
        private bool hasBaseline;
        private readonly Dictionary<string, string> changes = new(StringComparer.OrdinalIgnoreCase);
        private string? selectedId;
        private bool updating;
        private bool modal;
        private string? locateId;
        private bool locateDisconnected;
        private readonly Dictionary<string, string> displayNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<DeviceRow> rows = [];
        private readonly ListView<DeviceRow> list;
        private readonly TextField search;
        private readonly CheckBox external, offline, internalDevices, virtualDevices;
        private readonly FrameView left, right;
        private readonly Label warning, footer, deviceName, connection, details, status, searchLabel;
        private readonly Button rename, locate, history, technical, elevate, settings;
        public Window Window { get; }
        private string T(string zh, string en) => ui.Get(zh, en);
        public Main(IApplication a, InventoryService i, NamingService n, PortableState s, UiText u,
                    IdentificationService id, SettingsLoadResult loaded)
        {
            app = a;
            inventory = i;
            naming = n;
            state = s;
            ui = u;
            identify = id;
            Window =
                new Window { Title = "Device Radar", Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = SchemeName };
            left = new FrameView { BorderStyle = LineStyle.Single, SchemeName = SchemeName };
            right = new FrameView { BorderStyle = LineStyle.Single, SchemeName = SchemeName };
            searchLabel = new Label { X = 1, Y = 0 };
            search = new TextField { X = 8, Y = 0, Width = Dim.Fill(1), Height = 1 };
            external =
                new CheckBox
                {
                    X = 1,
                    Y = 1,
                    Value = loaded.Settings.ExternalOnly ? CheckState.Checked : CheckState.UnChecked
                };
            offline = new CheckBox { X = 24, Y = 1, Value = CheckState.UnChecked };
            internalDevices = new CheckBox { X = 36, Y = 1, Value = CheckState.UnChecked };
            virtualDevices = new CheckBox { X = 49, Y = 1, Value = CheckState.UnChecked };
            list = new ListView<DeviceRow>
            {
                X = 1,
                Y = 2,
                Width = Dim.Fill(1),
                Height = Dim.Fill(),
                SchemeName = SchemeName
            };
            list.SetSource(rows);
            deviceName = new Label { X = 1, Y = 0, Width = Dim.Fill(1) };
            connection = new Label { X = 1, Y = 1, Width = Dim.Fill(1) };
            details = new Label { X = 1, Y = 2, Width = Dim.Fill(1), Height = 3 };
            status = new Label { X = 1, Y = 9, Width = Dim.Fill(1), Height = 1, Text = ui.Reason(loaded.Warning) };
            rename = Button("", 1, 5);
            locate = Button("", 12, 5);
            history = Button("", 27, 5);
            technical = Button("", 1, 7);
            settings = Button("", 16, 7);
            elevate = Button("", 31, 7);
            warning = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Height = 2, Visible = false };
            footer = new Label { X = 1, Y = 0, Width = Dim.Fill(1) };
            left.Add(searchLabel, search, external, offline, internalDevices, virtualDevices, list);
            right.Add(deviceName, connection, details, rename, locate, history, technical, settings, elevate, status);
            Window.Add(left, right, warning, footer);
            list.ValueChanged += (_, e) =>
            {
                if (updating)
                    return;
                selectedId = e.NewValue?.Device.DeviceId;
                ShowDetails();
            };
            list.Accepted += (_, _) => WithDevice(Locations);
            search.ValueChanged += (_, _) => Refresh();
            external.ValueChanged += (_, _) =>
            {
                Refresh();
                Save();
            };
            offline.ValueChanged += (_, _) => Refresh();
            internalDevices.ValueChanged += (_, _) => Refresh();
            virtualDevices.ValueChanged += (_, _) => Refresh();
            rename.Accepted += (_, _) => WithDevice(d => Edit(d));
            locate.Accepted += (_, _) => WithDevice(StartLocate);
            history.Accepted += (_, _) => WithDevice(History);
            technical.Accepted += (_, _) => WithDevice(Technical);
            settings.Accepted += (_, _) => Settings();
            elevate.Accepted += (_, _) => Elevate();
            Window.KeyDown += (_, key) =>
            {
                if (modal)
                    return;
                if (key == Key.F2)
                {
                    ui.Chinese = !ui.Chinese;
                    Save();
                    Translate();
                    Refresh();
                    key.Handled = true;
                }
                else if (key == Key.F5)
                {
                    Scan();
                    key.Handled = true;
                }
            };
            app.ScreenChanged += (_, _) => Layout(app.Driver?.Cols ?? 80, app.Driver?.Rows ?? 24);
            Translate();
            Layout(app.Driver?.Cols ?? 80, app.Driver?.Rows ?? 24);
            Scan();
        }
        private void Save()
        {
            try
            {
                state.SaveSettings(new UserSettings(ui.Chinese ? "zh" : "en", external.Value == CheckState.Checked));
            }
            catch (Exception e)
            {
                Error(e);
            }
        }
        private void Translate()
        {
            searchLabel.Text = T("搜索", "Search");
            external.Title = T("外接/未知", "External/unknown");
            offline.Title = T("离线", "Offline");
            internalDevices.Title = T("内置", "Internal");
            virtualDevices.Title = T("虚拟", "Virtual");
            right.Title = T("设备详情", "Device details");
            warning.Text = T("终端太小，请放大到 80×24；当前状态已保留。",
                             "Terminal too small. Resize to at least 80×24; state is preserved.");
            footer.Text = T("Tab 切换 · ↑↓ 选择 · Enter 查看位置 · F2 语言 · F5 刷新 · Esc 退出",
                            "Tab focus · ↑↓ select · Enter locations · F2 language · F5 refresh · Esc exit");
            rename.Title = T("_改名", "_Rename");
            locate.Title = T("_查找实物", "_Locate");
            history.Title = T("_历史/恢复", "_History");
            technical.Title = T("_技术详情", "_Technical");
            settings.Title = T("_系统设置", "_Settings");
            elevate.Title = T("_管理员重开", "_Elevate");
        }
        private void Layout(int w, int h)
        {
            int width = Math.Max(0, w - 2), height = Math.Max(0, h - 2);
            bool ok = w >= 80 && h >= 24, stacked = width < 108;
            warning.Visible = !ok;
            left.Visible = right.Visible = footer.Visible = ok;
            if (!ok)
                return;
            int lw = stacked ? width : width * 49 / 100;
            left.X = 0;
            left.Y = 0;
            left.Width = lw;
            left.Height = stacked ? 8 : height - 1;
            right.X = stacked ? 0 : lw;
            right.Y = stacked ? 8 : 0;
            right.Width = stacked ? width : width - lw;
            right.Height = stacked ? height - 9 : height - 1;
            footer.Y = height - 1;
        }
        private void Scan()
        {
            try
            {
                Update(inventory.Scan());
            }
            catch (Exception e)
            {
                Error(e);
            }
        }
        public void Update(IReadOnlyList<DeviceSnapshot> value)
        {
            var old = devices.ToDictionary(d => d.DeviceId, d => d.IsPresent, StringComparer.OrdinalIgnoreCase);
            if (hasBaseline)
            {
                var differences =
                    value.Where(d => !old.TryGetValue(d.DeviceId, out var present) || present != d.IsPresent).ToArray();
                var missing =
                    devices.Where(d => !value.Any(v => StringComparer.OrdinalIgnoreCase.Equals(v.DeviceId, d.DeviceId)))
                        .ToArray();
                if (differences.Length > 0 || missing.Length > 0)
                {
                    changes.Clear();
                    foreach (var item in differences)
                        changes[item.DeviceId] = item.IsPresent ? T("[新增]", "[new]") : T("[移除]", "[removed]");
                    if (locateId is null)
                        status.Text =
                            T($"设备枚举变化：{differences.Length} 项更新，{missing.Length} 项消失。",
                              $"Enumeration changed: {differences.Length} updated, {missing.Length} disappeared.");
                }
            }
            hasBaseline = true;
            var previousSelection = Selected;
            if (previousSelection is not null && !value.Any(d => StringComparer.OrdinalIgnoreCase.Equals(d.DeviceId, previousSelection.DeviceId)))
            {
                var missingSelection = previousSelection with
                {
                    IsPresent = false,
                    Functions = previousSelection.Functions.Select(f => f with { IsPresent = false }).ToArray(),
                    Locations = previousSelection.Locations.Select(l => l with { IsPresent = false }).ToArray()
                };
                devices = value.Append(missingSelection).ToArray();
            }
            else
                devices = value;
            displayNames.Clear();
            try
            {
                var historyByLocation = naming.NicknameHistory().ToLookup(entry => entry.LocationId, StringComparer.Ordinal);
                foreach (var device in devices.Where(d => d.IsPresent))
                {
                    var matchingHistory = device.Locations.SelectMany(location => historyByLocation[location.Id]).ToArray();
                    displayNames[device.DeviceId] = PreferredName(device, matchingHistory);
                }
            }
            catch (Exception) { /* A failed history read leaves the current device names visible. */ }
            if (locateId is not null)
            {
                var found = value.FirstOrDefault(d => StringComparer.OrdinalIgnoreCase.Equals(d.DeviceId, locateId));
                if (found is null || !found.IsPresent)
                {
                    locateDisconnected = true;
                    status.Text = T("该条目已从当前枚举消失，等待重新出现。",
                                    "Entry disappeared from the current enumeration; waiting for it to return.");
                }
                else if (locateDisconnected)
                {
                    status.Text = T("相同条目已重新出现；请确认实物。弱身份仅供当前连接确认。",
                                    "Same entry returned; confirm the physical device. Weak identity applies to this " +
                                    "connection only.");
                    selectedId = found.DeviceId;
                    locateId = null;
                }
            }
            Refresh();
        }
        public void Error(Exception e) => status.Text = ui.Reason(NamingService.ErrorCode(e));
        public void CheckControls(string? directory)
        {
            var id = selectedId;
            Update(devices.ToArray());
            if (!StringComparer.OrdinalIgnoreCase.Equals(id, selectedId))
                throw new InvalidOperationException("Selection changed on refresh");
            Layout(79, 23);
            if (!warning.Visible || left.Visible)
                throw new InvalidOperationException("Small terminal state failed");
            Layout(80, 24);
            if (warning.Visible || !left.Visible || !right.Visible)
                throw new InvalidOperationException("Compact layout failed");
            var device = Selected;
            if (device is null)
                return;
            if (device.Locations.Length > 0)
            {
                var location = device.Locations[0] with { IsPresent = true, Name = "Current local name" };
                var displayDevice = device with { Locations = [location] };
                var success = new HistoryEntry("check-name", DateTimeOffset.Now, location.Id, "old", location.Name, "applied");
                var other = location with { Id = location.Id + "-other", Name = "Another local name" };
                var withTwoNames = displayDevice with { Locations = [location, other] };
                var otherSuccess = success with { LocationId = other.Id, ProposedName = other.Name };
                if (PreferredName(withTwoNames, [success, otherSuccess]) != withTwoNames.Name ||
                    PreferredName(displayDevice, [success]) != location.Name ||
                    PreferredName(displayDevice, [success with { ProposedName = "stale" }]) != displayDevice.Name ||
                    PreferredName(displayDevice, [success with { Status = "failed" }]) != displayDevice.Name ||
                    PreferredName(displayDevice, [success with { IsNickname = false }]) != displayDevice.Name)
                    throw new InvalidOperationException("Current local name selection failed");
            }
            var (dialog, edits) = BuildEditor(device);
            var token = app.Begin(dialog);
            if (directory is not null)
            {
                File.WriteAllText(Path.Combine(directory, "narrow-rename.txt"),
                                  "Development-only rendering; real inventory.\n" + app.ToString(),
                                  new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory, "narrow-rename.ansi"), app.Driver!.ToAnsi(),
                                  new UTF8Encoding(false));
            }
            if (edits.Count > 0)
            {
                var original = edits[0].Text.Text;
                edits[0].Text.Text = "Device Radar test";
                var originalDevices = devices.ToArray();
                var disconnected = device with
                {
                    IsPresent = false,
                    Functions = device.Functions.Select(f => f with { IsPresent = false }).ToArray(),
                    Locations = device.Locations.Select(l => l with { IsPresent = false }).ToArray()
                };
                Update(originalDevices.Select(d => StringComparer.OrdinalIgnoreCase.Equals(d.DeviceId, device.DeviceId) ? disconnected : d).ToArray());
                if (selectedId != device.DeviceId || Selected?.IsPresent != false || edits[0].Text.Text != "Device Radar test")
                    throw new InvalidOperationException("Selection or edit changed on disconnect");
                Update(originalDevices.Where(d => !StringComparer.OrdinalIgnoreCase.Equals(d.DeviceId, device.DeviceId)).ToArray());
                if (selectedId != device.DeviceId || Selected?.IsPresent != false)
                    throw new InvalidOperationException("Selection was lost when the device disappeared");
                Update(originalDevices);
                if (selectedId != device.DeviceId || edits[0].Text.Text != "Device Radar test")
                    throw new InvalidOperationException("Selection or edit changed on reconnect");
                app.Driver!.SetScreenSize(100, 24);
                dialog.SetNeedsLayout();
                app.LayoutAndDraw(true);
                if (edits[0].Text.Text != "Device Radar test")
                    throw new InvalidOperationException("Editor state changed during resize");
                edits[0].Text.Text = original;
            }
            if (token is not null)
                app.End(token);

            app.Driver!.SetScreenSize(80, 24);
            var identityWarning = "同一接口和型号不能证明是同一实物。确认对应此记录？";
            var confirm = BuildConfirmation("确认当前实物", "设备与记录\n旧名称 → 新名称\n" + identityWarning);
            var confirmToken = app.Begin(confirm);
            if (!app.ToString().Contains(identityWarning))
                throw new InvalidOperationException("Physical identity warning is not visible at 80x24");
            if (confirmToken is not null) app.End(confirmToken);
            var sample = new HistoryEntry("check", DateTimeOffset.Now, "check", "OLD-NAME-COMPLETE", "NEW-NAME-COMPLETE", "applied");
            var secondSample = sample with { RecordToken = "check2", OriginalName = "OTHER-OLD", ProposedName = "OTHER-NEW" };
            var (historyDialog, historyList, historyDetail) = BuildHistoryDialog([sample, secondSample]);
            var historyToken = app.Begin(historyDialog);
            if (historyList.Value?.Entry != sample || !historyDetail.Text.Contains(sample.OriginalName) ||
                !historyDetail.Text.Contains(sample.ProposedName) || !app.ToString().Contains(sample.OriginalName) ||
                !app.ToString().Contains(sample.ProposedName))
                throw new InvalidOperationException("History detail is not visible at 80x24");
            historyList.Value = new HistoryRow(secondSample, LocationName(secondSample.LocationId), ui.Reason(secondSample.Reason));
            if (!historyDetail.Text.Contains(secondSample.OriginalName) || !historyDetail.Text.Contains(secondSample.ProposedName))
                throw new InvalidOperationException("History detail did not follow selection");
            if (historyToken is not null) app.End(historyToken);

            SessionToken? refreshConfirmToken = null;
            Dialog? refreshConfirm = null;
            var (settingsDialog, refreshButton) = BuildSettingsDialog(() =>
            {
                refreshConfirm = BuildConfirmation("刷新声音设置", "确认关闭并重新打开声音设置");
                refreshConfirmToken = app.Begin(refreshConfirm);
            });
            bool settingsAccepted = false;
            settingsDialog.Accepted += (_, _) => settingsAccepted = true;
            var settingsToken = app.Begin(settingsDialog);
            refreshButton.InvokeCommand(Command.Accept);
            if (settingsAccepted || refreshConfirmToken is null || refreshConfirm is null ||
                !app.ToString().Contains("确认关闭并重新打开声音设置"))
                throw new InvalidOperationException("Refresh confirmation did not open or settings accepted prematurely");
            app.End(refreshConfirmToken);
            if (settingsToken is not null) app.End(settingsToken);
        }
        private DeviceSnapshot? Selected =>
            devices.FirstOrDefault(d => StringComparer.OrdinalIgnoreCase.Equals(d.DeviceId, selectedId));
        private void Refresh()
        {
            var q = search.Text?.Trim() ?? "";
            var filtered =
                devices
                    .Where(d => (d.IsPresent || offline.Value == CheckState.Checked || StringComparer.OrdinalIgnoreCase.Equals(d.DeviceId, selectedId)) &&
                                (external.Value != CheckState.Checked ||
                                 d.Classification is DeviceClassification.External or DeviceClassification.Unknown ||
                                 d.Classification == DeviceClassification.Internal &&
                                     internalDevices.Value == CheckState.Checked ||
                                 d.Classification == DeviceClassification.Virtual &&
                                     virtualDevices.Value == CheckState.Checked) &&
                                (q.Length == 0 || DisplayName(d).Contains(q, StringComparison.CurrentCultureIgnoreCase) || d.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                 d.Connection.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                 d.Locations.Any(l => l.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
                    .ToArray();
            updating = true;
            rows.Clear();
            foreach (var d in filtered)
                rows.Add(new(d, DisplayName(d), (DisplayName(d) != d.Name ? T("[本机名称]", "[local name]") : "") + changes.GetValueOrDefault(d.DeviceId, "")));
            var selected =
                rows.FirstOrDefault(r => StringComparer.OrdinalIgnoreCase.Equals(r.Device.DeviceId, selectedId)) ??
                rows.FirstOrDefault();
            list.Value = selected;
            selectedId = selected?.Device.DeviceId;
            updating = false;
            left.Title = $"{T("设备列表", "Devices")} ({rows.Count})";
            ShowDetails();
        }
        private string DisplayName(DeviceSnapshot device) => displayNames.GetValueOrDefault(device.DeviceId, device.Name);
        private static string PreferredName(DeviceSnapshot device, IReadOnlyList<HistoryEntry> history)
        {
            var matches = (from record in history
                           where record.IsNickname && record.Status is "applied" or "restored" or "already-at-value" or "verified"
                           from location in device.Locations
                           where location.IsPresent && string.Equals(location.Id, record.LocationId, StringComparison.Ordinal)
                               && string.Equals(location.Name, record.ProposedName, StringComparison.Ordinal)
                           select location.Name).Distinct(StringComparer.Ordinal).ToArray();
            return matches.Length == 1 ? matches[0] : device.Name;
        }
        private IdentificationCandidate DisplayCandidate(DeviceSnapshot d) =>
            new(d.Name,
                d.NameSource switch
                {
                    DeviceNameSource.BusReported => T("Windows 总线报告", "Windows bus reported"),
                    DeviceNameSource.AudioEndpoint => T("Windows 声音端点", "Windows audio endpoint"),
                    _ => T("Windows 设备名", "Windows device name")
                },
                IdentificationConfidence.SystemProvided);
        private void ShowDetails()
        {
            var d = Selected;
            if (d is null)
            {
                deviceName.Text = T("没有匹配设备", "No matching device");
                connection.Text = "";
                details.Text = "";
                return;
            }
            var display = DisplayCandidate(d);
            deviceName.Text = UiText.Safe(display.Name);
            connection.Text =
                $"{UiText.Safe(d.Connection)} · {(d.IsPresent ? T("当前枚举", "Enumerated") : T("未枚举，可能已断开", "Not enumerated; possibly disconnected"))} · {UiText.Safe(display.Source)} / {display.Confidence}";
            try
            {
                var caps = naming.Capabilities(d).OrderByDescending(c => c.CanWrite).ToArray();
                details.Text = string.Join(
                    "\n",
                    caps.Take(caps.Length > 3 ? 2 : 3)
                        .Select(
                            c =>
                                $"{ui.Kind(c.Location.Kind)}: {UiText.Safe(c.CurrentName)} {(c.CanWrite ? T("可写", "Writable") : ui.Reason(c.Reason))}"));
                if (caps.Length > 3)
                    details.Text += "\n" + T($"另有 {caps.Length - 2} 个位置，按 Enter 查看全部",
                                             $"{caps.Length - 2} more locations; press Enter");
            }
            catch (Exception e)
            {
                Error(e);
            }
        }
        private void WithDevice(Action<DeviceSnapshot> action)
        {
            var d = Selected;
            if (d is null)
                return;
            try
            {
                modal = true;
                action(d);
            }
            catch (Exception e)
            {
                ShowText(T("操作失败", "Operation failed"), ui.Reason(NamingService.ErrorCode(e)));
            }
            finally
            {
                modal = false;
                Scan();
                RedrawMain();
            }
        }
        private void RedrawMain() => app.Invoke(() => { Window.SetNeedsDraw(); app.LayoutAndDraw(true); });
        private void StartLocate(DeviceSnapshot d)
        {
            if (!d.IsPresent)
            {
                ShowText(T("查找实物", "Locate"), T("请选择当前在线设备。", "Select a connected device."));
                return;
            }
            locateId = d.DeviceId;
            locateDisconnected = false;
            status.Text = T("请断开并重新连接设备，观察同一条目的枚举变化。",
                            "Disconnect and reconnect the device; observe whether the same entry returns.");
        }
        private void Locations(DeviceSnapshot d)
        {
            var caps = naming.Capabilities(d);
            ShowText(
                T("全部命名位置", "All naming locations"),
                string.Join(
                    "\n\n",
                    caps.Select(
                        c =>
                            $"{ui.Kind(c.Location.Kind)}: {UiText.Safe(c.CurrentName)}\n{(c.CanWrite ? T("可写", "Writable") : ui.Reason(c.Reason))} · {(c.CanWrite ? UiText.Safe(c.Evidence) : "")}")));
        }
        private void Technical(DeviceSnapshot d)
        {
            var caps = naming.Capabilities(d);
            ShowText(
                T("技术详情（仅本机）", "Technical details (local only)"),
                $"{UiText.Safe(d.Name)}\n{UiText.Safe(d.DeviceId)}\nIdentity: {d.Identity.Strength}\nName source: {d.NameSource} · {UiText.Safe(d.NameSourceTargetId)}\n{string.Join("\n", d.HardwareIds.Select(UiText.Safe))}\n\n{string.Join("\n", caps.Select(c => $"{ui.Kind(c.Location.Kind)}: {UiText.Safe(c.CurrentName)} | {(c.CanWrite ? T("可写", "Writable") : ui.Reason(c.Reason))} | {(c.CanWrite ? UiText.Safe(c.Evidence) : "")}"))}");
        }
        private void ShowText(string title, string text)
        {
            var d = Dialog(title, 20);
            var view =
                new TextView { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1), Text = text, ReadOnly = true, WordWrap = true };
            d.Add(view);
            CloseButton(d, T("_关闭", "_Close"));
            app.Run(d);
        }
        private Dialog BuildConfirmation(string title, string text)
        {
            var d = Dialog(title, 15);
            d.Add(new TextView { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1), Text = text, ReadOnly = true, WordWrap = true });
            d.AddButton(Button(T("_取消", "_Cancel")));
            d.AddButton(Button(T("_确认", "_Confirm")));
            return d;
        }
        private bool Confirm(string title, string text)
        {
            var d = BuildConfirmation(title, text);
            app.Run(d);
            return d.Result == 1;
        }
        private (Dialog, List<EditorRow>) BuildEditor(DeviceSnapshot device)
        {
            var caps = naming.Capabilities(device);
            var d = Dialog(T("修改名称", "Rename") + " · " + UiText.Safe(device.Name), 20);
            var scroll = Scroll(1, 0, 12, Math.Max(12, caps.Count * 3 + 1));
            var editors = new List<EditorRow>();
            for (int x = 0; x < caps.Count; x++)
            {
                var c = caps[x];
                int y = x * 3;
                var check = new CheckBox
                {
                    X = 1,
                    Y = y,
                    Title = $"{ui.Kind(c.Location.Kind)}  {(c.CanWrite ? "" : ui.Reason(c.Reason))}",
                    Value = c.CanWrite ? CheckState.Checked : CheckState.UnChecked,
                    Enabled = c.CanWrite
                };
                var text = new TextField
                {
                    X = 12,
                    Y = y + 2,
                    Width = Dim.Fill(2),
                    Height = 1,
                    Text = UiText.Safe(c.CurrentName),
                    Enabled = c.CanWrite
                };
                void Reveal()
                {
                    int bottom = y + 2;
                    if (y < scroll.Viewport.Y)
                        scroll.ScrollVertical(y - scroll.Viewport.Y);
                    else if (bottom >= scroll.Viewport.Y + scroll.Viewport.Height)
                        scroll.ScrollVertical(bottom - (scroll.Viewport.Y + scroll.Viewport.Height) + 1);
                }
                check.HasFocusChanged += (_, _) =>
                {
                    if (check.HasFocus)
                        Reveal();
                };
                text.HasFocusChanged += (_, _) =>
                {
                    if (text.HasFocus)
                        Reveal();
                };
                scroll.Add(check,
                           new Label
                           {
                               X = 3,
                               Y = y + 1,
                               Width = Dim.Fill(2),
                               Text = T("当前：", "Current: ") + UiText.Safe(c.CurrentName)
                           },
                           new Label { X = 3, Y = y + 2, Text = T("新名称：", "New: ") }, text);
                editors.Add(new(c, check, text));
            }
            d.Add(scroll);
            var candidate = Button(T("_候选名称", "_Candidates"));
            candidate.Accepting += (_, e) =>
            {
                e.Handled = true;
                ChooseCandidate(device, editors);
            };
            d.Add(candidate);
            candidate.X = 2;
            candidate.Y = 12;
            d.AddButton(Button(T("_取消", "_Cancel")));
            d.AddButton(Button(T("_预览修改", "_Preview")));
            return (d, editors);
        }
        private void Edit(DeviceSnapshot device)
        {
            var (d, editors) = BuildEditor(device);
            while (true)
            {
                app.Run(d);
                if (d.Result != 1)
                    return;
                try
                {
                    var edits = editors.Where(x => x.Check.Value == CheckState.Checked)
                                    .Select(x => new NameEdit(x.Capability.LocationId, x.Text.Text?.Trim() ?? ""))
                                    .ToArray();
                    if (edits.Length == 0)
                    {
                        ShowText(T("提示", "Notice"),
                                 T("请至少选择一个可写位置。", "Select at least one writable location."));
                        continue;
                    }
                    var preview = naming.Preview(device, edits);
                    if (!Preview(preview, false))
                        continue;
                    var result = naming.Apply(naming.Prepare(preview));
                    Result(result);
                    return;
                }
                catch (Exception e)
                {
                    ShowText(T("操作失败", "Operation failed"), ui.Reason(NamingService.ErrorCode(e)));
                }
            }
        }
        private void ChooseCandidate(DeviceSnapshot d, List<EditorRow> editors)
        {
            var choices = identify.Identify(d.HardwareIds, d.Locations.Select(x => x.Name).Prepend(d.Name));
            var dialog = Dialog(T("候选名称", "Candidates"), 18);
            var source = new ObservableCollection<CandidateRow>(choices.Select(c => new CandidateRow(c)));
            var list = new ListView<CandidateRow> { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1) };
            list.SetSource(source);
            dialog.Add(list);
            dialog.AddButton(Button(T("_取消", "_Cancel")));
            dialog.AddButton(Button(T("_填入选中位置", "_Use for selected locations")));
            app.Run(dialog);
            if (dialog.Result == 1 && list.Value is { } selected)
                foreach (var e in editors.Where(e => e.Check.Value == CheckState.Checked))
                    e.Text.Text = selected.Candidate.Name;
        }
        private bool Preview(NamingPreview preview, bool restore)
        {
            var d = Dialog(restore ? T("恢复预览", "Restore preview") : T("修改预览", "Preview"), 19);
            var msg = restore ? T("当前名称可能已被外部修改，请逐项核对后确认。",
                                  "Current names may have changed externally; review each entry.")
                              : T("确认后逐项写入，并记录结果。",
                                  "Confirm to write each selected location and record the result.");
            var text =
                msg + "\n\n" +
                string.Join(
                    "\n",
                    preview.Entries.Select(
                        e =>
                            $"{ui.Kind(e.Kind)}: {UiText.Safe(e.OriginalName)} → {UiText.Safe(e.NewName)}\n  {UiText.Safe(e.Method)} · {UiText.Safe(e.Evidence)}"));
            d.Add(
                new TextView { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1), ReadOnly = true, WordWrap = true, Text = text });
            d.AddButton(Button(T("_返回", "_Back")));
            d.AddButton(Button(T("_确认写入", "_Apply")));
            app.Run(d);
            return d.Result == 1;
        }
        private void Result(NamingResult result)
        {
            var text = string.Join(
                "\n\n",
                result.Locations.Select(
                    l =>
                        $"{LocationName(l.LocationId)}\n{T("状态", "Status")}: {UiText.Safe(l.Status)} · API: {l.ApiSucceeded?.ToString() ?? "—"} · {T("回读", "Readback")}: {UiText.Safe(l.ObservedState)} · {UiText.Safe(l.ObservedName)}\n{T("同步", "Sync")}: {UiText.Safe(l.SynchronizedState)} · {T("界面", "UI")}: {UiText.Safe(l.InterfaceVerification)} · {T("重连", "Reconnect")}: {UiText.Safe(l.ReconnectVerification)}\n{ui.Reason(l.Reason)}"));
            ShowText(T("修改结果", "Result"), text);
        }
        private string LocationName(string id)
        {
            var location = Selected?.Locations.FirstOrDefault(l => StringComparer.OrdinalIgnoreCase.Equals(l.Id, id));
            return location is null ? T("命名位置", "Naming location") : ui.Kind(location.Kind);
        }
        private (Dialog Dialog, ListView<HistoryRow> List, TextView Detail) BuildHistoryDialog(IEnumerable<HistoryEntry> entries)
        {
            var dialog = Dialog(T("本机当前连接的候选记录 / 恢复", "Local connection candidate history / Restore"), 20);
            var source = new ObservableCollection<HistoryRow>(
                entries.Select(e => new HistoryRow(e, LocationName(e.LocationId), ui.Reason(e.Reason))));
            var list = new ListView<HistoryRow> { X = 1, Y = 1, Width = Dim.Fill(1), Height = 5 };
            var detail = new TextView { X = 1, Y = 7, Width = Dim.Fill(1), Height = Dim.Fill(1), ReadOnly = true, WordWrap = true };
            void UpdateDetail(HistoryRow? row)
            {
                detail.Text = row is null ? T("没有历史记录", "No history records") :
                    $"{T("位置", "Location")}: {row.Location} · {T("状态", "Status")}: {UiText.Safe(row.Entry.Status)}\n" +
                    $"{T("旧名称", "Previous name")}: {UiText.Safe(row.Entry.OriginalName)}\n" +
                    $"{T("新名称", "New name")}: {UiText.Safe(row.Entry.ProposedName)}\n{row.Reason}";
            }
            list.SetSource(source);
            list.ValueChanged += (_, e) => UpdateDetail(e.NewValue);
            if (list.Value is null && source.Count > 0) list.Value = source[0];
            UpdateDetail(list.Value);
            dialog.Add(list, detail);
            dialog.AddButton(Button(T("_关闭", "_Close")));
            dialog.AddButton(Button(T("_恢复预览", "_Restore preview")));
            return (dialog, list, detail);
        }
        private void History(DeviceSnapshot device)
        {
            var (dialog, list, _) = BuildHistoryDialog(naming.History(device));
            app.Run(dialog);
            if (dialog.Result != 1 || list.Value is null)
                return;
            var selected = list.Value.Entry;
            if (device.Identity.Strength == IdentityStrength.Weak &&
                !Confirm(
                    T("确认当前实物", "Confirm physical device"),
                    T($"同一接口和型号不能证明是同一实物。确认对应此记录？\n\n{UiText.Safe(device.Name)} · {selected.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n{UiText.Safe(selected.OriginalName)} → {UiText.Safe(selected.ProposedName)}",
                      $"Same port and model do not prove the same unit. Confirm this record?\n\n{UiText.Safe(device.Name)} · {selected.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n{UiText.Safe(selected.OriginalName)} → {UiText.Safe(selected.ProposedName)}")))
                return;
            var preview = naming.PreviewRestore(selected.RecordToken);
            if (Preview(preview, true))
                Result(naming.Apply(naming.Prepare(preview)));
        }
        private (Dialog Dialog, Button Refresh) BuildSettingsDialog(Action refreshAction)
        {
            var d = Dialog(T("Windows 设置", "Windows settings"), 12);
            var sound = Button(T("_声音", "_Sound"), 1, 1);
            var bluetooth = Button(T("_蓝牙", "_Bluetooth"), 1, 3);
            var dev = Button(T("_设备管理器", "_Device Manager"), 1, 5);
            var refresh = Button(T("_刷新声音设置", "_Refresh sound settings"), 24, 1);
            refresh.Accepting += (_, e) => { e.Handled = true; refreshAction(); };
            sound.Accepting += (_, e) => { e.Handled = true; Open("ms-settings:sound"); };
            bluetooth.Accepting += (_, e) => { e.Handled = true; Open("ms-settings:bluetooth"); };
            dev.Accepting += (_, e) => { e.Handled = true; Open("devmgmt.msc"); };
            d.Add(sound, bluetooth, dev, refresh);
            CloseButton(d, T("_关闭", "_Close"));
            return (d, refresh);
        }
        private void Settings()
        {
            modal = true;
            try
            {
                var (d, _) = BuildSettingsDialog(RefreshSound);
                app.Run(d);
            }
            finally
            {
                modal = false;
                RedrawMain();
            }
        }
        private void RefreshSound()
        {
            if (!Confirm(T("刷新声音设置", "Refresh sound settings"),
                         T("将关闭已打开的 Windows 设置窗口并重新打开声音设置。",
                           "Close open Windows Settings windows and reopen Sound settings.")))
                return;
            try
            {
                var result = naming.RefreshSoundSettings();
                ShowText(T("刷新结果", "Refresh result"),
                         $"{UiText.Safe(result.RefreshState)} · {UiText.Safe(result.Reason)}");
            }
            catch (Exception e)
            {
                ShowText(T("操作失败", "Operation failed"), ui.Reason(NamingService.ErrorCode(e)));
            }
        }
        private void Open(string target)
        {
            if (target is not ("ms-settings:sound" or "ms-settings:bluetooth" or "devmgmt.msc"))
                return;
            try
            {
                var file = target == "devmgmt.msc" ? Path.Combine(Environment.SystemDirectory, "devmgmt.msc") : target;
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ShowText(T("打开失败", "Open failed"), ui.Reason(NamingService.ErrorCode(e)));
            }
        }
        private void Elevate()
        {
            if (Runtime.IsAdministrator)
            {
                ShowText(T("管理员", "Administrator"), T("当前已是管理员。", "Already running as administrator."));
                return;
            }
            modal = true;
            try
            {
                if (Confirm(T("管理员重开", "Restart as administrator"),
                            T("将请求 Windows 管理员权限并重新打开程序。",
                              "Request Windows administrator permission and restart the application.")) &&
                    Runtime.RequestElevation())
                    app.RequestStop();
            }
            catch (Exception e)
            {
                Error(e);
            }
            finally
            {
                modal = false;
                RedrawMain();
            }
        }
    }

    public static void SelfCheck()
    {
        Capture(null);
    }
    public static int WriteSnapshots(string directory)
    {
        Directory.CreateDirectory(directory);
        Capture(directory);
        return 0;
    }
    private static void Capture(string? directory)
    {
        Theme();
        var old = Console.Out;
        var env = Environment.GetEnvironmentVariable("DisableRealDriverIO");
        Console.SetOut(TextWriter.Null);
        Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");
        try
        {
            using IApplication app = Application.Create().Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(80, 24);
            var inv = new InventoryService();
            var state = new PortableState();
            var main = new Main(app, inv, new NamingService(), state, new UiText("zh"),
                                new IdentificationService(Runtime.BaseDirectory), state.LoadSettings());
            var token = app.Begin(main.Window);
            var text = app.ToString();
            var ansi = app.Driver.ToAnsi();
            main.CheckControls(directory);
            if (!text.Contains("Device Radar"))
                throw new InvalidOperationException("TUI render failed");
            if (directory is not null)
            {
                File.WriteAllText(Path.Combine(directory, "main.txt"),
                                  "Development-only rendering; real inventory.\n" + text, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory, "main.ansi"), ansi, new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(directory, "manifest.json"),
                    "{\"description\":\"Development-only rendering with real local inventory; no example " +
                    "data\",\"snapshots\":[{\"state\":\"main\",\"columns\":80,\"rows\":24,\"ansiFile\":\"main.ansi\"," +
                    "\"textFile\":\"main.txt\"},{\"state\":\"narrow-rename\",\"columns\":80,\"rows\":24,\"ansiFile\":" +
                    "\"narrow-rename.ansi\",\"textFile\":\"narrow-rename.txt\"}]}",
                    new UTF8Encoding(false));
            }
            if (token is not null)
                app.End(token);
        }
        finally
        {
            Console.SetOut(old);
            Environment.SetEnvironmentVariable("DisableRealDriverIO", env);
        }
    }
}

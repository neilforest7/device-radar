using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

internal static class Program
{
    private const string SchemeName = "DeviceRadarLight";

    private static int Main(string[] args)
    {
        RegisterTheme();

        if (args.Contains("--self-check", StringComparer.Ordinal))
        {
            return RunSelfCheck();
        }

        int snapshotArg = Array.IndexOf(args, "--snapshot-dir");
        if (snapshotArg >= 0)
        {
            if (snapshotArg + 1 >= args.Length)
            {
                Console.Error.WriteLine("Usage: DeviceRadar.Preview --snapshot-dir <dir>");
                return 2;
            }

            return WriteSnapshots(args[snapshotArg + 1]);
        }

        using IApplication app = Application.Create().Init(DriverRegistry.Names.ANSI);
        List<DeviceSample> samples = CreateSamples();
        MainView main = BuildMain(app, samples, samples[0], app.Driver!.Cols, app.Driver.Rows);
        app.Run(main.Window);
        return 0;
    }

    private static void RegisterTheme()
    {
        SchemeManager.AddScheme(SchemeName, new Scheme
        {
            Normal = new TgAttribute(new Color(35, 41, 49), new Color(250, 251, 252)),
            Focus = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
            HotFocus = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
            Active = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
            HotActive = new TgAttribute(new Color(255, 255, 255), new Color(0, 0, 170)),
            Editable = new TgAttribute(new Color(35, 41, 49), new Color(255, 255, 255))
        });
    }

    private static int WriteSnapshots(string directory)
    {
        Directory.CreateDirectory(directory);
        var snapshots = new List<SnapshotInfo>();
        snapshots.Add(WriteSnapshot(directory, "main", "主界面 · 宽屏", 144, 24, SnapshotState.Main));
        snapshots.Add(WriteSnapshot(directory, "rename", "改名对话框 · 宽屏", 144, 24, SnapshotState.Rename));
        snapshots.Add(WriteSnapshot(directory, "preview", "修改预览 · 宽屏", 144, 24, SnapshotState.Preview));
        snapshots.Add(WriteSnapshot(directory, "result", "部分完成结果 · 宽屏", 144, 24, SnapshotState.Result));
        snapshots.Add(WriteSnapshot(directory, "narrow", "主界面 · 80×24", 80, 24, SnapshotState.Narrow));
        snapshots.Add(WriteSnapshot(directory, "narrow-rename", "改名对话框 · 80×24", 80, 24, SnapshotState.NarrowRename));
        var manifest = new { snapshots };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }), new UTF8Encoding(false));
        return 0;
    }

    private static SnapshotInfo WriteSnapshot(string directory, string state, string title, int columns, int rows, SnapshotState snapshotState)
    {
        CapturedFrame frame = CaptureFrame(columns, rows, snapshotState);
        string ansiFile = $"{state}.ansi";
        string textFile = $"{state}.txt";
        File.WriteAllText(Path.Combine(directory, ansiFile), frame.Ansi, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, textFile), frame.Text, new UTF8Encoding(false));
        return new SnapshotInfo(state, title, columns, rows, ansiFile, textFile);
    }

    private static CapturedFrame CaptureFrame(int columns, int rows, SnapshotState state)
    {
        TextWriter originalOut = Console.Out;
        string? originalDriverIo = Environment.GetEnvironmentVariable("DisableRealDriverIO");
        Console.SetOut(TextWriter.Null);
        Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");

        try
        {
            using IApplication app = Application.Create().Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(columns, rows);
            List<DeviceSample> samples = CreateSamples();
            DeviceSample selected = samples[0];
            if (state == SnapshotState.Result)
            {
                ApplySimulatedWrite(selected, [true, true, true], selected.Slots.Select(slot => slot.SuggestedName).ToArray());
            }

            MainView main = BuildMain(app, samples, selected, columns, rows);
            SessionToken? mainToken = app.Begin(main.Window);
            SessionToken? modalToken = null;

            Dialog? dialog = state switch
            {
                SnapshotState.Rename or SnapshotState.NarrowRename => BuildRenameDialog(selected, columns, rows, out _),
                SnapshotState.Preview => BuildPreviewDialog(selected, selected.Slots.Select(slot => new NameEdit(slot, true, slot.SuggestedName)).ToList(), columns, rows),
                SnapshotState.Result => BuildResultDialog(selected, columns, rows),
                _ => null
            };
            if (dialog is not null)
            {
                modalToken = app.Begin(dialog);
            }

            string ansi = app.Driver.ToAnsi();
            string text = app.ToString();
            if (modalToken is not null)
            {
                app.End(modalToken);
            }
            if (mainToken is not null)
            {
                app.End(mainToken);
            }
            return new CapturedFrame(ansi, text);
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("DisableRealDriverIO", originalDriverIo);
        }
    }

    private static MainView BuildMain(IApplication app, List<DeviceSample> allDevices, DeviceSample initialSelection, int columns, int rows)
    {
        var window = new Window
        {
            Title = "Device Radar · 界面样稿 / 示例数据",
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = SchemeName
        };
        var footer = new Label { Text = "Tab 切换 · ↑↓ 选择设备 · Enter 确认 · 鼠标可用", X = 1, Y = 0, Width = Dim.Fill(1) };
        var smallWarning = new Label { Text = "终端太小，请放大到 80×24；当前筛选与名称状态已保留。", X = 1, Y = 0, Width = Dim.Fill(1), Height = 2, Visible = false };
        var listFrame = new FrameView { Title = "设备列表", SchemeName = SchemeName, BorderStyle = LineStyle.Single };
        var detailFrame = new FrameView { Title = "设备详情", SchemeName = SchemeName, BorderStyle = LineStyle.Single };
        var searchLabel = new Label { Text = "搜索", X = 1, Y = 0 };
        var search = new TextField { X = 8, Y = 0, Width = Dim.Fill(1), Height = 1 };
        var externalOnly = new CheckBox { Title = "仅显示外接设备", Value = CheckState.Checked, X = 1, Y = 1 };
        var visibleDevices = new ObservableCollection<DeviceSample>();
        var list = new ListView<DeviceSample> { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(), SchemeName = SchemeName };
        list.SetSource(visibleDevices);
        var name = new Label { X = 1, Y = 0, Width = Dim.Fill(1) };
        var connection = new Label { X = 1, Y = 1, Width = Dim.Fill(1) };
        var section = new Label { Text = "Windows 命名位置", X = 1, Y = 2, Width = Dim.Fill(1) };
        var slotLines = Enumerable.Range(0, 3)
            .Select(index => new Label { X = 1, Y = 3 + index, Width = Dim.Fill(1) })
            .ToArray();
        var status = new Label { Text = "名称分别保存；未完成项可重试或恢复。", X = 1, Y = 7, Width = Dim.Fill(1), Height = 1 };
        DeviceSample? selected = initialSelection;

        var renameButton = FlatButton("_改名", 1, 6);
        renameButton.Accepted += (_, _) =>
        {
            if (selected is not null)
            {
                OpenRenameFlow(app, selected, () => UpdateDetails(selected));
            }
        };
        var locateButton = FlatButton("_查找实物", 12, 6);
        locateButton.Accepted += (_, _) => status.Text = "真实流程会引导拔插并观察条目变化。";
        var restoreButton = FlatButton("_恢复原名", 28, 6);
        restoreButton.Accepted += (_, _) =>
        {
            if (selected is null)
            {
                return;
            }
            RestoreOriginalNames(selected);
            status.Text = "已恢复为示例原名；无系统操作。";
            UpdateDetails(selected);
        };
        listFrame.Add(searchLabel, search, externalOnly, list);
        detailFrame.Add(name, connection, section, slotLines[0], slotLines[1], slotLines[2],
                        renameButton, locateButton, restoreButton, status);
        window.Add(smallWarning, footer, listFrame, detailFrame);

        void UpdateDetails(DeviceSample? device)
        {
            if (device is null)
            {
                name.Text = "没有匹配设备";
                connection.Text = "调整搜索或筛选条件";
                foreach (Label line in slotLines)
                {
                    line.Text = string.Empty;
                }
                return;
            }

            name.Text = device.DisplayName;
            connection.Text = $"{device.Connection} · {device.State}";
            for (int i = 0; i < slotLines.Length; i++)
            {
                slotLines[i].Text = i < device.Slots.Count
                    ? $"{device.Slots[i].Feature}（{device.Slots[i].Location}）  {device.Slots[i].CurrentName}"
                    : string.Empty;
            }
        }

        void RefreshList()
        {
            string query = search.Text?.Trim() ?? string.Empty;
            bool externalFilter = externalOnly.Value == CheckState.Checked;
            List<DeviceSample> filtered = allDevices
                .Where(device => !externalFilter || device.IsExternal)
                .Where(device => query.Length == 0 ||
                                 device.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 device.Connection.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 device.Slots.Any(slot => slot.CurrentName.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();
            visibleDevices.Clear();
            foreach (DeviceSample device in filtered)
            {
                visibleDevices.Add(device);
            }

            if (selected is null || !visibleDevices.Contains(selected))
            {
                selected = visibleDevices.FirstOrDefault();
            }
            list.Value = selected;
            listFrame.Title = $"设备列表（{filtered.Count}）";
            UpdateDetails(selected);
        }

        list.Value = initialSelection;
        list.ValueChanged += (_, args) =>
        {
            selected = args.NewValue;
            UpdateDetails(selected);
        };
        search.ValueChanged += (_, _) => RefreshList();
        externalOnly.ValueChanged += (_, _) => RefreshList();
        RefreshList();

        void ApplyLayout(int width, int height)
        {
            int usableWidth = Math.Max(0, width - 2);
            int usableHeight = Math.Max(0, height - 2);
            bool supported = width >= 80 && height >= 24;
            smallWarning.Visible = !supported;
            listFrame.Visible = supported;
            detailFrame.Visible = supported;
            footer.Visible = supported;
            smallWarning.Width = usableWidth;
            smallWarning.Y = 0;
            footer.Width = usableWidth;
            if (!supported)
            {
                UpdateDetails(selected);
                return;
            }

            bool stacked = usableWidth < 108;
            int listWidth = stacked ? usableWidth : usableWidth * 49 / 100;
            listFrame.X = 0;
            listFrame.Y = 0;
            listFrame.Width = listWidth;
            listFrame.Height = stacked ? 8 : usableHeight - 1;
            if (stacked)
            {
                detailFrame.X = 0;
                detailFrame.Y = 8;
                detailFrame.Width = usableWidth;
                detailFrame.Height = usableHeight - 1 - (stacked ? 8 : 0);
                renameButton.X = 1;
                locateButton.X = 11;
                restoreButton.X = 29;
                footer.Y = usableHeight - 1;
            }
            else
            {
                detailFrame.X = listWidth;
                detailFrame.Y = 0;
                detailFrame.Width = usableWidth - listWidth;
                detailFrame.Height = usableHeight - 1 - (stacked ? 8 : 0);
                renameButton.X = 1;
                locateButton.X = 12;
                restoreButton.X = 28;
                footer.Y = usableHeight - 1;
            }
            UpdateDetails(selected);
        }

        ApplyLayout(columns, rows);
        app.ScreenChanged += (_, _) => ApplyLayout(app.Driver?.Cols ?? columns, app.Driver?.Rows ?? rows);
        UpdateDetails(selected);
        return new MainView(window, list, search, externalOnly, status, UpdateDetails, searchLabel,
            listFrame, detailFrame, smallWarning, slotLines, [renameButton, locateButton, restoreButton]);
    }
    private static Dialog BuildRenameDialog(DeviceSample device, int columns, int rows, out List<NameEdit> edits)
    {
        var dialog = new Dialog
        {
            Title = $"修改名称 · {device.DisplayName}",
            SchemeName = SchemeName,
            BorderStyle = LineStyle.Single
        };
        FitDialogToTerminal(dialog, columns, rows, 6 + device.Slots.Count * 4);
        edits = [];
        for (int i = 0; i < device.Slots.Count; i++)
        {
            NameSlot slot = device.Slots[i];
            int y = 1 + i * 4;
            var enabled = new CheckBox { Title = $"{slot.Feature} · {slot.Location}", Value = CheckState.Checked, X = 2, Y = y };
            var name = new TextField { Text = slot.SuggestedName, X = 15, Y = y + 2, Width = Dim.Fill(3) };
            dialog.Add(enabled,
                       new Label { Text = $"当前：{slot.CurrentName}", X = 4, Y = y + 1 },
                       new Label { Text = "新名称：", X = 4, Y = y + 2 },
                       name);
            edits.Add(new NameEdit(slot, true, slot.SuggestedName, enabled, name));
        }
        dialog.AddButton(FlatButton("_取消"));
        dialog.AddButton(FlatButton("_预览修改"));
        return dialog;
    }

    private static Dialog BuildPreviewDialog(DeviceSample device, List<NameEdit> edits, int columns, int rows)
    {
        var dialog = new Dialog
        {
            Title = "修改预览",
            SchemeName = SchemeName,
            BorderStyle = LineStyle.Single
        };
        FitDialogToTerminal(dialog, columns, rows, 9 + edits.Count);
        dialog.Add(new Label { Text = $"设备：{device.DisplayName} · {device.Connection}", X = 2, Y = 1 });
        dialog.Add(new Label { Text = "确认后模拟写入；成功修改保留，失败项逐项报告。", X = 2, Y = 2 });
        int y = 3;
        foreach (NameEdit edit in edits)
        {
            string line = edit.Enabled
                ? $"{edit.Slot.Feature}：{edit.Slot.CurrentName}  →  {edit.Name}"
                : $"{edit.Slot.Feature}：跳过（{edit.Slot.CurrentName}）";
            dialog.Add(new Label { Text = line, X = 2, Y = y, Width = Dim.Fill(3) });
            y++;
        }
        dialog.AddButton(FlatButton("_返回编辑"));
        dialog.AddButton(FlatButton("_模拟写入"));
        return dialog;
    }

    private static Dialog BuildResultDialog(DeviceSample device, int columns, int rows)
    {
        var dialog = new Dialog
        {
            Title = "修改结果",
            SchemeName = SchemeName,
            BorderStyle = LineStyle.Single
        };
        FitDialogToTerminal(dialog, columns, rows, 10 + device.Slots.Count);
        int completed = device.Slots.Count(slot => slot.Status.StartsWith("已保存 · 回读一致", StringComparison.Ordinal));
        int failed = device.Slots.Count(slot => slot.Status.StartsWith("未完成", StringComparison.Ordinal));
        dialog.Title = failed > 0
            ? completed > 0 ? "修改结果 · 部分完成" : "修改结果 · 未完成"
            : completed > 0 ? "修改结果 · 已完成" : "修改结果 · 未修改";
        var message = new Label
        {
            Text = $"{(completed > 0 ? "成功修改已保留" : "没有成功修改")}；已保存 {completed} 项，未完成 {failed} 项。原名可恢复。",
            X = 2,
            Y = 1,
            Width = Dim.Fill(3),
            Height = 1
        };
        dialog.Add(message);
        int y = 3;
        foreach (NameSlot slot in device.Slots)
        {
            dialog.Add(new Label { Text = $"{slot.Feature} · {slot.Location}：{slot.Status}", X = 2, Y = y, Width = Dim.Fill(3) });
            y++;
        }
        var soundHelp = FlatButton("_声音设置", 2, y);
        soundHelp.Accepting += (_, e) =>
        {
            e.Handled = true;
            message.Text = "声音名称由 Windows 声音设置管理；此样稿没有启动系统设置。";
        };
        dialog.Add(soundHelp);
        dialog.AddButton(FlatButton("_关闭"));
        return dialog;
    }

    private static void FitDialogToTerminal(Dialog dialog, int columns, int rows, int preferredHeight)
    {
        dialog.Width = Dim.Func(_ => Math.Min(100, Math.Max(1, (dialog.App?.Driver?.Cols ?? columns) - 4)));
        dialog.Height = Dim.Func(_ => Math.Min(preferredHeight, Math.Max(1, (dialog.App?.Driver?.Rows ?? rows) - 4)));
    }

    private static Button FlatButton(string title, int? x = null, int? y = null) => new()
    {
        Title = title,
        X = x ?? 0,
        Y = y ?? 0,
        ShadowStyle = ShadowStyles.None
    };

    private static void OpenRenameFlow(IApplication app, DeviceSample device, Action refresh)
    {
        Dialog editDialog = BuildRenameDialog(device, app.Driver?.Cols ?? 144, app.Driver?.Rows ?? 40, out List<NameEdit> edits);
        while (true)
        {
            app.Run(editDialog);
            if (editDialog.Result != 1)
            {
                return;
            }

            foreach (NameEdit edit in edits)
            {
                edit.Enabled = edit.CheckBox?.Value == CheckState.Checked;
                edit.Name = edit.TextField?.Text?.Trim() ?? string.Empty;
            }
            Dialog preview = BuildPreviewDialog(device, edits, app.Driver?.Cols ?? 144, app.Driver?.Rows ?? 40);
            app.Run(preview);
            if (preview.Result == 0)
            {
                continue;
            }
            if (preview.Result != 1)
            {
                return;
            }

            ApplySimulatedWrite(device,
                edits.Select(edit => edit.Enabled).ToArray(),
                edits.Select(edit => edit.Name).ToArray());
            Dialog result = BuildResultDialog(device, app.Driver?.Cols ?? 144, app.Driver?.Rows ?? 40);
            app.Run(result);
            refresh();
            return;
        }
    }

    private static void ApplySimulatedWrite(DeviceSample device, bool[] enabled, string[] names)
    {
        for (int i = 0; i < device.Slots.Count; i++)
        {
            NameSlot slot = device.Slots[i];
            if (i >= enabled.Length || !enabled[i])
            {
                slot.Status = "跳过";
                continue;
            }
            if (i >= names.Length || string.IsNullOrWhiteSpace(names[i]))
            {
                slot.Status = "未完成；名称不能为空，当前名保留";
                continue;
            }
            if (slot.Feature == "麦克风")
            {
                slot.Status = "未完成；当前名保留";
                continue;
            }
            slot.CurrentName = names[i];
            slot.Status = $"已保存 · 回读一致 · {slot.CurrentName}";
        }
    }

    private static void RestoreOriginalNames(DeviceSample device)
    {
        foreach (NameSlot slot in device.Slots)
        {
            slot.CurrentName = slot.OriginalName;
            slot.Status = "已恢复示例原名";
        }
    }

    private static int RunSelfCheck()
    {
        TextWriter originalOut = Console.Out;
        string? originalDriverIo = Environment.GetEnvironmentVariable("DisableRealDriverIO");
        Console.SetOut(TextWriter.Null);
        Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");
        try
        {
            using IApplication app = Application.Create().Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(144, 40);

            List<DeviceSample> allSelected = CreateSamples();
            DeviceSample allSelectedC920 = allSelected[0];
            string[] suggested = allSelectedC920.Slots.Select(slot => slot.SuggestedName).ToArray();
            ApplySimulatedWrite(allSelectedC920, [true, true, true], suggested);
            Require(CountSaved(allSelectedC920) == 2 && CountFailed(allSelectedC920) == 1, "all selected should yield two saved and one failed");
            Require(allSelectedC920.Slots[1].CurrentName == allSelectedC920.Slots[1].OriginalName, "failed mock write must keep the original microphone name");
            Require(allSelectedC920.Slots[0].Status.StartsWith("已保存 · 回读一致", StringComparison.Ordinal), "successful item should report read-back consistency");

            List<DeviceSample> oneSkipped = CreateSamples();
            ApplySimulatedWrite(oneSkipped[0], [true, false, true], oneSkipped[0].Slots.Select(slot => slot.SuggestedName).ToArray());
            Require(CountSaved(oneSkipped[0]) == 2 && CountFailed(oneSkipped[0]) == 0, "canceling one item should exclude it from result counts");

            List<DeviceSample> emptyName = CreateSamples();
            ApplySimulatedWrite(emptyName[0], [true, false, false], ["   ", "unused", "unused"]);
            Require(CountSaved(emptyName[0]) == 0 && CountFailed(emptyName[0]) == 1, "an empty name should count as one incomplete item");
            Require(emptyName[0].Slots[0].CurrentName == emptyName[0].Slots[0].OriginalName, "an empty name must not change the current name");

            RestoreOriginalNames(allSelectedC920);
            Require(allSelectedC920.Slots.All(slot => slot.CurrentName == slot.OriginalName), "restore should return every mock name to its original value");

            DeviceSample dialogDevice = CreateSamples()[0];
            Dialog editorCancel = BuildRenameDialog(dialogDevice, 144, 40, out _);
            editorCancel.Buttons[0].InvokeCommand(Command.Accept);
            Require(editorCancel.Result == 0, "rename dialog cancel button must return index 0");
            Dialog editorPreview = BuildRenameDialog(dialogDevice, 144, 40, out _);
            editorPreview.Buttons[1].InvokeCommand(Command.Accept);
            Require(editorPreview.Result == 1, "rename dialog preview button must return index 1");

            var edits = dialogDevice.Slots.Select(slot => new NameEdit(slot, true, slot.SuggestedName)).ToList();
            Dialog previewBack = BuildPreviewDialog(dialogDevice, edits, 144, 40);
            previewBack.Buttons[0].InvokeCommand(Command.Accept);
            Require(previewBack.Result == 0, "preview back button must return index 0");
            Dialog previewWrite = BuildPreviewDialog(dialogDevice, edits, 144, 40);
            previewWrite.Buttons[1].InvokeCommand(Command.Accept);
            Require(previewWrite.Result == 1, "preview simulate-write button must return index 1");

            app.Driver.SetScreenSize(144, 24);
            List<DeviceSample> wideSamples = CreateSamples();
            MainView wide = BuildMain(app, wideSamples, wideSamples[0], 144, 24);
            SessionToken? wideMainToken = app.Begin(wide.Window);
            Dialog wideRename = BuildRenameDialog(wideSamples[0], 144, 24, out List<NameEdit> resizeEdits);
            SessionToken? wideRenameToken = app.Begin(wideRename);
            Require(wideRename.Frame.Right <= wide.Window.Viewport.Width && wideRename.Frame.Bottom <= wide.Window.Viewport.Height, "wide rename dialog must fit inside the 144x24 window viewport");
            string wideRenameSize = $"{wideRename.Frame.Width}x{wideRename.Frame.Height}";
            resizeEdits[0].TextField!.Text = "缩放保留的名称";
            resizeEdits[0].CheckBox!.Value = CheckState.UnChecked;
            app.Driver.SetScreenSize(80, 24);
            wideRename.SetNeedsLayout();
            app.LayoutAndDraw(true);
            Require(wideRename.Frame.Width <= 76 && wideRename.Frame.Right <= 80, "an open rename dialog must resize within an 80-column terminal");
            app.Driver.SetScreenSize(144, 24);
            wideRename.SetNeedsLayout();
            app.LayoutAndDraw(true);
            Require(resizeEdits[0].TextField!.Text == "缩放保留的名称" && resizeEdits[0].CheckBox!.Value == CheckState.UnChecked, "terminal resizing must preserve edited names and checkboxes");
            if (wideRenameToken is not null) app.End(wideRenameToken);

            List<NameEdit> wideEdits = wideSamples[0].Slots.Select(slot => new NameEdit(slot, true, slot.SuggestedName)).ToList();
            Dialog widePreview = BuildPreviewDialog(wideSamples[0], wideEdits, 144, 24);
            SessionToken? widePreviewToken = app.Begin(widePreview);
            Require(widePreview.Frame.Right <= wide.Window.Viewport.Width && widePreview.Frame.Bottom <= wide.Window.Viewport.Height, "wide preview dialog must fit inside the 144x24 window viewport");
            string widePreviewSize = $"{widePreview.Frame.Width}x{widePreview.Frame.Height}";
            if (widePreviewToken is not null) app.End(widePreviewToken);

            ApplySimulatedWrite(wideSamples[0], [true, true, true], wideSamples[0].Slots.Select(slot => slot.SuggestedName).ToArray());
            Dialog wideResult = BuildResultDialog(wideSamples[0], 144, 24);
            SessionToken? wideResultToken = app.Begin(wideResult);
            Require(wideResult.Frame.Right <= wide.Window.Viewport.Width && wideResult.Frame.Bottom <= wide.Window.Viewport.Height, "wide result dialog must fit inside the 144x24 window viewport");
            string wideResultSize = $"{wideResult.Frame.Width}x{wideResult.Frame.Height}";
            if (wideResultToken is not null) app.End(wideResultToken);
            if (wideMainToken is not null) app.End(wideMainToken);

            app.Driver.SetScreenSize(80, 24);
            List<DeviceSample> narrowSamples = CreateSamples();
            MainView narrow = BuildMain(app, narrowSamples, narrowSamples[0], 80, 24);
            SessionToken? mainToken = app.Begin(narrow.Window);
            Require(!narrow.SmallWarning.Visible && narrow.ListFrame.Visible && narrow.DetailFrame.Visible, "80x24 should remain an operable supported layout");
            Require(narrow.ListFrame.Frame.Right <= narrow.Window.Viewport.Width && narrow.ListFrame.Frame.Bottom <= narrow.Window.Viewport.Height, "narrow list frame must stay inside the window viewport");
            Require(narrow.DetailFrame.Frame.Right <= narrow.Window.Viewport.Width && narrow.DetailFrame.Frame.Bottom <= narrow.Window.Viewport.Height, "narrow detail frame must stay inside the window viewport");
            Require(narrow.Search.Visible && narrow.Search.Frame.Bottom <= narrow.ListFrame.Viewport.Height, "compact search input must remain visible inside the list frame");
            Require(narrow.Search.Frame.Y == narrow.SearchLabel.Frame.Y && narrow.Search.Frame.Height == 1, "compact search label and single-row input must share one row");
            Require(narrow.SlotLines.All(line => line.Visible && line.Frame.Bottom <= narrow.DetailFrame.Viewport.Height), "all three naming locations must fit in the compact detail frame");
            Require(narrow.ActionButtons.All(button => button.Visible && button.Frame.Bottom <= narrow.DetailFrame.Viewport.Height), "all three detail actions must fit in the compact detail frame");

            Dialog narrowRename = BuildRenameDialog(narrowSamples[0], 80, 24, out _);
            SessionToken? renameToken = app.Begin(narrowRename);
            Require(narrowRename.Frame.Width <= narrow.Window.Viewport.Width && narrowRename.Frame.Height <= narrow.Window.Viewport.Height, "rename dialog must fit inside the 80x24 window viewport");
            Require(narrowRename.Frame.Right <= narrow.Window.Viewport.Width && narrowRename.Frame.Bottom <= narrow.Window.Viewport.Height, "rename dialog must not leave the 80x24 window viewport");
            string renameText = app.ToString();
            Require(renameText.Contains("设备本体") && renameText.Contains("USB Composite Device") && renameText.Contains("预览修改") && renameText.Contains("取消"), "80x24 rename dialog must show all fields and both actions");
            Require(renameText.Split("新名称：", StringSplitOptions.None).Length - 1 == 3, "80x24 rename dialog must expose three independently editable names");
            if (renameToken is not null) app.End(renameToken);

            List<NameEdit> narrowEdits = narrowSamples[0].Slots.Select(slot => new NameEdit(slot, true, slot.SuggestedName)).ToList();
            Dialog narrowPreview = BuildPreviewDialog(narrowSamples[0], narrowEdits, 80, 24);
            SessionToken? previewToken = app.Begin(narrowPreview);
            Require(narrowPreview.Frame.Width <= narrow.Window.Viewport.Width && narrowPreview.Frame.Height <= narrow.Window.Viewport.Height, "preview dialog must fit inside the 80x24 window viewport");
            string previewText = app.ToString();
            Require(previewText.Contains("失败项逐项报告") && previewText.Contains("模拟写入"), "80x24 preview must show its outcome note and write action");
            if (previewToken is not null) app.End(previewToken);

            DeviceSample resultDevice = CreateSamples()[0];
            ApplySimulatedWrite(resultDevice, [true, true, true], resultDevice.Slots.Select(slot => slot.SuggestedName).ToArray());
            Dialog narrowResult = BuildResultDialog(resultDevice, 80, 24);
            SessionToken? resultToken = app.Begin(narrowResult);
            Require(narrowResult.Frame.Width <= narrow.Window.Viewport.Width && narrowResult.Frame.Height <= narrow.Window.Viewport.Height, "result dialog must fit inside the 80x24 window viewport");
            string resultText = app.ToString();
            Require(resultText.Contains("原名可恢复") && resultText.Contains("⟦ 声音设置 ⟧"), "80x24 result must show restore guidance and the visible sound settings button");
            bool resultAccepted = false;
            narrowResult.Accepted += (_, _) => resultAccepted = true;
            narrowResult.SubViews.OfType<Button>().Single(button => button.Title.Contains("声音设置")).InvokeCommand(Command.Accept);
            app.LayoutAndDraw(true);
            Require(!resultAccepted && app.ToString().Contains("此样稿没有启动系统设置"), "sound settings help must update its message without accepting the result dialog");
            if (resultToken is not null) app.End(resultToken);

            if (mainToken is not null) app.End(mainToken);

            MainView tooSmall = BuildMain(app, narrowSamples, narrowSamples[0], 79, 23);
            Require(tooSmall.SmallWarning.Visible && !tooSmall.ListFrame.Visible && !tooSmall.DetailFrame.Visible, "below 80x24 should prompt to enlarge while retaining model state");
            Require(tooSmall.SmallWarning.Text.Contains("80×24", StringComparison.Ordinal), "below-minimum prompt must state the supported terminal size");
            Require(narrowSamples[0].Slots.All(slot => slot.CurrentName == slot.OriginalName), "too-small layout must preserve the current mock names");

            Console.SetOut(originalOut);
            Console.WriteLine($"Self-check passed: simulated flow, dialog indices, 80x24 bounds; 144x24 dialogs rename={wideRenameSize}, preview={widePreviewSize}, result={wideResultSize}.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.SetOut(originalOut);
            Console.Error.WriteLine($"Self-check failed: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("DisableRealDriverIO", originalDriverIo);
        }

        static int CountSaved(DeviceSample device) => device.Slots.Count(slot => slot.Status.StartsWith("已保存 · 回读一致", StringComparison.Ordinal));
        static int CountFailed(DeviceSample device) => device.Slots.Count(slot => slot.Status.StartsWith("未完成", StringComparison.Ordinal));
        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private static List<DeviceSample> CreateSamples() =>
    [
        new DeviceSample("罗技 C920", "USB", "已连接", true,
        [
            new NameSlot("摄像头", "设备管理器", "USB Video Device", "罗技 C920 摄像头"),
            new NameSlot("麦克风", "声音设置", "USB Audio Device", "罗技 C920 麦克风"),
            new NameSlot("设备本体", "设备管理器", "USB Composite Device", "罗技 C920")
        ]),
        new DeviceSample("蓝牙耳机", "蓝牙", "已连接", true,
        [new NameSlot("蓝牙设备", "设备管理器", "Bluetooth Audio Device", "蓝牙耳机")]),
        new DeviceSample("USB 接收器", "USB", "已连接", true,
        [new NameSlot("设备本体", "设备管理器", "USB Receiver", "USB 接收器")]),
        new DeviceSample("USB 存储设备", "USB", "新接入", true,
        [new NameSlot("设备本体", "设备管理器", "USB Mass Storage Device", "USB 存储设备")]),
        new DeviceSample("内置麦克风阵列", "内置", "已连接", false,
        [new NameSlot("录音端点", "声音设置", "麦克风阵列", "内置麦克风阵列")])
    ];

    private sealed record CapturedFrame(string Ansi, string Text);
    private sealed record SnapshotInfo(string State, string Title, int Cols, int Rows, string File, string TextFile);
    private sealed record MainView(Window Window, ListView<DeviceSample> List, TextField Search, CheckBox ExternalOnly, Label Status, Action<DeviceSample?> UpdateDetails, Label SearchLabel,
        FrameView ListFrame, FrameView DetailFrame, Label SmallWarning, Label[] SlotLines, Button[] ActionButtons);
    private enum SnapshotState { Main, Rename, Preview, Result, Narrow, NarrowRename }

    private sealed class NameEdit(NameSlot slot, bool enabled, string name, CheckBox? checkBox = null, TextField? textField = null)
    {
        public NameSlot Slot { get; } = slot;
        public bool Enabled { get; set; } = enabled;
        public string Name { get; set; } = name;
        public CheckBox? CheckBox { get; } = checkBox;
        public TextField? TextField { get; } = textField;
    }

    private sealed class DeviceSample(string displayName, string connection, string state, bool isExternal, List<NameSlot> slots)
    {
        public string DisplayName { get; } = displayName;
        public string Connection { get; } = connection;
        public string State { get; } = state;
        public bool IsExternal { get; } = isExternal;
        public List<NameSlot> Slots { get; } = slots;
        public override string ToString() => $"{DisplayName}   ·   {Connection}   ·   {State}";
    }

    private sealed class NameSlot(string feature, string location, string currentName, string suggestedName)
    {
        public string Feature { get; } = feature;
        public string Location { get; } = location;
        public string OriginalName { get; } = currentName;
        public string CurrentName { get; set; } = currentName;
        public string SuggestedName { get; } = suggestedName;
        public string Status { get; set; } = "待处理";
    }
}

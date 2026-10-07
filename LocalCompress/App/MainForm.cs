using System.Diagnostics;
using LocalCompress.Core;

namespace LocalCompress.App;

public sealed class MainForm : Form
{
    private sealed class VideoItem(string path)
    {
        public string Path { get; } = path;
        public string Detail { get; set; } = "";
        public bool Completed { get; set; }
        public string? Output { get; set; }
    }

    private readonly ToolPaths? _tools = ToolPaths.Find(AppContext.BaseDirectory);
    private readonly ListView _files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None, ShowItemToolTips = true };
    private readonly CheckBox _limitSize = new() { Text = "接收方有大小限制", AutoSize = true, Margin = new Padding(0, 9, 14, 0), Name = "limitSize" };
    private readonly NumericUpDown _target = new() { Minimum = .1m, Maximum = 100000, DecimalPlaces = 1, Value = 100, Width = 105, AccessibleName = "每个视频的目标大小 MB", Name = "targetMegabytes" };
    private readonly Button _add = MakeButton("＋ 添加视频");
    private readonly Button _remove = MakeButton("移除选中");
    private readonly Button _folder = MakeButton("选择保存位置");
    private readonly Button _start = MakeButton("开始压缩", true);
    private readonly Button _cancel = MakeButton("停止", false);
    private readonly Button _open = MakeButton("定位文件");
    private readonly TextBox _result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Name = "resultDetail", Text = "处理后会显示是否生成文件、前后大小和保存位置。选择任务可查看详情。", BackColor = Color.White };
    private readonly Label _output = MakeLabel("默认保存在各原视频旁，原文件始终保留。", 10);
    private readonly Label _status = MakeLabel("添加视频后即可开始均衡压缩。", 10);
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 8 };
    private CancellationTokenSource? _run;
    private string? _outputDirectory;
    private bool _closeAfterStop;

    public MainForm()
    {
        Text = "轻压 · 本地视频压缩";
        ClientSize = new Size(900, 740);
        MinimumSize = new Size(800, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(246, 248, 251);
        ForeColor = Color.FromArgb(31, 42, 58);
        AllowDrop = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 14 };
        var heights = new[] { 52, 34, 38, 42, 0, 42, 40, 34, 42, 14, 34, 44, 86, 44 };
        for (var index = 0; index < heights.Length; index++)
            layout.RowStyles.Add(index == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, heights[index]));
        layout.Controls.Add(MakeLabel("把视频变小，把文件留在自己手里。", 20, true), 0, 0);
        layout.Controls.Add(MakeLabel("离线处理 · 无账号 · 无上传 · 保留原文件", 11), 0, 1);
        layout.Controls.Add(MakeLabel("1  添加视频   →   2  按需设置大小上限   →   3  开始压缩", 11, true), 0, 2);
        var fileActions = Flow(_add, _remove, MakeLabel("也可以把视频拖到这里", 10));
        layout.Controls.Add(fileActions, 0, 3);
        _files.Columns.Add("视频", 280);
        _files.Columns.Add("原始大小", 100);
        _files.Columns.Add("状态", 330);
        _files.Columns.Add("耗时", 100);
        layout.Controls.Add(_files, 0, 4);
        var modeLabel = MakeLabel("均衡压缩 · 默认", 10, true);
        modeLabel.Name = "compressionMode";
        layout.Controls.Add(Flow(modeLabel, MakeLabel("兼顾体积和画质，无需调整编码参数。", 9)), 0, 5);
        var targetOptions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Visible = false, Name = "targetOptions", Margin = Padding.Empty };
        targetOptions.Controls.AddRange([MakeLabel("每个视频不超过", 9), _target, MakeLabel("MB", 9)]);
        _target.Enabled = false;
        var sizeHelp = MakeLabel("使用均衡方案，保持原尺寸和帧时间；有损压缩，请保留重要原件。", 9);
        _limitSize.CheckedChanged += (_, _) =>
        {
            targetOptions.Visible = _limitSize.Checked;
            _target.Enabled = _limitSize.Checked && _run is null;
            sizeHelp.Text = _limitSize.Checked
                ? "按上限分析再编码，耗时更长。目标越小画质越低；1 MB = 100 万字节。"
                : "使用均衡方案，保持原尺寸和帧时间；有损压缩，请保留重要原件。";
        };
        layout.Controls.Add(Flow(_limitSize, targetOptions), 0, 6);
        layout.Controls.Add(sizeHelp, 0, 7);
        layout.Controls.Add(Flow(_folder, _output), 0, 8);
        layout.Controls.Add(_progress, 0, 9);
        layout.Controls.Add(_status, 0, 10);
        _open.Name = "locateResult";
        _status.Name = "batchStatus";
        _files.Name = "videoFiles";
        layout.Controls.Add(Flow(_start, _cancel, _open), 0, 11);
        layout.Controls.Add(_result, 0, 12);
        var engineLabel = MakeLabel(_tools is null
            ? "请使用完整体验包，包内自带本地处理引擎。"
            : "本地处理 · 输出 MP4 · 保留首个音轨 · 原文件不覆盖\n暂不支持 HDR、字幕和多音轨保留；重要视频请保留原件。", 9);
        layout.Controls.Add(engineLabel, 0, 13);
        Controls.Add(layout);
        _cancel.Enabled = false;
        _open.Enabled = false;
        _files.SelectedIndexChanged += (_, _) => UpdateResult();
        UpdateStart();

        _add.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "选择本机视频", Multiselect = true,
                Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.mts;*.m2ts;*.flv;*.wmv|所有文件|*.*" };
            if (picker.ShowDialog(this) == DialogResult.OK) AddFiles(picker.FileNames);
        };
        _remove.Click += (_, _) =>
        {
            foreach (ListViewItem item in _files.SelectedItems) _files.Items.Remove(item);
            UpdateStart();
            UpdateResult();
        };
        _folder.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "选择本机保存文件夹", UseDescriptionForTitle = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            if (picker.SelectedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(picker.SelectedPath)!).DriveType == DriveType.Network)
            { MessageBox.Show(this, "请选择本机磁盘上的文件夹。", "保存位置"); return; }
            _outputDirectory = picker.SelectedPath;
            _output.Text = _outputDirectory;
        };
        _start.Click += async (_, _) => await RunBatchAsync();
        _cancel.Click += (_, _) => { _run?.Cancel(); _cancel.Enabled = false; _status.Text = "正在停止，原文件不会改动…"; };
        _open.Click += (_, _) => OpenSelectedResult();
        _files.DoubleClick += (_, _) =>
        {
            if (_files.SelectedItems.Count == 0) return;
            var item = (VideoItem)_files.SelectedItems[0].Tag!;
            MessageBox.Show(this, item.Detail.Length == 0 ? "等待压缩。原文件不会被覆盖。" : item.Detail, "任务详情");
        };
        DragEnter += (_, e) => e.Effect = _run is null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (_run is null && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
        FormClosing += (_, e) =>
        {
            if (_run is null) return;
            e.Cancel = true;
            _closeAfterStop = true;
            _run.Cancel();
            _status.Text = "正在停止处理并清理临时文件…";
        };
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var local = CompressionEngine.LocalFile(path);
                if (_files.Items.Cast<ListViewItem>().Any(x => string.Equals(((VideoItem)x.Tag!).Path, local, StringComparison.OrdinalIgnoreCase))) continue;
                var row = new ListViewItem([Path.GetFileName(local), FormatBytes(new FileInfo(local).Length), "等待压缩", "—"])
                    { Tag = new VideoItem(local), ToolTipText = local };
                _files.Items.Add(row);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
        }
        _status.Text = $"已添加 {_files.Items.Count} 个视频。";
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors.Take(5)), "部分文件无法添加");
        UpdateStart();
    }

    private async Task RunBatchAsync()
    {
        if (_run is not null || _tools is null) return;
        var jobs = _files.Items.Cast<ListViewItem>().Where(x => !((VideoItem)x.Tag!).Completed).ToArray();
        if (jobs.Length == 0) return;
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        SetRunning(true);
        double? targetMegabytes = _limitSize.Checked ? (double)_target.Value : null;
        var engine = new CompressionEngine(_tools);
        var batchClock = Stopwatch.StartNew();
        int completed = 0, skipped = 0, unsuccessful = 0;
        try
        {
            for (var i = 0; i < jobs.Length; i++)
            {
                if (cancellation.IsCancellationRequested) break;
                var row = jobs[i];
                var item = (VideoItem)row.Tag!;
                var taskClock = Stopwatch.StartNew();
                row.SubItems[3].Text = "处理中";
                foreach (ListViewItem selected in _files.SelectedItems) selected.Selected = false;
                row.Selected = true;
                row.EnsureVisible();
                row.SubItems[2].Text = "正在读取视频…";
                _status.Text = $"正在处理 {i + 1}/{jobs.Length}：{Path.GetFileName(item.Path)}";
                var batchIndex = i;
                var active = true;
                var progress = new Progress<CompressionProgress>(p =>
                {
                    if (_run != cancellation || item.Completed || !active) return;
                    row.SubItems[2].Text = $"{p.Stage} {p.Fraction:P0}";
                    _progress.Value = Math.Clamp((int)((batchIndex + p.Fraction) / jobs.Length * 1000), 0, 1000);
                });
                try
                {
                    var result = await engine.CompressBalancedAsync(item.Path, _outputDirectory ?? Path.GetDirectoryName(item.Path)!, targetMegabytes, progress, cancellation.Token);
                    item.Completed = true;
                    var presentation = ResultPresentation.From(result);
                    item.Output = presentation.CreatedFile ? result.OutputPath : null;
                    row.SubItems[2].Text = presentation.Status;
                    item.Detail = presentation.Detail;
                    if (presentation.CreatedFile) completed++; else skipped++;
                    UpdateResult();
                }
                catch (OperationCanceledException)
                {
                    if (cancellation.IsCancellationRequested)
                    { row.SubItems[2].Text = "已停止 · 原文件保留"; item.Detail = "任务已停止，临时结果已清理。"; break; }
                    row.SubItems[2].Text = "读取超时 · 双击查看";
                    item.Detail = "读取视频超过 30 秒，原文件未改动。";
                    unsuccessful++;
                }
                catch (Exception ex)
                {
                    row.SubItems[2].Text = "未完成 · 双击查看原因";
                    item.Detail = ex.Message;
                    unsuccessful++;
                }
                finally
                {
                    taskClock.Stop();
                    active = false;
                    var elapsed = FormatDuration(taskClock.Elapsed);
                    row.SubItems[3].Text = elapsed;
                    item.Detail += $"\r\n处理耗时：{elapsed}（包含读取、编码、结果检查和临时文件清理，不含排队）。";
                    UpdateResult();
                }
                _progress.Value = (i + 1) * 1000 / jobs.Length;
            }
            _status.Text = cancellation.IsCancellationRequested
                ? $"已停止：已压缩 {completed} 个，已跳过 {skipped} 个，失败 {unsuccessful} 个。原文件保留。"
                : $"处理结束：已压缩 {completed} 个，已跳过 {skipped} 个，失败 {unsuccessful} 个。原文件保留。";
            batchClock.Stop();
            _status.Text += $" 总耗时 {FormatDuration(batchClock.Elapsed)}。";
        }
        finally
        {
            _run = null;
            SetRunning(false);
            if (_closeAfterStop) Close();
        }
    }

    private void SetRunning(bool running)
    {
        _add.Enabled = _remove.Enabled = _folder.Enabled = !running;
        _limitSize.Enabled = !running;
        _target.Enabled = !running && _limitSize.Checked;
        _cancel.Enabled = running;
        UpdateResult();
        UpdateStart();
    }

    private string? SelectedOutput()
    {
        if (_files.SelectedItems.Count != 1) return null;
        var output = ((VideoItem)_files.SelectedItems[0].Tag!).Output;
        return output is not null && File.Exists(output) ? output : null;
    }

    private void OpenSelectedResult()
    {
        var output = SelectedOutput();
        if (output is null) { UpdateResult(); return; }
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(output);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            MessageBox.Show(this, "无法定位文件，请复制下方路径，在资源管理器中打开。\n" + ex.Message, "定位文件");
        }
    }

    private void UpdateResult()
    {
        _result.Text = _files.SelectedItems.Count == 1 ? ((VideoItem)_files.SelectedItems[0].Tag!).Detail : "选择任务查看处理结果。";
        if (_result.Text.Length == 0) _result.Text = "等待处理，尚未生成压缩文件。";
        _open.Enabled = _run is null && SelectedOutput() is not null;
    }

    private void UpdateStart() => _start.Enabled = _run is null && _tools is not null && _files.Items.Cast<ListViewItem>().Any(x => !((VideoItem)x.Tag!).Completed);
    private static string FormatBytes(long size) => size >= 1_000_000_000 ? $"{size / 1_000_000_000d:F2} GB" : $"{size / 1_000_000d:F2} MB";
    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分 {elapsed.Seconds} 秒"
        : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
        : elapsed.TotalSeconds < .1 ? "小于 0.1 秒" : $"{elapsed.TotalSeconds:F1} 秒";
    private static Label MakeLabel(string text, float size, bool bold = false) => new()
    { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 9, 12, 0) };
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }
    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(100, 36), FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Color.FromArgb(41, 94, 226) : Color.White,
        ForeColor = primary ? Color.White : Color.FromArgb(31, 42, 58),
        Margin = new Padding(0, 2, 12, 2), Cursor = Cursors.Hand
    };
}

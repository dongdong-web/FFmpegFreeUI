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
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175, AccessibleName = "压缩方案" };
    private readonly Button _add = MakeButton("＋ 添加视频");
    private readonly Button _remove = MakeButton("移除选中");
    private readonly Button _folder = MakeButton("选择保存位置");
    private readonly Button _start = MakeButton("开始压缩", true);
    private readonly Button _cancel = MakeButton("停止", false);
    private readonly Button _open = MakeButton("打开结果文件夹");
    private readonly Button _more = MakeButton("更多处理 ▸");
    private readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, AccessibleName = "输出分辨率", Name = "resolution" };
    private readonly CheckBox _denoise = new() { Text = "轻度降噪", AutoSize = true, Margin = new Padding(0, 9, 18, 0), Name = "denoise" };
    private readonly CheckBox _normalize = new() { Text = "统一音量", AutoSize = true, Margin = new Padding(0, 9, 0, 0), Name = "normalize" };
    private readonly Label _output = MakeLabel("默认保存在各原视频旁，原文件始终保留。", 10);
    private readonly Label _status = MakeLabel("添加视频，然后选择压缩方案。", 10);
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 8 };
    private CancellationTokenSource? _run;
    private string? _outputDirectory;
    private string? _lastOutput;
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
        var heights = new[] { 52, 34, 48, 48, 0, 48, 40, 0, 40, 36, 14, 42, 54, 56 };
        for (var index = 0; index < heights.Length; index++)
            layout.RowStyles.Add(index == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, heights[index]));
        layout.Controls.Add(MakeLabel("把视频变小，把文件留在自己手里。", 20, true), 0, 0);
        layout.Controls.Add(MakeLabel("离线处理 · 无账号 · 无上传 · 保留原文件", 11), 0, 1);
        layout.Controls.Add(MakeLabel("1  添加视频   →   2  选择方案   →   3  开始压缩", 11, true), 0, 2);
        var fileActions = Flow(_add, _remove, MakeLabel("也可以把视频拖到这里", 10));
        layout.Controls.Add(fileActions, 0, 3);
        _files.Columns.Add("视频", 340);
        _files.Columns.Add("原始大小", 110);
        _files.Columns.Add("状态", 330);
        layout.Controls.Add(_files, 0, 4);
        _quality.Items.AddRange(["画质优先", "均衡（推荐）", "体积优先"]);
        _quality.SelectedIndex = 1;
        layout.Controls.Add(Flow(MakeLabel("压缩方案", 10, true), _quality,
            MakeLabel("画质与体积需要取舍，实际缩小比例因视频而异。", 9)), 0, 5);
        _resolution.Items.AddRange(["保留原尺寸（默认）", "缩小到 1080p", "缩小到 720p"]);
        _resolution.SelectedIndex = 0;
        _more.Name = "moreProcessing";
        var processing = Flow(MakeLabel("分辨率", 10, true), _resolution, _denoise, _normalize);
        processing.WrapContents = true;
        processing.SetFlowBreak(_normalize, true);
        processing.Controls.Add(MakeLabel("只缩小不放大；降噪会减少细节，统一音量会调整音轨，默认都关闭。", 9));
        processing.Name = "processingOptions";
        processing.Visible = false;
        layout.Controls.Add(Flow(_more, MakeLabel("可选，不改也能直接压缩", 9)), 0, 6);
        layout.Controls.Add(processing, 0, 7);
        _more.Click += (_, _) =>
        {
            processing.Visible = !processing.Visible;
            layout.RowStyles[7].Height = processing.Visible ? 74 : 0;
            _more.Text = processing.Visible ? "收起处理 ▾" : "更多处理 ▸";
        };
        var tips = new ToolTip();
        tips.SetToolTip(_resolution, "保持宽高比，只缩小不放大。横屏限制高度，竖屏限制宽度。");
        tips.SetToolTip(_denoise, "适合有噪点的画面，会减少部分细节；默认关闭。");
        tips.SetToolTip(_normalize, "自动调整首个音轨的响度，可能改变原有音量；默认关闭。");
        Disposed += (_, _) => tips.Dispose();
        layout.Controls.Add(Flow(_folder, _output), 0, 8);
        layout.Controls.Add(MakeLabel("输出 MP4 · 保留首个音轨 · 不保留字幕、其他音轨和可选元数据 · 暂不支持 HDR", 9), 0, 9);
        layout.Controls.Add(_progress, 0, 10);
        layout.Controls.Add(_status, 0, 11);
        layout.Controls.Add(Flow(_start, _cancel, _open), 0, 12);
        var engineLabel = MakeLabel(_tools is null
            ? "缺少本地处理引擎：请将 ffmpeg.exe 和 ffprobe.exe 放入程序旁的 tools 文件夹。程序不会自动下载。"
            : "本地处理引擎已就绪 · 使用 3FUI 预设引擎。程序不发起网络请求。", 9);
        layout.Controls.Add(engineLabel, 0, 13);
        Controls.Add(layout);
        _cancel.Enabled = false;
        _open.Enabled = false;
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
        _open.Click += (_, _) =>
        {
            if (_lastOutput is null) return;
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(Path.GetDirectoryName(_lastOutput)!);
            Process.Start(start)?.Dispose();
        };
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
                var row = new ListViewItem([Path.GetFileName(local), FormatBytes(new FileInfo(local).Length), "等待压缩"])
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
        var quality = (CompressionQuality)_quality.SelectedIndex;
        var processingOptions = new ProcessingOptions(
            _resolution.SelectedIndex switch { 1 => 1080, 2 => 720, _ => 0 }, _denoise.Checked, _normalize.Checked);
        var engine = new CompressionEngine(_tools);
        int completed = 0, unsuccessful = 0;
        try
        {
            for (var i = 0; i < jobs.Length; i++)
            {
                if (cancellation.IsCancellationRequested) break;
                var row = jobs[i];
                var item = (VideoItem)row.Tag!;
                row.SubItems[2].Text = "正在读取视频…";
                _status.Text = $"正在处理 {i + 1}/{jobs.Length}：{Path.GetFileName(item.Path)}";
                var batchIndex = i;
                var active = true;
                var progress = new Progress<CompressionProgress>(p =>
                {
                    if (_run != cancellation || item.Completed || !active) return;
                    row.SubItems[2].Text = $"压缩中 {p.Fraction:P0}";
                    _progress.Value = Math.Clamp((int)((batchIndex + p.Fraction) / jobs.Length * 1000), 0, 1000);
                });
                try
                {
                    var result = await engine.CompressAsync(item.Path, _outputDirectory ?? Path.GetDirectoryName(item.Path)!, quality, progress, cancellation.Token, processingOptions);
                    item.Completed = true;
                    item.Output = result.OutputPath;
                    _lastOutput = result.OutputPath;
                    var reduction = 1 - (double)result.OutputBytes / result.OriginalBytes;
                    row.SubItems[2].Text = $"完成 · {FormatBytes(result.OutputBytes)} · 减少 {reduction:P0}";
                    item.Detail = $"已保存：{result.OutputPath}\n原始大小：{FormatBytes(result.OriginalBytes)}\n压缩后：{FormatBytes(result.OutputBytes)}\n原文件保留。";
                    completed++;
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
                finally { active = false; }
                _progress.Value = (i + 1) * 1000 / jobs.Length;
            }
            _status.Text = cancellation.IsCancellationRequested
                ? $"已停止。完成 {completed} 个视频，原文件全部保留。"
                : $"处理结束：完成 {completed} 个，未完成 {unsuccessful} 个。原文件全部保留。";
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
        _add.Enabled = _remove.Enabled = _folder.Enabled = _quality.Enabled = !running;
        _more.Enabled = _resolution.Enabled = _denoise.Enabled = _normalize.Enabled = !running;
        _cancel.Enabled = running;
        _open.Enabled = !running && _lastOutput is not null;
        UpdateStart();
    }

    private void UpdateStart() => _start.Enabled = _run is null && _tools is not null && _files.Items.Cast<ListViewItem>().Any(x => !((VideoItem)x.Tag!).Completed);
    private static string FormatBytes(long size) => size >= 1024 * 1024 * 1024 ? $"{size / (1024d * 1024 * 1024):F2} GB" : $"{size / (1024d * 1024):F1} MB";
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

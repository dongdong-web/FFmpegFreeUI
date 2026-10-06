using LocalCompress.App;

Exception? failure = null;
var destination = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-preview.png");
var thread = new Thread(() =>
{
    try
    {
        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();
        if (!form.Text.Contains("本地视频压缩")) throw new Exception("Unexpected window title.");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(destination, System.Drawing.Imaging.ImageFormat.Png);
        foreach (var name in new[] { "quality", "moreProcessing", "resolution", "denoise", "normalize", "scene" })
            if (form.Controls.Find(name, true).Length != 0) throw new Exception("Advanced control still exposed: " + name);
        if (!form.Controls.Find("compressionMode", true).Single().Text.Contains("均衡压缩")) throw new Exception("Expected the restored balanced product mode.");
        var targetOptions = form.Controls.Find("targetOptions", true).Single();
        var limit = (CheckBox)form.Controls.Find("limitSize", true).Single();
        var target = (NumericUpDown)form.Controls.Find("targetMegabytes", true).Single();
        if (targetOptions.Visible || limit.Checked || target.Enabled) throw new Exception("Size limit must be off by default.");
        limit.Checked = true;
        Application.DoEvents();
        if (!targetOptions.Visible || !target.Enabled || target.Value != 100) throw new Exception("Size limit input did not appear.");
        using var targetBitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(targetBitmap, new Rectangle(0, 0, form.Width, form.Height));
        targetBitmap.Save(Path.Combine(Path.GetDirectoryName(destination)!, "ui-target-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
        target.Value = 25;
        limit.Checked = false;
        Application.DoEvents();
        if (targetOptions.Visible || target.Enabled || target.Value != 25) throw new Exception("Disabling the limit must stop applying it and retain the entered value.");
        Console.WriteLine("PASS Balanced compression is the sole default; conservative scene choices are not exposed");
        Console.WriteLine("PASS Size limit starts disabled and toggles independently of the scene");
        if (args.Length > 1)
        {
            var fixtureRoot = Path.GetFullPath(args[1]);
            typeof(MainForm).GetMethod("AddFiles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(form, [new[] { Path.Combine(fixtureRoot, "success.mp4"), Path.Combine(fixtureRoot, "skipped.mp4"), Path.Combine(fixtureRoot, "broken.mp4") }]);
            var run = (Task)typeof(MainForm).GetMethod("RunBatchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null)!;
            while (!run.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
            run.GetAwaiter().GetResult();
            Application.DoEvents();
            var rows = (ListView)form.Controls.Find("videoFiles", true).Single();
            var status = form.Controls.Find("batchStatus", true).Single();
            var detail = (TextBox)form.Controls.Find("resultDetail", true).Single();
            var play = form.Controls.Find("playResult", true).Single();
            var locate = form.Controls.Find("locateResult", true).Single();
            if (!status.Text.Contains("已压缩 1 个，已跳过 1 个，失败 1 个")) throw new Exception("Misleading batch counts: " + status.Text);
            void SelectRow(int index)
            {
                foreach (ListViewItem row in rows.Items) row.Selected = false;
                rows.Items[index].Selected = true;
                Application.DoEvents();
            }
            SelectRow(0);
            if (!rows.Items[0].SubItems[2].Text.StartsWith("已生成") || !detail.Text.Contains("保存位置") ||
                !detail.Text.Contains("减少") || !play.Enabled || !locate.Enabled) throw new Exception("Created output is not discoverable.");
            using var successBitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(successBitmap, new Rectangle(0, 0, form.Width, form.Height));
            successBitmap.Save(Path.Combine(Path.GetDirectoryName(destination)!, "ui-result-success.png"));
            SelectRow(1);
            if (!rows.Items[1].SubItems[2].Text.StartsWith("未生成文件") || !detail.Text.Contains("本次没有压缩后文件或压缩率") ||
                play.Enabled || locate.Enabled) throw new Exception("Skipped task still appears to have an output.");
            using var skipBitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(skipBitmap, new Rectangle(0, 0, form.Width, form.Height));
            skipBitmap.Save(Path.Combine(Path.GetDirectoryName(destination)!, "ui-result-skipped.png"));
            SelectRow(2);
            if (play.Enabled || locate.Enabled || !detail.Text.Contains("无法读取视频")) throw new Exception("Failure shows stale output actions.");
            Console.WriteLine("PASS Real batch distinguishes generated/skipped/failed, shows sizes and path, and enables actions only for the selected generated output");
        }
        form.Close();
        Console.WriteLine("PASS Native WinForms startup and rendering: " + destination);
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (failure is not null) throw failure;

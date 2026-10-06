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
        foreach (var name in new[] { "quality", "moreProcessing", "resolution", "denoise", "normalize" })
            if (form.Controls.Find(name, true).Length != 0) throw new Exception("Advanced control still exposed: " + name);
        var scene = (ComboBox)form.Controls.Find("scene", true).Single();
        if (scene.Items.Count != 2 || scene.SelectedIndex != 0) throw new Exception("Expected two scenes with daily as default.");
        scene.SelectedIndex = 1;
        Application.DoEvents();
        using var screen = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(screen, new Rectangle(0, 0, form.Width, form.Height));
        screen.Save(Path.Combine(Path.GetDirectoryName(destination)!, "ui-screen-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
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
        scene.SelectedIndex = 0;
        Application.DoEvents();
        if (targetOptions.Visible || target.Enabled || target.Value != 25) throw new Exception("Disabling the limit must stop applying it and retain the entered value.");
        Console.WriteLine("PASS Two scene choices replace advanced controls; daily is the default");
        Console.WriteLine("PASS Size limit starts disabled and toggles independently of the scene");
        form.Close();
        Console.WriteLine("PASS Native WinForms startup and rendering: " + destination);
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (failure is not null) throw failure;

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
        var processing = form.Controls.Find("processingOptions", true).Single();
        if (processing.Visible) throw new Exception("Optional processing must start collapsed.");
        ((Button)form.Controls.Find("moreProcessing", true).Single()).PerformClick();
        Application.DoEvents();
        if (!processing.Visible) throw new Exception("Optional processing did not expand.");
        if (((ComboBox)form.Controls.Find("resolution", true).Single()).SelectedIndex != 0 ||
            ((CheckBox)form.Controls.Find("denoise", true).Single()).Checked ||
            ((CheckBox)form.Controls.Find("normalize", true).Single()).Checked)
            throw new Exception("Default processing must retain the original size and leave optional filters off.");
        using var expanded = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(expanded, new Rectangle(0, 0, form.Width, form.Height));
        expanded.Save(Path.Combine(Path.GetDirectoryName(destination)!, "ui-processing-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
        var targetOptions = form.Controls.Find("targetOptions", true).Single();
        if (targetOptions.Visible) throw new Exception("Target size must be hidden in the default quality mode.");
        var quality = (ComboBox)form.Controls.Find("quality", true).Single();
        quality.SelectedIndex = 3;
        Application.DoEvents();
        if (!targetOptions.Visible || ((NumericUpDown)form.Controls.Find("targetMegabytes", true).Single()).Value != 100)
            throw new Exception("Target mode must expose a 100 MB default.");
        using var targetBitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(targetBitmap, new Rectangle(0, 0, form.Width, form.Height));
        targetBitmap.Save(Path.Combine(Path.GetDirectoryName(destination)!, "ui-target-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
        quality.SelectedIndex = 1;
        Application.DoEvents();
        if (targetOptions.Visible) throw new Exception("Returning to quality mode must hide target settings.");
        Console.WriteLine("PASS Target-size mode exposes its input and returns to the default mode");
        Console.WriteLine("PASS Optional controls are collapsed by default and expand correctly");
        form.Close();
        Console.WriteLine("PASS Native WinForms startup and rendering: " + destination);
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (failure is not null) throw failure;

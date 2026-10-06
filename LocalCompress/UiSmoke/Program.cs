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

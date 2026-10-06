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
        form.Close();
        Console.WriteLine("PASS Native WinForms startup and rendering: " + destination);
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (failure is not null) throw failure;

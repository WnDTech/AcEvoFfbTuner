using PedalTest;

try
{
    Application.SetHighDpiMode(HighDpiMode.SystemAware);
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm());
}
catch (Exception ex)
{
    MessageBox.Show($"Fatal error: {ex.Message}\n\n{ex.StackTrace}",
        "Pedal Test Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

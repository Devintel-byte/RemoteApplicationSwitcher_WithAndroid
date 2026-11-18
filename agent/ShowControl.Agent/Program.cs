namespace ShowControl.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string configPath = ConfigStore.DefaultPath;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--config" or "-c") configPath = Path.GetFullPath(args[i + 1]);
        }

        // One-shot switches used by the installer (run as the signed-in user, then exit):
        //   --startup on|off    start the agent when this user signs in to Windows
        //   --set-name "Name"   name this PC (what phones show), creating the config if needed
        if (OneShot(args, configPath)) return;

        using var mutex = new Mutex(false, @"Local\ShowControlAgent");
        bool firstInstance;
        // Wait briefly: after "Restart agent" the previous copy is still shutting down.
        try { firstInstance = mutex.WaitOne(TimeSpan.FromSeconds(6)); }
        catch (AbandonedMutexException) { firstInstance = true; }
        if (!firstInstance)
        {
            MessageBox.Show("Show Control Agent is already running. Look for its icon in the system tray.",
                "Show Control Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(configPath));
    }

    private static bool OneShot(string[] args, string configPath)
    {
        int i = Array.FindIndex(args, a => a is "--startup" or "--set-name");
        if (i < 0) return false;
        string value = i + 1 < args.Length ? args[i + 1] : "";
        try
        {
            if (args[i] == "--startup")
            {
                StartupRegistration.Set(value.Equals("on", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                string name = value.Trim();
                if (name.Length > 0)
                {
                    using var store = new ConfigStore(configPath);
                    store.Load();
                    store.Mutate(c => c.Name = name[..Math.Min(60, name.Length)]);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{args[i]} failed", ex);
        }
        return true;
    }
}

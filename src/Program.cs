namespace MessengerWrapper;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Une seule instance : un second lancement ramène simplement la fenêtre existante.
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MessengerWrapper.Activate", out bool first);
        if (!first)
        {
            activate.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(activate, startHidden: args.Contains("--hidden"), startDocked: args.Contains("--dock")));
    }
}

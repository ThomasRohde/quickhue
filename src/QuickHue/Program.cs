namespace QuickHue;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, @"Local\QuickHue.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        var store = new ConfigStore();
        Application.Run(new TrayApplicationContext(store, store.Load()));
    }
}

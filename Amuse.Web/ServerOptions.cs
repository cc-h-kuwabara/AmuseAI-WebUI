namespace Amuse.Web;

public sealed record ServerOptions(int Port, string DataDirectory, string HostDirectory, string? ImportDirectory)
{
    public const string Usage = "Amuse.Web [--port 8080] [--data-dir <directory>] [--host-dir <directory>] [--import-desktop <directory>]\nLocal browser: http://localhost:<port>  |  Stop: Ctrl+C";

    public static ServerOptions Parse(string[] args)
    {
        var port = 8080;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amuse", "Web");
        var hosts = AppContext.BaseDirectory;
        string? import = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!seen.Add(key)) throw new ArgumentException($"Duplicate option: {key}");
            if (key is not ("--port" or "--data-dir" or "--host-dir" or "--import-desktop"))
                throw new ArgumentException($"Unknown option: {key}");
            if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException($"Missing value: {key}");
            var value = args[i];
            switch (key)
            {
                case "--port":
                    if (!int.TryParse(value, out port) || port < 1 || port > 65535)
                        throw new ArgumentException("--port must be between 1 and 65535.");
                    break;
                case "--data-dir": data = Path.GetFullPath(value); break;
                case "--host-dir": hosts = Path.GetFullPath(value); break;
                case "--import-desktop": import = Path.GetFullPath(value); break;
            }
        }
        return new(port, Path.GetFullPath(data), Path.GetFullPath(hosts), import);
    }
}

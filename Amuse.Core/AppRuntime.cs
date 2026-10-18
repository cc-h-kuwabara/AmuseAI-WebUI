using Amuse.App.Common;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Amuse.App
{
    // Host-specific paths and interactions. The core never creates Application or a Window.
    public static class AppRuntime
    {
        public static string DirectoryBase { get; set; } = AppContext.BaseDirectory;
        public static string DirectoryData { get; set; } = AppContext.BaseDirectory;
        public static string DirectoryServer { get; set; } = AppContext.BaseDirectory;
        public static string AppVersionDisplay => typeof(AppRuntime).Assembly.GetName().Version?.ToString();
        public static Action<Action> Dispatch { get; set; } = action => action();
        public static Func<EnvironmentModel, Task<bool>> PrepareEnvironment { get; set; } = _ =>
            throw new InvalidOperationException("Create or update the environment before loading this model.");
        public static Func<IDownloadModel, Task> RequestAccessToken { get; set; } = _ =>
            throw new InvalidOperationException("Configure the model access token in Settings before downloading.");
        public static Func<string, Task<(bool Allowed, bool Remember)>> ConfirmExternalAccess { get; set; } = _ =>
            Task.FromResult((false, false));
    }
}

using Amuse.App;
using Amuse.App.Common;
using Amuse.Common;
using TensorStack.Common;

namespace Amuse.Web;

internal static class LinuxPlatform
{
    public const string NativeVersion = "master-968-8cd731e";
    public static void Configure(Settings settings)
    {
        if (!OperatingSystem.IsLinux()) return;
        // Windows runtime archives and Python environments cannot be reused.
        foreach (var environment in settings.Environments.ToArray())
            if (environment.Backend == BackendType.HuggingFace ||
                environment.Requirements?.Any(x => x.Contains("win-", StringComparison.OrdinalIgnoreCase) || x.Contains("windows", StringComparison.OrdinalIgnoreCase)) == true)
                settings.Environments.Remove(environment);
        if (!settings.Environments.Any(x => x.Backend == BackendType.StableDiffusionCpp && x.Vendor == VendorType.CPU))
            settings.Environments.Add(new EnvironmentModel
            {
                Id = settings.Environments.Count == 0 ? 10001 : Math.Max(10001, settings.Environments.Max(x => x.Id) + 1),
                Name = "StableDiffusion.cpp Linux CPU", Backend = BackendType.StableDiffusionCpp,
                Vendor = VendorType.CPU, Type = EnvironmentType.Vendor, IsDefault = true,
                Environment = "linux-cpu", HostVersion = "CPU", Requirements =
                [$"https://github.com/leejet/stable-diffusion.cpp/releases/download/{NativeVersion}/sd-master-8cd731e-bin-Linux-Ubuntu-24.04-x86_64.zip"],
                Variables = []
            });
        if (!settings.Environments.Any(x => x.Backend == BackendType.StableDiffusionCpp && x.Vendor == VendorType.Nvidia))
            settings.Environments.Add(new EnvironmentModel
            {
                Id = Math.Max(10002, settings.Environments.Max(x => x.Id) + 1),
                Name = "StableDiffusion.cpp Linux NVIDIA CUDA", Backend = BackendType.StableDiffusionCpp,
                Vendor = VendorType.Nvidia, Type = EnvironmentType.Vendor, IsDefault = true,
                Environment = "linux-cuda", HostVersion = "CUDA", Requirements = [], Variables = []
            });
    }
}

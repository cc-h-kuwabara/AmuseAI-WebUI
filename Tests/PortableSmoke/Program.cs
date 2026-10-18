using System.Diagnostics;
using System.Text.Json;
using Amuse.Common;
using Amuse.Common.Config;
using Amuse.Common.Message;
using TensorStack.Common;
using TensorStack.Common.Tensor;
using TensorStack.OnnxRuntime;
using TensorStack.WPF.Image;
using TensorStack.StableDiffusionCpp;
using Microsoft.ML.OnnxRuntime;
using Amuse.App;
using Amuse.App.Services;

void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS: " + name); }
if (args is ["read", var channelName, var metadataPath])
{
    var response = JsonSerializer.Deserialize<PipelineResponse>(File.ReadAllText(metadataPath));
    new PipelineTensorChannel(new ServerConfig { ChannelTensor = channelName }).ReadResponse(response);
    Check(response.ImageTensors[0].Span.SequenceEqual(new ImageTensor(2, 3, .25f).Span), "cross-process tensor values and dimensions");
    return;
}
if (args.Length < 3) throw new ArgumentException("Usage: PortableSmoke <image> <identity.onnx> <native-runtime-directory>");
var temp = Path.Combine(Path.GetTempPath(), "amuse-portable-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    using var image = await ImageInput.CreateAsync(args[0]);
    image.Resize(64, 64, ResizeMode.Stretch);
    var png = Path.Combine(temp, "image.png"); await image.SaveAsync(png);
    using var restored = await ImageInput.CreateAsync(png);
    Check(restored.Width == 64 && restored.Height == 64, "Skia image load, resize and PNG round-trip");

    Check(!string.IsNullOrEmpty(new System.Resources.ResourceManager("TensorStack.WPF.Resources.Errors", typeof(TensorStack.WPF.BaseModel).Assembly).GetString("DownloadQueriedZero")), "headless download error resource lookup");
    var savedPath = Environment.GetEnvironmentVariable("PATH");
    var savedVisible = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
    try
    {
        var fake = Path.Combine(temp, "nvidia-smi");
        File.WriteAllText(fake, "#!/bin/sh\nprintf '%s\\n' '0, 8192, 00000000:01:00.0, GPU-zero, NVIDIA Test A' '1, 16384, 00000000:02:00.0, GPU-one, NVIDIA Test B'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("PATH", temp + ":" + savedPath);
        Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "GPU-one");
        using var hardware = new HardwareService(new Settings());
        var gpu = hardware.GetGPUDevices().Single(x=>x.Vendor==VendorType.Nvidia);
        Check(gpu.Name == "NVIDIA Test B" && gpu.DeviceId == 0 && gpu.VendorIndex == 0 && gpu.PCIBusId == 2 && gpu.Memory == 16384,
            "NVIDIA discovery fixture, CUDA visibility ordering and PCI bus mapping");
        Check(gpu.SupportedBackends.SequenceEqual(new[] {BackendType.StableDiffusionCpp}), "NVIDIA device advertises only implemented Linux GPU backends");
        Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "-1");
        using var cpuOnly = new HardwareService(new Settings());
        Check(cpuOnly.GetGPUDevices().All(x=>x.Type==DeviceType.CPU), "CUDA_VISIBLE_DEVICES=-1 retains the CPU fallback");
    }
    finally { Environment.SetEnvironmentVariable("PATH", savedPath); Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", savedVisible); }

    Provider.Initialize();
    Check(Provider.GetDevices().Any(x => x.Type == DeviceType.CPU), "CPU ONNX provider discovery");
    using (var session = new InferenceSession(args[1]))
    using (var result = session.Run([NamedOnnxValue.CreateFromTensor("input", new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(new float[] {1, 2, 3, 4}, new int[] {1, 4}))]))
        Check(result.First().AsEnumerable<float>().SequenceEqual(new float[] {1, 2, 3, 4}), "real ONNX CPU inference");

    var name = "Amuse.PortableSmoke." + Guid.NewGuid().ToString("N");
    var response = new PipelineResponse(new ImageTensor(2, 3, .25f));
    var tensors = response.PackTensors();
    var channel = new PipelineTensorChannel(new ServerConfig {ChannelTensor=name});
    using (channel.WriteResponse(tensors))
    {
        var metadata = Path.Combine(temp, "tensor.json"); File.WriteAllText(metadata, JsonSerializer.Serialize(response));
        var start = new ProcessStartInfo(Environment.ProcessPath) {UseShellExecute=false};
        foreach (var arg in new[] {"read", name, metadata}) start.ArgumentList.Add(arg);
        using var child = Process.Start(start); await child.WaitForExitAsync();
        Check(child.ExitCode == 0, "shared tensor IPC between Linux processes");
    }
    channel.Cleanup();
    using (var handler = new ProcessHandler())
    {
        using var child = Process.Start("sleep", "60"); handler.AddProcess(child); handler.Dispose();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(child.HasExited, "owned host process cleanup");
    }
    using var native = new StableDiffusionPipeline(args[2]);
    Check(native.Devices.Any(x=>x.Type==DeviceType.CPU), "native StableDiffusion.cpp Linux ABI and CPU discovery");
}
finally { Directory.Delete(temp, true); }

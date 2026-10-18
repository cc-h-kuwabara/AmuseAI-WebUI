using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Amuse.App.Common;
using TensorStack.Common;
namespace Amuse.App.Services
{
    // Linux CPU baseline and NVIDIA CUDA device discovery; no Windows WMI/DXGI calls.
    public sealed class HardwareService : IHardwareService
    {
        private readonly IReadOnlyList<DeviceModel> _devices;
        public HardwareService(Settings settings) { _devices = Discover(); }
        public CPUDevice CPUDevice => new() {Name = "CPU", MemoryTotal = MemoryMb};
        private static int MemoryMb => (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
        public CPUStatus CPUStatus => new(CPUDevice);
        public NPUDevice NPUDevice => null;
        public NPUStatus NPUStatus => default;
        public GPUDevice[] GPUDevices => [];
        public GPUStatus[] GPUStatus => [];
        public AdapterInfo[] Adapters => [];
        public void Pause() { }
        public void Resume() { }
        public void Dispose() { }
        public IReadOnlyList<DeviceModel> GetGPUDevices() => _devices;
        private static IReadOnlyList<DeviceModel> Discover()
        {
            var devices = new List<DeviceModel> {new DeviceModel(new Device {Id=0, DeviceId=0, Type=DeviceType.CPU, Vendor=VendorType.CPU, Name="CPU (Linux)", Memory=MemoryMb}, new GPUDevice(new AdapterInfo {Description="CPU", DedicatedVideoMemory=(ulong)MemoryMb*1024*1024}, new DeviceInfo("CPU", "", "", "CPU"))) { DeviceCode="cpu", SupportedBackends=[BackendType.OnnxRuntime, BackendType.StableDiffusionCpp], QualityModes=[QualityMode.Draft, QualityMode.Standard, QualityMode.Production], DefaultQualityMode=QualityMode.Standard }};
            try
            {
                var start = new ProcessStartInfo("nvidia-smi") {UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true};
                start.ArgumentList.Add("--query-gpu=index,memory.total,pci.bus_id,uuid,name");
                start.ArgumentList.Add("--format=csv,noheader,nounits");
                using var process = Process.Start(start);
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000)) { process.Kill(true); process.WaitForExit(); return devices; }
                if (process.ExitCode != 0) return devices;
                var visible = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
                foreach (var line in output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = line.Split(',', 5, StringSplitOptions.TrimEntries);
                    if (fields.Length != 5 || !int.TryParse(fields[0], out var index) || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory)) continue;
                    var ordinal = index;
                    if (visible is not null)
                    {
                        var selected = visible.Split(',', StringSplitOptions.TrimEntries);
                        ordinal = Array.FindIndex(selected, x => x == fields[0] || (!string.IsNullOrEmpty(x) && fields[3].StartsWith(x, StringComparison.Ordinal)));
                        if (ordinal < 0) continue;
                    }
                    var bus = Convert.ToInt32(fields[2].Split(':')[1], 16);
                    devices.Add(new DeviceModel(new Device {Id=index+1, DeviceId=ordinal, Type=DeviceType.GPU, Vendor=VendorType.Nvidia, Name=fields[4], Memory=memory},
                        new GPUDevice(new AdapterInfo {Description=fields[4], DedicatedVideoMemory=(ulong)memory*1024*1024}, new DeviceInfo(fields[4], "", $"pci bus {bus},", "GPU")))
                        {DeviceCode="cuda", VendorIndex=ordinal, SupportedBackends=[BackendType.StableDiffusionCpp]});
                }
            }
            catch (Exception) { /* Missing/unavailable NVIDIA driver leaves the CPU usable. */ }
            return devices;
        }
    }
    public interface IHardwareService : IDisposable
    {
        CPUDevice CPUDevice { get; }
        CPUStatus CPUStatus { get; }

        NPUDevice NPUDevice { get; }
        NPUStatus NPUStatus { get; }

        GPUDevice[] GPUDevices { get; }
        GPUStatus[] GPUStatus { get; }

        AdapterInfo[] Adapters { get; }

        void Pause();
        void Resume();

        IReadOnlyList<DeviceModel> GetGPUDevices();
    }



    public record CPUDevice
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public float MemoryTotal { get; set; }
    }

    public record struct CPUStatus
    {
        public CPUStatus(CPUDevice device)
        {
            Id = device.Id;
            Name = device.Name;
            MemoryTotal = device.MemoryTotal;
        }

        public int Id { get; }
        public string Name { get; }

        public float MemoryTotal { get; }




        public int Usage { get; set; }
        public int MemoryUsage { get; set; }
        public float MemoryAvailable { get; set; }
    }

    public record struct DeviceInfo(string Name, string DriverVersion, string Location, string DeviceType);

    public record GPUDevice
    {
        public GPUDevice(AdapterInfo adapter, DeviceInfo deviceInfo)
        {
            Id = (int)adapter.Id;
            AdapterInfo = adapter;
            Name = adapter.Description;
            DeviceType = deviceInfo.DeviceType;
            Location = deviceInfo.Location;
            PCIBusId = GetBusId(deviceInfo.Location);
            HardwareLUID = GetLuid(adapter);
            DriverVersion = deviceInfo.DriverVersion;
            MemoryTotal = adapter.DedicatedVideoMemory / 1024f / 1024f;
            SharedMemoryTotal = adapter.SharedSystemMemory / 1024f / 1024f;
            AdapterId = $"luid_0x{adapter.AdapterLuid.HighPart:X8}_0x{adapter.AdapterLuid.LowPart:X8}_phys_0";
        }

        public int Id { get; }
        public string Name { get; }
        public string DeviceType { get; }
        public string Location { get; }
        public int PCIBusId { get; }
        public long HardwareLUID { get; }
        public string AdapterId { get; }
        public string DriverVersion { get; }
        public float MemoryTotal { get; }
        public float SharedMemoryTotal { get; }
        public AdapterInfo AdapterInfo { get; }

        private static int GetBusId(string location)
        {
            var pciBusId = location.Split(',').First();
            if (string.IsNullOrWhiteSpace(pciBusId))
                return 0;

            if (!int.TryParse(pciBusId.Replace("pci bus ", "", StringComparison.OrdinalIgnoreCase), out var pciid))
                return 0;

            return pciid;
        }

        private static long GetLuid(AdapterInfo adapter)
        {
            try
            {
                uint high = Convert.ToUInt32(adapter.AdapterLuid.HighPart);
                uint low = Convert.ToUInt32(adapter.AdapterLuid.LowPart);
                return ((long)high << 32) | low;
            }
            catch
            {
                return 0;
            }
        }
    }


    public record struct GPUStatus
    {
        public GPUStatus(GPUDevice device)
        {
            Id = device.Id;
            Name = device.Name;
            MemoryTotal = device.MemoryTotal;
            SharedMemoryTotal = device.SharedMemoryTotal;
        }

        public int Id { get; set; }
        public string Name { get; set; }

        public float MemoryTotal { get; set; }
        public float MemoryUsage { get; set; }

        public float SharedMemoryTotal { get; set; }
        public float SharedMemoryUsage { get; set; }

        public float ProcessMemoryTotal { get; set; }
        public float ProcessSharedMemoryUsage { get; set; }

        public int UsageCuda { get; set; }
        public int UsageCompute { get; set; }
        public int UsageCompute1 { get; set; }
        public int UsageGraphics { get; set; }
        public int UsageGraphics1 { get; set; }
    }

    public record NPUDevice
    {
        public NPUDevice(AdapterInfo adapter, DeviceInfo deviceInfo)
        {
            Id = (int)adapter.Id;
            AdapterInfo = adapter;
            Name = adapter.Description;
            DeviceType = deviceInfo.DeviceType;
            DriverVersion = deviceInfo.DriverVersion;
            MemoryTotal = adapter.SharedSystemMemory / 1024f / 1024f;
            AdapterId = $"luid_0x{adapter.AdapterLuid.HighPart:X8}_0x{adapter.AdapterLuid.LowPart:X8}_phys_0";
        }

        public int Id { get; }
        public string Name { get; }
        public string DeviceType { get; }
        public string AdapterId { get; }
        public string DriverVersion { get; }
        public float MemoryTotal { get; }
        public AdapterInfo AdapterInfo { get; }
    }

    public record struct NPUStatus
    {
        public NPUStatus(NPUDevice device)
        {
            Id = device.Id;
            Name = device.Name;
            MemoryTotal = device.MemoryTotal;
        }

        public int Id { get; set; }
        public string Name { get; set; }

        public int Usage { get; set; }
        public float MemoryTotal { get; set; }
        public float MemoryUsage { get; set; }

        public float ProcessMemoryTotal { get; set; }
        public float ProcessSharedMemoryUsage { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.None)]
    public struct AdapterInfo
    {
        public uint Id;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public Luid AdapterLuid;
        public AdapterType Type;

        [MarshalAs(UnmanagedType.I1)]
        public bool IsHardware;

        [MarshalAs(UnmanagedType.I1)]
        public bool IsIntegrated;

        [MarshalAs(UnmanagedType.I1)]
        public bool IsDetachable;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        [MarshalAs(UnmanagedType.I1)]
        public bool IsLegacy;
    }


    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;

        public override bool Equals(object obj)
        {
            return obj is Luid other && this == other;
        }

        public static bool operator ==(Luid left, Luid right)
        {
            return left.LowPart == right.LowPart && left.HighPart == right.HighPart;
        }


        public static bool operator !=(Luid left, Luid right)
        {
            return !(left == right);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(LowPart, HighPart);
        }
    }

    public class LuidComparer : IEqualityComparer<AdapterInfo>
    {
        public bool Equals(AdapterInfo x, AdapterInfo y)
        {
            return x.AdapterLuid.Equals(y.AdapterLuid);
        }

        public int GetHashCode(AdapterInfo obj)
        {
            return obj.AdapterLuid.GetHashCode();
        }
    }



    [Flags]
    public enum AdapterFlags : uint
    {
        None = 0,
        Remote = 1,
        Software = 2
    }

    public enum AdapterType : uint
    {
        GPU = 0,
        NPU = 1,
        Other = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;

        public MemoryStatusEx()
        {
            Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        }
    }
}


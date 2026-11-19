using LibreHardwareMonitor.Hardware;

namespace PhoneBox.Agent;

/// <summary>
/// Reads the CPU and GPU sensors directly with LibreHardwareMonitorLib, for a PC where LibreHardwareMonitor's web server
/// (port 8085) is not running. Windows only. The CPU temperature needs a hardware driver, so the agent must run as
/// administrator for it; GPU sensors from the vendor library (NVAPI, ADL) often work without. A reading that is not
/// available stays null, exactly as when no sensor source exists.
/// </summary>
internal sealed class LhmDirect : IDisposable
{
    private static readonly string[] CpuTempNames = ["Core (Tctl/Tdie)", "Tctl/Tdie", "CPU Package", "Tdie", "Tctl", "Core (Tdie)"];

    private readonly Computer _pc = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private bool _open;

    /// <summary>Opens the sensors; false when the library cannot start at all.</summary>
    public bool Open()
    {
        try { _pc.Open(); _open = true; }
        catch { _open = false; }
        return _open;
    }

    /// <summary>Refreshes every sensor and returns the CPU temperature and the GPUs, as the LibreHardwareMonitor web server would.</summary>
    public (double? CpuTemp, List<GpuInfo> Gpus) Read()
    {
        if (!_open) return (null, []);
        double? cpuTemp = null;
        var gpus = new List<GpuInfo>();
        foreach (var hw in _pc.Hardware)
        {
            Refresh(hw);
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpuTemp ??= CpuTemperature(Sensors(hw));
                    break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    gpus.Add(Gpu(hw));
                    break;
            }
        }
        return (cpuTemp, gpus);
    }

    private static void Refresh(IHardware hw)
    {
        try { hw.Update(); } catch { }
        foreach (var sub in hw.SubHardware) Refresh(sub);
    }

    private static List<ISensor> Sensors(IHardware hw)
    {
        var all = new List<ISensor>(hw.Sensors);
        foreach (var sub in hw.SubHardware) all.AddRange(Sensors(sub));
        return all;
    }

    private static double? CpuTemperature(List<ISensor> sensors)
    {
        // without the driver the library reports 0 for the CPU: no reading, not 0 °C
        var temps = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0).ToList();
        foreach (var name in CpuTempNames)
            if (temps.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value: { } v }) return Math.Round(v, 1);
        // older Intel processors only report their cores: the hottest one
        var cores = temps.Where(s => s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)
            || s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value!.Value).ToList();
        return cores.Count > 0 ? Math.Round(cores.Max(), 1) : null;
    }

    private static GpuInfo Gpu(IHardware hw)
    {
        var sensors = Sensors(hw);
        double? Get(SensorType type, params string[] names)
        {
            foreach (var n in names)
                if (sensors.FirstOrDefault(s => s.SensorType == type && s.Value is not null && (type != SensorType.Temperature || s.Value > 0)
                    && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { Value: { } v })
                    return v;
            return null;
        }
        // LibreHardwareMonitor reports memory in MB (SmallData) or GB (Data); the agent uses GB
        double? MemGb(params string[] names)
        {
            foreach (var n in names)
                if (sensors.FirstOrDefault(s => (s.SensorType is SensorType.SmallData or SensorType.Data) && s.Value is not null && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { Value: { } v } s)
                    return s.SensorType == SensorType.SmallData ? v / 1024.0 : v;
            return null;
        }
        var vendor = hw.HardwareType switch { HardwareType.GpuNvidia => "nvidia", HardwareType.GpuAmd => "amd", _ => "intel" };
        return new GpuInfo(hw.Name,
            Util: Get(SensorType.Load, "GPU Core"),
            Temp: Get(SensorType.Temperature, "GPU Core"),
            Power: Get(SensorType.Power, "GPU Package", "GPU Total", "GPU Power", "GPU PPT", "GPU Core"),
            PowerLimit: null,
            VramUsed: MemGb("GPU Memory Used", "D3D Dedicated Memory Used"),
            VramTotal: MemGb("GPU Memory Total", "D3D Dedicated Memory Total"),
            Fan: Get(SensorType.Control, "GPU Fan"),
            Clock: Get(SensorType.Clock, "GPU Core"),
            Vendor: vendor,
            Source: "lhm",
            Integrated: false, // the Windows adapter list knows which GPU is integrated, and the merge keeps its answer
            HotSpot: Get(SensorType.Temperature, "GPU Hot Spot"),
            MemTemp: Get(SensorType.Temperature, "GPU Memory Junction", "GPU Memory"),
            MemClock: Get(SensorType.Clock, "GPU Memory"),
            SharedUsed: MemGb("D3D Shared Memory Used"),
            SharedTotal: MemGb("D3D Shared Memory Total"),
            FanRpm: sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Value is not null && s.Name.StartsWith("GPU Fan", StringComparison.OrdinalIgnoreCase))?.Value);
    }

    public void Dispose()
    {
        try { _pc.Close(); } catch { }
    }
}

using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

if (args.Length == 3 && args[0] == "--install-task")
{
    return await InstallScheduledTaskAsync(args[1], args[2]);
}

if (args.Length != 3 || args[0] != "--pipe" || !int.TryParse(args[2], out var parentProcessId)) return 2;
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.Out, PipeOptions.Asynchronous);
try { await pipe.ConnectAsync(15000); }
catch { return 3; }

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
    IsStorageEnabled = true,
    IsPowerMonitorEnabled = true
};
computer.Open();
try
{
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
    while (pipe.IsConnected)
    {
        if (parentProcessId > 0)
        {
            try { using var parent = Process.GetProcessById(parentProcessId); }
            catch { break; }
        }
        computer.Accept(new UpdateVisitor());
        var sensors = computer.Hardware.SelectMany(Flatten).SelectMany(item => item.Sensors)
            .Where(sensor => sensor.Value.HasValue)
            .Select(sensor => new SensorValue(
                sensor.Hardware.HardwareType.ToString(),
                sensor.Hardware.Name,
                sensor.Hardware.Identifier.ToString(),
                sensor.Name,
                sensor.SensorType.ToString(),
                sensor.Value!.Value))
            .ToArray();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new SensorSnapshot(DateTimeOffset.UtcNow, sensors)));
        await Task.Delay(1000);
    }
}
finally { computer.Close(); }
return 0;

static async Task<int> InstallScheduledTaskAsync(string taskName, string pipeName)
{
    var helperPath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(helperPath)) return 5;
    try
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null) return 6;
        dynamic scheduler = Activator.CreateInstance(schedulerType)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        dynamic definition = scheduler.NewTask(0);
        definition.RegistrationInfo.Description = "X-Tool 高级硬件传感器按需授权任务";
        definition.Settings.Enabled = true;
        definition.Settings.Hidden = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 3;
        definition.Principal.RunLevel = 1;
        definition.Principal.LogonType = 3;
        dynamic action = definition.Actions.Create(0);
        action.Path = helperPath;
        action.Arguments = $"--pipe {pipeName} 0";
        folder.RegisterTaskDefinition(taskName, definition, 6, null, null, 3, null);
        await Task.CompletedTask;
        return 0;
    }
    catch { return 7; }
}

static IEnumerable<IHardware> Flatten(IHardware hardware)
{
    yield return hardware;
    foreach (var child in hardware.SubHardware)
        foreach (var nested in Flatten(child)) yield return nested;
}

sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var child in hardware.SubHardware) child.Accept(this); }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

sealed record SensorSnapshot(DateTimeOffset CapturedAt, SensorValue[] Sensors);
sealed record SensorValue(string HardwareType, string HardwareName, string HardwareIdentifier, string Name, string Type, float Value);

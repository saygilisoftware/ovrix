using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Ovrix.Engine;

namespace Ovrix;

public partial class MainWindow
{
    private void RefreshMetrics()
    {
        if (playback is { } run)
        {
            latest = run.Snapshot();
            if (latest.SourceIntervalMs > 0)
            {
                frameIntervals.Enqueue(latest.SourceIntervalMs);
                if (frameIntervals.Count > 64) frameIntervals.Dequeue();
            }
        }
        UpdateReadouts();
        if (graph is not null)
        {
            var values = frameIntervals.ToArray();
            graph.Points = new PointCollection(values.Select((value, index) =>
                new Point(4 + index * 252d / 63, 79 - Math.Clamp(value, 0, 65) * 75 / 65)));
        }
        if (controllerDetails is not null)
        {
            var pad = ControllerInput.Read();
            controllerDetails.Text = pad.Name.Length == 0 ? L("Kumanda algılanmadı.", "No controller detected.") :
                $"{pad.Name}\nL  {pad.LeftX}, {pad.LeftY}   R  {pad.RightX}, {pad.RightY}" +
                $"\nL2 {pad.LeftTrigger}   R2 {pad.RightTrigger} · 0x{pad.Buttons:X4}";
        }
        if (dashboardDetails is not null)
        {
            dashboardDetails.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
            if (session is not null)
            {
                var state = system.Read();
                _ = system.RefreshGpu(appLifetime.Token);
                var subtitle = Config.Translate
                    ? translationStatus.Length > 0 ? translationStatus : L("OCR bekleniyor", "Waiting for OCR")
                    : L("Çeviri kapalı", "Translation off");
                dashboardDetails.Text = state.Gpu is { } gpu
                    ? $"GPU {gpu.Utilization:0}% · {subtitle}" : subtitle;
            }
        }
        if (monitorDetails is not null)
        {
            var state = system.Read();
            _ = system.RefreshGpu(appLifetime.Token);
            var m = latest;
            var items = new List<string>();
            if (m is not null)
            {
                items.Add(L($"Üretim süresi {m.GenerationMs:0.00} ms", $"Generation time {m.GenerationMs:0.00} ms"));
                items.Add(L($"Video hattı {m.PipelineMs:0.00} ms", $"Video pipeline {m.PipelineMs:0.00} ms"));
                items.Add(L($"Kare iletimi {m.DeliveryMs:0.00} ms", $"Frame delivery {m.DeliveryMs:0.00} ms"));
                items.Add(L($"Kayıp kare {m.Dropped}", $"Dropped frames {m.Dropped}"));
                items.Add($"OCR {m.OcrMs:0.0} ms · " + L($"Çeviri {m.TranslationMs:0.0} ms", $"Translation {m.TranslationMs:0.0} ms"));
                items.Add(L($"Ses tamponu {m.AudioBufferMs:0} ms", $"Audio buffer {m.AudioBufferMs:0} ms"));
                items.Add(m.Gpu ? "Direct3D 11" : L("Yazılım işleme", "Software processing"));
                if (m.Network is { } n) items.Add($"RTT {n.RoundTripMs:0.0} ms · {n.Lost}/{n.Received} " +
                    L("kayıp/alınan paket", "packets lost/received"));
            }
            items.Add(L($"OVRIX CPU %{state.Cpu:0.0} · RAM {state.Ram:0} MB", $"OVRIX CPU {state.Cpu:0.0}% · RAM {state.Ram:0} MB"));
            if (state.Gpu is { } gpu) items.Add($"GPU {gpu.Utilization:0}% · VRAM {gpu.Memory:0} MiB");
            if (translationStatus.Length > 0) items.Add(translationStatus);
            monitorDetails.Text = string.Join("\n", items);
        }
    }
}

internal sealed class SystemProbe
{
    private double lastCpu = -1;
    private long lastTime = Stopwatch.GetTimestamp();
    private GpuState? gpu;
    private bool refreshing;
    internal sealed record GpuState(double Utilization, double Memory);
    internal (double Cpu, double Ram, GpuState? Gpu) Read()
    {
        using var process = Process.GetCurrentProcess();
        var now = Stopwatch.GetTimestamp();
        var cpu = process.TotalProcessorTime.TotalMilliseconds;
        var elapsed = Stopwatch.GetElapsedTime(lastTime, now).TotalMilliseconds;
        var percent = lastCpu < 0 || elapsed < 1 ? 0 :
            Math.Clamp((cpu - lastCpu) / elapsed / Environment.ProcessorCount * 100, 0, 100);
        lastCpu = cpu;
        lastTime = now;
        return (percent, process.WorkingSet64 / 1048576d, gpu);
    }
    internal async Task RefreshGpu(CancellationToken token)
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exe)) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(1.5));
            var result = await ProcessTools.CaptureAsync(exe,
                ["--query-gpu=utilization.gpu,memory.used", "--format=csv,noheader,nounits", "-i", "0"], deadline.Token);
            var fields = result.Output.Trim().Split(',', StringSplitOptions.TrimEntries);
            if (result.ExitCode == 0 && fields.Length == 2 &&
                double.TryParse(fields[0], CultureInfo.InvariantCulture, out var usage) &&
                double.TryParse(fields[1], CultureInfo.InvariantCulture, out var memory))
                gpu = new(usage, memory);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception) { }
        finally { refreshing = false; }
    }
}

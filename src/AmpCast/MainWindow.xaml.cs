using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AmpCast.Audio;
using AmpCast.Controls;
using AmpCast.Setup;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Forms = System.Windows.Forms;

namespace AmpCast;

public partial class MainWindow : Window
{
    private sealed record DeviceItem(string Id, string Name);

    private sealed record Snapshot(
        bool DawConnected, string DawHost, double DawRate, int DawBuffer,
        string MicName, int MicRate, string OutName, int OutRate,
        bool Running, double BufferedMs, double DriftPpm, int Dropouts, string? Error,
        bool Stereo, int ShareState, double ShareMs);

    private const int WaveBarCount = 22;

    private readonly bool _demo;
    private readonly AppSettings _settings;
    private readonly AudioEngine? _engine;
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly DeviceWatcher? _watcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _deviceRefreshTimer;

    private readonly MeterBallistics _dawL = new(), _dawR = new(), _mic = new(), _outL = new(), _outR = new();
    private readonly Rectangle[] _waveBars = new Rectangle[WaveBarCount];
    private readonly double[] _waveHistory = new double[WaveBarCount];
    private double _waveTimer, _wavePeak, _demoTime;
    private TimeSpan _lastRenderTime;
    private DateTime _clipUntil;

    private bool _loading = true;
    private bool _exiting;
    private string? _activeMicId, _activeOutId;
    private string? _deviceError;
    private DateTime? _shareOffSince;
    private string _warningsKey = string.Empty;

    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayBroadcast;

    public MainWindow(bool demo)
    {
        _demo = demo;
        InitializeComponent();
        BuildWaveBars();

        _settings = demo
            ? new AppSettings { Broadcast = true, StereoStream = Environment.GetCommandLineArgs().Contains("--stereo") }
            : AppSettings.Load();
        DawSlider.Value = _settings.DawVolume;
        MicSlider.Value = _settings.MicVolume;
        Lock48Check.IsChecked = demo || DriverConfig.Lock48k;
        BroadcastToggle.IsChecked = _settings.Broadcast;
        StereoCheck.IsChecked = _settings.StereoStream;

        if (demo)
        {
            FillDemoDevices();
        }
        else
        {
            _engine = new AudioEngine();
            _engine.DeviceFailed += message => Dispatcher.InvokeAsync(() =>
            {
                _deviceError = message;
                ScheduleDeviceRefresh();
            });
            _engine.Broadcast = _settings.Broadcast;
            _engine.StereoStream = _settings.StereoStream;
            ApplyGains();

            _enumerator = new MMDeviceEnumerator();
            LoadAsioDrivers();
            RefreshDeviceLists(forceRestart: true);

            _watcher = new DeviceWatcher(() => Dispatcher.InvokeAsync(ScheduleDeviceRefresh));
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
            CreateTray();
        }

        _loading = false;
        UpdateBroadcastVisuals();

        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _deviceRefreshTimer.Tick += (_, _) =>
        {
            _deviceRefreshTimer.Stop();
            RefreshDeviceLists(forceRestart: _deviceError is not null);
        };

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                                           (_, _) => RefreshStatus(), Dispatcher);
        _statusTimer.Start();
        CompositionTarget.Rendering += OnRendering;
        SourceInitialized += (_, _) => ApplyWindowCorners();
        RefreshStatus();
    }

    // ================================================================ devices

    private void LoadAsioDrivers()
    {
        var drivers = AsioRegistration.GetHardwareDrivers().Select(n => new DeviceItem(n, n)).ToList();
        AsioCombo.ItemsSource = drivers;

        string? configured = DriverConfig.AsioDevice;
        DeviceItem? selected = drivers.FirstOrDefault(d => d.Id.Equals(configured, StringComparison.OrdinalIgnoreCase))
                               ?? drivers.FirstOrDefault();
        AsioCombo.SelectedItem = selected;
        if (selected is not null && !selected.Id.Equals(configured, StringComparison.OrdinalIgnoreCase))
            DriverConfig.AsioDevice = selected.Id;
    }

    private void RefreshDeviceLists(bool forceRestart)
    {
        if (_enumerator is null) return;
        _loading = true;

        var mics = new List<DeviceItem> { new(string.Empty, "Без микрофона") };
        var outs = new List<DeviceItem>();
        mics.AddRange(Enumerate(DataFlow.Capture));
        outs.AddRange(Enumerate(DataFlow.Render));

        // Microphone: saved choice → otherwise the Windows communications mic (never a virtual cable).
        DeviceItem mic;
        if (_settings.MicDeviceId == string.Empty)
            mic = mics[0];
        else if (_settings.MicDeviceId is not null)
            mic = mics.FirstOrDefault(m => m.Id == _settings.MicDeviceId) ?? mics[0];
        else
            mic = DefaultMic(mics);

        // Output: saved choice → otherwise the first virtual cable we recognise.
        DeviceItem? output = outs.FirstOrDefault(o => o.Id == _settings.OutputDeviceId)
                             ?? outs.FirstOrDefault(o => o.Name.Contains("AmpCast", StringComparison.OrdinalIgnoreCase))
                             ?? outs.FirstOrDefault(o => o.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                             ?? outs.FirstOrDefault(o => LooksVirtual(o.Name));

        MicCombo.ItemsSource = mics;
        OutputCombo.ItemsSource = outs;
        MicCombo.SelectedItem = mic;
        OutputCombo.SelectedItem = output;
        _loading = false;

        string? micId = string.IsNullOrEmpty(mic.Id) ? null : mic.Id;
        if (forceRestart || micId != _activeMicId || output?.Id != _activeOutId)
            RestartEngine();
    }

    private IEnumerable<DeviceItem> Enumerate(DataFlow flow)
    {
        var list = new List<DeviceItem>();
        try
        {
            foreach (var device in _enumerator!.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    try { list.Add(new DeviceItem(device.ID, device.FriendlyName)); }
                    catch (COMException) { /* device vanished while enumerating */ }
                }
            }
        }
        catch (COMException) { }
        return list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private DeviceItem DefaultMic(List<DeviceItem> mics)
    {
        try
        {
            using var def = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            var match = mics.FirstOrDefault(m => m.Id == def.ID);
            if (match is not null && !LooksVirtual(match.Name)) return match;
        }
        catch (COMException) { /* no capture devices */ }
        return mics.Skip(1).FirstOrDefault(m => !LooksVirtual(m.Name)) ?? mics[0];
    }

    private static bool LooksVirtual(string name) =>
        name.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ampcast", StringComparison.OrdinalIgnoreCase);

    private void RestartEngine()
    {
        if (_engine is null) return;
        string? micId = (MicCombo.SelectedItem as DeviceItem)?.Id;
        if (string.IsNullOrEmpty(micId)) micId = null;
        string? outId = (OutputCombo.SelectedItem as DeviceItem)?.Id;

        _deviceError = null;
        _engine.Start(micId, outId);
        SyncShareDevice(outId);
        _activeMicId = micId;
        _activeOutId = outId;
        UpdateBroadcastVisuals();
        RefreshStatus();
    }

    /// <summary>The driver plays the stereo screen-share output to the same device AmpCast uses.</summary>
    private void SyncShareDevice(string? outId)
    {
        if (_engine is null || string.IsNullOrEmpty(outId)) return;
        if (DriverConfig.ShareDevice == outId) return;
        DriverConfig.ShareDevice = outId;
        _engine.ReopenShareDevice();
    }

    private void ScheduleDeviceRefresh()
    {
        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Start();
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _demo) return;
        if (sender == MicCombo && MicCombo.SelectedItem is DeviceItem mic)
            _settings.MicDeviceId = mic.Id;                 // "" = no microphone
        if (sender == OutputCombo && OutputCombo.SelectedItem is DeviceItem output)
            _settings.OutputDeviceId = output.Id;
        _settings.Save();
        RestartEngine();
    }

    private void AsioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _demo || AsioCombo.SelectedItem is not DeviceItem item) return;
        DriverConfig.AsioDevice = item.Id;
        SharedDawBus.SignalDriverReset();   // a running DAW re-opens AmpCast ASIO with the new interface
    }

    private void StereoCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool stereo = StereoCheck.IsChecked == true;
        if (_engine is not null) _engine.StereoStream = stereo;
        SyncShareDevice(_activeOutId);
        UpdateBroadcastVisuals();
        if (_loading || _demo) return;
        _settings.StereoStream = stereo;
        _settings.Save();
    }

    private void Lock48Check_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _demo) return;
        DriverConfig.Lock48k = Lock48Check.IsChecked == true;
        SharedDawBus.SignalDriverReset();
    }

    private void SoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); }
        catch { /* not critical */ }
    }

    // ================================================================ mixer

    private static double SliderToGain(double v) => Math.Pow(v / 70.0, 3);   // 70 = 0 dB, 100 ≈ +9 dB

    private static string GainText(double v)
    {
        if (v <= 0.5) return "−∞ dB";
        double db = 20 * Math.Log10(SliderToGain(v));
        return (db >= 0.05 ? "+" : db <= -0.05 ? "−" : "") + Math.Abs(db).ToString("0.0") + " dB";
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DawDbText is null || MicDbText is null) return;   // during InitializeComponent
        DawDbText.Text = GainText(DawSlider.Value);
        MicDbText.Text = GainText(MicSlider.Value);
        ApplyGains();
        if (_loading || _demo) return;
        _settings.DawVolume = Math.Round(DawSlider.Value);
        _settings.MicVolume = Math.Round(MicSlider.Value);
        _settings.Save();
    }

    private void ApplyGains()
    {
        if (_engine is null) return;
        _engine.DawGain = (float)SliderToGain(DawSlider.Value);
        _engine.MicGain = (float)SliderToGain(MicSlider.Value);
    }

    private void BroadcastToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool on = BroadcastToggle.IsChecked == true;
        if (_engine is not null) _engine.Broadcast = on;
        if (_trayBroadcast is not null) _trayBroadcast.Checked = on;
        UpdateBroadcastVisuals();
        if (_loading || _demo) return;
        _settings.Broadcast = on;
        _settings.Save();
    }

    private void UpdateBroadcastVisuals()
    {
        if (HeroBadge is null) return;
        bool on = BroadcastToggle.IsChecked == true;
        string output = _demo ? "CABLE Input" : ShortName(_engine?.OutputName);

        HeroBadge.Text = on ? "В ЭФИРЕ" : "НЕ В ЭФИРЕ";
        HeroBadgeDot.Fill = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x80, 0x84, 0x8E));
        HeroTitle.Text = on ? "Вас слышат в Discord" : "Трансляция выключена";
        bool stereo = StereoCheck.IsChecked == true;
        HeroSubtitle.Text = string.IsNullOrEmpty(output)
            ? "Выберите виртуальный кабель, чтобы начать"
            : stereo ? "DAW → демонстрация окна DAW в Discord · стерео"
                     : $"DAW + микрофон → {output} · моно";

        // Stereo mode: the microphone goes to Discord directly, AmpCast carries the DAW only.
        MicStrip.Opacity = stereo ? 0.4 : 1.0;
        MicStrip.IsEnabled = !stereo;
        MicCombo.IsEnabled = !stereo;
        MicLabel.Text = stereo ? "Микрофон — идёт в Discord напрямую" : "Микрофон (голос)";
        OutputLabel.Text = stereo ? "Выход для DAW (виртуальный кабель)" : "Выход в Discord (виртуальный кабель)";
        Step2Text.Text = stereo
            ? "Discord → Голос и видео → Устройство ввода: ваш микрофон (не кабель)."
            : "Discord → Голос и видео → Устройство ввода: виртуальный кабель.";
        Step3Text.Text = stereo
            ? "Демонстрация экрана → окно вашей DAW, звук включён."
            : "Отключите в Discord шумоподавление и эхоподавление.";

        OutBadge.Background = on ? (Brush)FindResource("DangerBrush") : new SolidColorBrush(Color.FromRgb(0x38, 0x3A, 0x40));
        OutBadgeText.Text = on ? "● LIVE" : "ВЫКЛ";
        OutBadgeText.Foreground = on ? Brushes.White : (Brush)FindResource("TextSecondaryBrush");

        Brush barBrush = on ? new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) : (Brush)FindResource("AccentGradientVertical");
        foreach (var bar in _waveBars) bar.Fill = barBrush;

        if (_tray is not null) _tray.Text = on ? "AmpCast — в эфире" : "AmpCast";
    }

    // ================================================================ status

    private Snapshot TakeSnapshot()
    {
        if (_demo)
            return new Snapshot(true, "FL64.exe", 48000, 128, "Микрофон (Rode NT-USB)", 48000,
                                "CABLE Input (VB-Audio Virtual Cable)", 48000, true, 31, 12, 0, null,
                                StereoCheck.IsChecked == true, SharedDawBus.ShareRunning, 31);
        var e = _engine!;
        return new Snapshot(e.DawConnected, e.DawConnected ? e.DawHostName : string.Empty, e.DawSampleRate, e.DawBufferSize,
                            e.MicName, e.MicSampleRate, e.OutputName, e.OutputSampleRate, e.IsRunning,
                            e.DawBufferedMs, e.DawDriftPpm, e.Dropouts, _deviceError ?? e.LastError,
                            e.StereoStream, e.ShareState, e.ShareBufferedMs);
    }

    private void RefreshStatus()
    {
        var s = TakeSnapshot();
        var green = (Brush)FindResource("SuccessBrush");
        var yellow = (Brush)FindResource("WarningBrush");
        var red = (Brush)FindResource("DangerBrush");
        var grey = new SolidColorBrush(Color.FromRgb(0x4E, 0x50, 0x58));
        bool driverRegistered = _demo || AsioRegistration.IsRegistered();
        string asioName = (AsioCombo.SelectedItem as DeviceItem)?.Name ?? "—";

        // DAW
        if (s.DawConnected)
        {
            DawDot.Fill = green;
            DawStatusText.Text = $"{s.DawHost} · {s.DawBuffer} smp · {s.DawRate / 1000:0.#} кГц";
        }
        else
        {
            DawDot.Fill = driverRegistered ? grey : red;
            DawStatusText.Text = driverRegistered ? "выберите «AmpCast ASIO» в DAW" : "драйвер не установлен";
        }

        // Mic / cable
        MicDot.Fill = s.MicRate > 0 ? green : grey;
        MicStatusText.Text = s.MicRate > 0 ? ShortName(s.MicName) : "выключен";
        // Stereo mode: the driver inside the DAW plays the mix; report its state.
        if (s.Stereo && s.DawConnected && s.ShareState == SharedDawBus.ShareOff)
            _shareOffSince ??= DateTime.UtcNow;
        else
            _shareOffSince = null;
        bool oldDriver = _shareOffSince is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(3);
        CableTitle.Text = s.Stereo ? "Стерео из DAW" : "Discord-кабель";
        if (!s.Stereo)
        {
            CableDot.Fill = s.Running ? green : red;
            CableStatusText.Text = s.Running ? ShortName(s.OutName) : "не выбран";
        }
        else if (!s.DawConnected)
        {
            CableDot.Fill = grey;
            CableStatusText.Text = "ждёт DAW";
        }
        else
        {
            (CableDot.Fill, CableStatusText.Text) = s.ShareState switch
            {
                SharedDawBus.ShareRunning => (green, "играет — покажите окно DAW"),
                SharedDawBus.ShareWaiting => (yellow, "ждёт звук DAW"),
                SharedDawBus.ShareNoDevice => (red, "выберите выход"),
                SharedDawBus.ShareOff => oldDriver ? (red, "перезапустите DAW") : (yellow, "запуск…"),
                _ => (red, $"ошибка 0x{s.ShareState:X8}"),
            };
        }

        // Sample rates
        var rates = new List<string>();
        bool mismatch = false;
        if (s.DawConnected && Math.Abs(s.DawRate - AudioEngine.RequiredSampleRate) > 0.5) { mismatch = true; rates.Add($"DAW {s.DawRate / 1000:0.#}"); }
        if (s.MicRate > 0 && s.MicRate != AudioEngine.RequiredSampleRate) { mismatch = true; rates.Add($"мик. {s.MicRate / 1000.0:0.#}"); }
        if (s.OutRate > 0 && s.OutRate != AudioEngine.RequiredSampleRate) { mismatch = true; rates.Add($"кабель {s.OutRate / 1000.0:0.#}"); }
        RateDot.Fill = mismatch ? yellow : s.Running ? green : grey;
        RateStatusText.Text = mismatch ? "ресэмплинг: " + string.Join(", ", rates) + " кГц" : "48 кГц везде";

        // Warnings
        var warnings = new List<(Brush brush, string text)>();
        if (s.Error is not null) warnings.Add((red, s.Error));
        if (!driverRegistered) warnings.Add((red, "AmpCast ASIO не зарегистрирован. Переустановите AmpCast (нужны права администратора)."));
        if (!_demo && AsioCombo.Items.Count == 0) warnings.Add((yellow, "Не найден ни один ASIO-драйвер звуковой карты. Установите драйвер вашего аудиоинтерфейса."));
        if (s.Running && !LooksVirtual(s.OutName))
            warnings.Add((yellow, s.Stereo
                ? $"«{ShortName(s.OutName)}» — не виртуальный кабель: DAW будет звучать на нём второй раз. Выберите VB-CABLE или AmpCast Cable."
                : $"«{ShortName(s.OutName)}» — не виртуальный кабель: Discord его не услышит. Установите AmpCast Cable или VB-CABLE."));
        if (oldDriver)
            warnings.Add((red, "DAW использует старую версию драйвера AmpCast ASIO. Закройте и снова откройте DAW."));
        if (s.Stereo && s.ShareState < 0)
            warnings.Add((red, $"Стерео-выход DAW не открылся (0x{s.ShareState:X8}). Выберите другой выход или перезапустите DAW."));
        if (s.MicRate > 0 && LooksVirtual(s.MicName)) warnings.Add((yellow, "Микрофоном выбран виртуальный кабель — возможна петля и эхо."));
        if (s.OutRate > 0 && s.OutRate != AudioEngine.RequiredSampleRate) warnings.Add((yellow, $"Кабель работает на {s.OutRate / 1000.0:0.#} кГц. AmpCast пересчитывает звук, но лучше выставить 48 кГц в «Звук Windows»."));
        if (s.MicRate > 0 && s.MicRate != AudioEngine.RequiredSampleRate) warnings.Add((yellow, $"Микрофон работает на {s.MicRate / 1000.0:0.#} кГц — включён ресэмплинг. Рекомендуется 48 кГц."));
        if (s.DawConnected && Math.Abs(s.DawRate - AudioEngine.RequiredSampleRate) > 0.5) warnings.Add((yellow, $"Проект DAW на {s.DawRate / 1000:0.#} кГц. Для стабильной работы переключите проект на 48 кГц."));
        if (s.DawConnected && s.DawBuffer > 256) warnings.Add((yellow, $"Буфер ASIO {s.DawBuffer} сэмплов — будет заметная задержка. Для игры поставьте 64–128."));
        UpdateWarnings(warnings);

        // Footer
        FooterText.Text = s.DawConnected
            ? s.Stereo
                ? $"AmpCast ASIO → {asioName} · {s.DawBuffer} smp @ {s.DawRate:0} Гц · стерео-выход из DAW ≈ {s.ShareMs:0} мс"
                : $"AmpCast ASIO → {asioName} · {s.DawBuffer} smp @ {s.DawRate:0} Гц · буфер Discord ≈ {s.BufferedMs:0} мс · дрейф {s.DriftPpm:+0;-0;0} ppm · сбоев: {s.Dropouts}"
            : $"AmpCast ASIO → {asioName} · ожидание DAW";
    }

    private void UpdateWarnings(List<(Brush brush, string text)> warnings)
    {
        string key = string.Join("|", warnings.Select(w => w.text));
        if (key == _warningsKey) return;
        _warningsKey = key;
        WarningsPanel.Children.Clear();
        foreach (var (brush, text) in warnings.Take(3))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new TextBlock { Text = "", Foreground = brush, Margin = new Thickness(2, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            icon.Style = (Style)FindResource("Icon");
            icon.Foreground = brush;
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = (Brush)FindResource("TextSecondaryBrush") };
            Grid.SetColumn(label, 1);
            row.Children.Add(icon);
            row.Children.Add(label);
            WarningsPanel.Children.Add(row);
        }
    }

    private static string ShortName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return name.Length > 42 ? name[..41] + "…" : name;
    }

    // ================================================================ meters (60 fps)

    private void BuildWaveBars()
    {
        for (int i = 0; i < WaveBarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 4,
                Height = 6,
                RadiusX = 2,
                RadiusY = 2,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _waveBars[i] = bar;
            WaveBars.Items.Add(bar);
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastRenderTime == default ? 1 / 60.0 : (now - _lastRenderTime).TotalSeconds;
        if (dt <= 0) return;
        _lastRenderTime = now;
        dt = Math.Min(dt, 0.1);

        float dl, dr, m, outL, outR;
        if (_engine is not null)
        {
            MeterBank b = _engine.Meters;
            dl = MeterBank.Take(ref b.DawL);
            dr = MeterBank.Take(ref b.DawR);
            m = MeterBank.Take(ref b.Mic);
            outL = MeterBank.Take(ref b.OutL);
            outR = MeterBank.Take(ref b.OutR);
            if (Interlocked.Exchange(ref b.Clip, 0) != 0) _clipUntil = DateTime.UtcNow.AddSeconds(1.5);
        }
        else
        {
            DemoLevels(dt, out dl, out dr, out m, out outL, out outR);
        }

        _dawL.Update(dl, dt); _dawR.Update(dr, dt); _mic.Update(m, dt); _outL.Update(outL, dt); _outR.Update(outR, dt);
        Apply(DawMeterL, _dawL); Apply(DawMeterR, _dawR); Apply(MicMeter, _mic); Apply(OutMeterL, _outL); Apply(OutMeterR, _outR);

        bool clip = DateTime.UtcNow < _clipUntil;
        ClipText.Text = clip ? "перегруз!" : "без перегруза";
        ClipText.Foreground = (Brush)FindResource(clip ? "DangerBrush" : "TextMutedBrush");

        // Hero waveform: scrolling history of the Discord output level.
        _wavePeak = Math.Max(_wavePeak, Math.Max(outL, outR));
        _waveTimer += dt;
        if (_waveTimer >= 0.045)
        {
            _waveTimer = 0;
            Array.Copy(_waveHistory, 1, _waveHistory, 0, WaveBarCount - 1);
            _waveHistory[WaveBarCount - 1] = VuMeter.ToFraction(_wavePeak);
            _wavePeak = 0;
            for (int i = 0; i < WaveBarCount; i++)
                _waveBars[i].Height = 6 + Math.Pow(_waveHistory[i], 2.2) * 76;
        }
    }

    private static void Apply(VuMeter meter, MeterBallistics b)
    {
        meter.Level = b.Level;
        meter.Peak = b.Peak;
    }

    private void DemoLevels(double dt, out float dl, out float dr, out float m, out float outL, out float outR)
    {
        _demoTime += dt;
        double t = _demoTime;
        double strum = Math.Pow(Math.Abs(Math.Sin(t * 2.1)), 3);
        dl = (float)(0.25 + 0.45 * strum + 0.05 * Math.Sin(t * 37));
        dr = (float)(0.22 + 0.42 * strum + 0.05 * Math.Cos(t * 41));
        m = (float)(0.08 + 0.18 * Math.Abs(Math.Sin(t * 5.3)) * Math.Abs(Math.Sin(t * 0.7)));
        outL = Math.Min(0.95f, dl + m * 0.6f);
        outR = Math.Min(0.95f, dr + m * 0.6f);
    }

    private void FillDemoDevices()
    {
        AsioCombo.ItemsSource = new[] { new DeviceItem("a", "Audient USB Audio ASIO Driver") };
        MicCombo.ItemsSource = new[] { new DeviceItem("m", "Микрофон (Rode NT-USB)") };
        OutputCombo.ItemsSource = new[] { new DeviceItem("o", "CABLE Input (VB-Audio Virtual Cable)") };
        AsioCombo.SelectedIndex = MicCombo.SelectedIndex = OutputCombo.SelectedIndex = 0;
        for (int i = 0; i < WaveBarCount; i++)
            _waveHistory[i] = 0.35 + 0.4 * Math.Abs(Math.Sin(i * 0.55)) * Math.Abs(Math.Cos(i * 0.21));
        for (int i = 0; i < WaveBarCount; i++)
            _waveBars[i].Height = 6 + Math.Pow(_waveHistory[i], 2.2) * 76;
    }

    // ================================================================ window / tray

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && !_demo)
        {
            e.Cancel = true;      // like Discord: closing keeps AmpCast running in the tray
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    public void StartInTray()
    {
        // Window stays hidden; the engine is already running.
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray()
    {
        if (_tray is null)
        {
            WindowState = WindowState.Minimized;
            return;
        }
        Hide();
        if (!_settings.TrayHintShown)
        {
            _tray.ShowBalloonTip(4000, "AmpCast работает в трее",
                "Трансляция продолжается. Щёлкните по значку, чтобы открыть окно.", Forms.ToolTipIcon.Info);
            _settings.TrayHintShown = true;
            _settings.Save();
        }
    }

    private void CreateTray()
    {
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть AmpCast", null, (_, _) => ShowFromTray());
        _trayBroadcast = new Forms.ToolStripMenuItem("Транслировать в Discord") { Checked = BroadcastToggle.IsChecked == true };
        _trayBroadcast.Click += (_, _) => BroadcastToggle.IsChecked = BroadcastToggle.IsChecked != true;
        menu.Items.Add(_trayBroadcast);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApplication());

        _tray = new Forms.NotifyIcon { Icon = icon, Text = "AmpCast", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowFromTray();
        };
    }

    private void ExitApplication()
    {
        _exiting = true;
        CompositionTarget.Rendering -= OnRendering;
        _statusTimer.Stop();
        _settings.Save();
        if (_enumerator is not null && _watcher is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        }
        _engine?.Dispose();
        _enumerator?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Close();
        Application.Current.Shutdown();
    }

    // Windows 11: rounded window corners for the borderless window.
    private void ApplyWindowCorners()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int preference = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Windows 10: square corners
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dev helper (--render-preview): renders the window to a PNG.</summary>
    public void SavePreview(string path)
    {
        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)(Root.ActualWidth * scale), (int)(Root.ActualHeight * scale),
                                            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Forwards Windows audio device changes (plug/unplug, default change).</summary>
    private sealed class DeviceWatcher : IMMNotificationClient
    {
        private readonly Action _changed;
        public DeviceWatcher(Action changed) => _changed = changed;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string pwstrDeviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

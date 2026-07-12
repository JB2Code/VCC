using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using V60Control.Models;
using V60Control.Services;
using V60Control.Visca;

namespace V60Control;

public partial class MainWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private readonly ViscaClient _visca = new();
    private AppSettings _settings = new();
    private readonly ObservableCollection<Preset> _presets = [];
    private readonly HashSet<Key> _heldKeys = [];
    private bool _padActive;
    private bool _autoFocus;

    private int PtSpeed => (int)PtSpeedSlider.Value;
    private int TiltSpeed => Math.Clamp((int)(PtSpeedSlider.Value * 20 / 24), 1, 20);
    private int ZoomSpeed => (int)ZoomSpeedSlider.Value;

    public MainWindow()
    {
        InitializeComponent();
        PresetList.ItemsSource = _presets;

        LoadState();
        WirePadButtons();

        _visca.ConnectionChanged += connected => Dispatcher.Invoke(() => UpdateStatus(connected));

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        IpBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Connect_Click(this, new RoutedEventArgs()); };
    }

    // ------------------------------------------------------------------
    //  Initialisierung / Persistenz
    // ------------------------------------------------------------------

    private void LoadState()
    {
        _settings = Storage.LoadSettings();
        IpBox.Text = _settings.CameraIp;
        LatencySlider.Value = _settings.LatencyMs;
        PtSpeedSlider.Value = _settings.PanTiltSpeed;
        ZoomSpeedSlider.Value = _settings.ZoomSpeed;
        RtspTcpCheck.IsChecked = _settings.RtspOverTcp;
        MuteCheck.IsChecked = _settings.AudioMuted;
        MainStreamRadio.IsChecked = _settings.StreamNumber == 1;
        SubStreamRadio.IsChecked = _settings.StreamNumber == 2;

        foreach (var p in Storage.LoadPresets().OrderBy(p => p.Slot))
            _presets.Add(p);
    }

    private void CollectSettings()
    {
        _settings.CameraIp = IpBox.Text.Trim();
        _settings.LatencyMs = (int)LatencySlider.Value;
        _settings.PanTiltSpeed = (int)PtSpeedSlider.Value;
        _settings.ZoomSpeed = (int)ZoomSpeedSlider.Value;
        _settings.RtspOverTcp = RtspTcpCheck.IsChecked == true;
        _settings.AudioMuted = MuteCheck.IsChecked == true;
        _settings.StreamNumber = SubStreamRadio.IsChecked == true ? 2 : 1;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Core.Initialize();
        _libVlc = new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview");
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            EnableHardwareDecoding = true,
            Mute = _settings.AudioMuted
        };
        _mediaPlayer.Playing += (_, _) => Dispatcher.Invoke(() =>
        {
            ShowVideoOverlay(null);
            StatusText.Text = $"Verbunden – {_settings.GetRtspUrl()}";
        });
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.Invoke(
            () => ShowVideoOverlay("Videofehler – Einstellungen prüfen"));
        _mediaPlayer.Stopped += (_, _) => Dispatcher.Invoke(
            () => ShowVideoOverlay("Nicht verbunden"));
        VideoView.MediaPlayer = _mediaPlayer;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CollectSettings();
        Storage.SaveSettings(_settings);
        Storage.SavePresets(_presets);
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _visca.Dispose();
    }

    // ------------------------------------------------------------------
    //  Verbindung
    // ------------------------------------------------------------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        CollectSettings();
        Storage.SaveSettings(_settings);

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Verbinde …";
        try
        {
            await _visca.ConnectAsync(_settings.CameraIp, _settings.ViscaPort);
            // Fokusmodus explizit setzen, damit Anzeige und Kamera übereinstimmen
            await Safe(_visca.FocusAutoAsync);
            SetAutoFocus(true);
            StartVideo();
        }
        catch (Exception ex)
        {
            UpdateStatus(false);
            StatusText.Text = $"Fehler: {ex.Message}";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void UpdateStatus(bool connected)
    {
        StatusDot.Fill = connected
            ? (Brush)FindResource("OkBrush")
            : (Brush)FindResource("DangerBrush");
        StatusText.Text = connected ? "Steuerung verbunden" : "Getrennt";
    }

    private void StartVideo()
    {
        if (_libVlc is null || _mediaPlayer is null) return;

        var url = _settings.GetRtspUrl();
        using var media = new Media(_libVlc, new Uri(url));
        media.AddOption($":network-caching={_settings.LatencyMs}");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");
        if (_settings.RtspOverTcp)
            media.AddOption(":rtsp-tcp");

        ShowVideoOverlay("Verbinde Video …");
        _mediaPlayer.Play(media);
    }

    /// <summary>Overlay über dem Videofenster: Text anzeigen (schwarz gefüllt)
    /// oder mit null durchsichtig schalten, sobald das Video läuft.</summary>
    private void ShowVideoOverlay(string? message)
    {
        if (message is null)
        {
            NoSignalText.Visibility = Visibility.Collapsed;
            VideoOverlay.Background = Brushes.Transparent;
        }
        else
        {
            NoSignalText.Text = message;
            NoSignalText.Visibility = Visibility.Visible;
            VideoOverlay.Background = Brushes.Black;
        }
    }

    private void ApplyVideo_Click(object sender, RoutedEventArgs e)
    {
        CollectSettings();
        Storage.SaveSettings(_settings);
        _mediaPlayer?.Stop();
        StartVideo();
    }

    private void Mute_Changed(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is not null)
            _mediaPlayer.Mute = MuteCheck.IsChecked == true;
    }

    // ------------------------------------------------------------------
    //  PTZ-Steuerung (Pad, gedrückt halten)
    // ------------------------------------------------------------------

    private void WirePadButtons()
    {
        HookHold(PadUp, 0, 1);
        HookHold(PadDown, 0, -1);
        HookHold(PadLeft, -1, 0);
        HookHold(PadRight, 1, 0);
        HookHold(PadUL, -1, 1);
        HookHold(PadUR, 1, 1);
        HookHold(PadDL, -1, -1);
        HookHold(PadDR, 1, -1);

        HookHoldAction(ZoomInButton, () => _visca.ZoomAsync(1, ZoomSpeed), () => _visca.ZoomAsync(0, 0));
        HookHoldAction(ZoomOutButton, () => _visca.ZoomAsync(-1, ZoomSpeed), () => _visca.ZoomAsync(0, 0));
        HookHoldAction(FocusNearButton,
            async () => { SetAutoFocus(false); await _visca.FocusManualAsync(); await _visca.FocusAsync(-1, _settings.FocusSpeed); },
            () => _visca.FocusAsync(0, 0));
        HookHoldAction(FocusFarButton,
            async () => { SetAutoFocus(false); await _visca.FocusManualAsync(); await _visca.FocusAsync(1, _settings.FocusSpeed); },
            () => _visca.FocusAsync(0, 0));
    }

    private void HookHold(Button button, int panDir, int tiltDir)
        => HookHoldAction(button,
            () => _visca.PanTiltAsync(panDir, tiltDir, PtSpeed, TiltSpeed),
            () => _visca.PanTiltStopAsync(PtSpeed, TiltSpeed));

    private void HookHoldAction(Button button, Func<Task> start, Func<Task> stop)
    {
        button.PreviewMouseLeftButtonDown += async (_, _) =>
        {
            _padActive = true;
            await Safe(start);
        };
        button.PreviewMouseLeftButtonUp += async (_, _) =>
        {
            if (!_padActive) return;
            _padActive = false;
            await Safe(stop);
        };
        button.MouseLeave += async (_, _) =>
        {
            if (!_padActive) return;
            _padActive = false;
            await Safe(stop);
        };
    }

    private static async Task Safe(Func<Task> action)
    {
        try { await action(); } catch { }
    }

    private async void Home_Click(object sender, RoutedEventArgs e) => await Safe(_visca.HomeAsync);

    private async void FocusAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoFocus)
        {
            // Autofokus aus = aktuelle Schärfe halten (Fokus-Sperre)
            await Safe(_visca.FocusManualAsync);
            SetAutoFocus(false);
        }
        else
        {
            await Safe(_visca.FocusAutoAsync);
            SetAutoFocus(true);
        }
    }

    private void SetAutoFocus(bool on)
    {
        _autoFocus = on;
        FocusAutoButton.Content = on ? "Auto ✓" : "Auto";
        FocusAutoButton.Background = (Brush)FindResource(on ? "AccentBrush" : "PanelLight");
        FocusAutoButton.BorderBrush = (Brush)FindResource(on ? "AccentBrush" : "BorderBrush2");
        FocusAutoButton.Foreground = on ? Brushes.White : (Brush)FindResource("FgBrush");
        FocusAutoButton.ToolTip = on
            ? "Autofokus aktiv – klicken zum Sperren der aktuellen Schärfe"
            : "Autofokus aus (manuell/gesperrt) – klicken zum Aktivieren";
    }

    // ------------------------------------------------------------------
    //  Tastatursteuerung
    // ------------------------------------------------------------------

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (!IsControlKey(e.Key)) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        _heldKeys.Add(e.Key);

        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.Left or Key.Right:
                await UpdateKeyboardPanTilt();
                break;
            case Key.PageUp:
                SetKeyVisual(ZoomInButton, true);
                await Safe(() => _visca.ZoomAsync(1, ZoomSpeed));
                break;
            case Key.PageDown:
                SetKeyVisual(ZoomOutButton, true);
                await Safe(() => _visca.ZoomAsync(-1, ZoomSpeed));
                break;
            case Key.Home:
                SetKeyVisual(PadHome, true);
                await Safe(_visca.HomeAsync);
                break;
        }
    }

    private async void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_heldKeys.Remove(e.Key)) return;
        e.Handled = true;

        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.Left or Key.Right:
                await UpdateKeyboardPanTilt();
                break;
            case Key.PageUp:
                SetKeyVisual(ZoomInButton, false);
                await Safe(() => _visca.ZoomAsync(0, 0));
                break;
            case Key.PageDown:
                SetKeyVisual(ZoomOutButton, false);
                await Safe(() => _visca.ZoomAsync(0, 0));
                break;
            case Key.Home:
                SetKeyVisual(PadHome, false);
                break;
        }
    }

    /// <summary>Kombiniert alle gehaltenen Pfeiltasten zu einer (auch diagonalen) Fahrt
    /// und hebt den passenden Pad-Button hervor. (0,0) = Stopp.</summary>
    private async Task UpdateKeyboardPanTilt()
    {
        int pan = (_heldKeys.Contains(Key.Right) ? 1 : 0) - (_heldKeys.Contains(Key.Left) ? 1 : 0);
        int tilt = (_heldKeys.Contains(Key.Up) ? 1 : 0) - (_heldKeys.Contains(Key.Down) ? 1 : 0);
        HighlightPad(pan, tilt);
        await Safe(() => _visca.PanTiltAsync(pan, tilt, PtSpeed, TiltSpeed));
    }

    private void HighlightPad(int pan, int tilt)
    {
        foreach (var b in new[] { PadUp, PadDown, PadLeft, PadRight, PadUL, PadUR, PadDL, PadDR })
            SetKeyVisual(b, false);

        Button? active = (pan, tilt) switch
        {
            (0, 1) => PadUp,
            (0, -1) => PadDown,
            (-1, 0) => PadLeft,
            (1, 0) => PadRight,
            (-1, 1) => PadUL,
            (1, 1) => PadUR,
            (-1, -1) => PadDL,
            (1, -1) => PadDR,
            _ => null
        };
        if (active is not null) SetKeyVisual(active, true);
    }

    private void SetKeyVisual(Button button, bool on)
        => button.Background = (Brush)FindResource(on ? "AccentDark" : "PanelLight");

    private static bool IsControlKey(Key k) =>
        k is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home;

    // ------------------------------------------------------------------
    //  Presets
    // ------------------------------------------------------------------

    private async void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!_visca.IsConnected)
        {
            MessageBox.Show(this, "Bitte zuerst mit der Kamera verbinden.", "Nicht verbunden",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int slot = NextFreeSlot();
        if (slot < 0)
        {
            MessageBox.Show(this, "Alle 254 Preset-Slots sind belegt.", "Kein Slot frei",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new NameDialog("Neues Preset", "Name für das Preset:", $"Preset {slot}") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // Position in der Kamera speichern und Livebild als Thumbnail sichern
        await Safe(() => _visca.PresetSetAsync((byte)slot));
        var thumbFile = await CaptureThumbnailAsync(slot);

        var preset = new Preset { Slot = slot, Name = dialog.EnteredName, ThumbnailFile = thumbFile };
        _presets.Add(preset);
        Storage.SavePresets(_presets);
    }

    private int NextFreeSlot()
    {
        var used = _presets.Select(p => p.Slot).ToHashSet();
        for (int i = 1; i <= 254; i++)
            if (!used.Contains(i)) return i;
        return -1;
    }

    /// <summary>Schnappschuss vom Livebild als Preset-Thumbnail speichern.</summary>
    private async Task<string?> CaptureThumbnailAsync(int slot)
    {
        if (_mediaPlayer is null || !_mediaPlayer.IsPlaying) return null;

        string tmp = Path.Combine(Path.GetTempPath(), $"v60_snap_{Guid.NewGuid():N}.png");
        try
        {
            if (!_mediaPlayer.TakeSnapshot(0, tmp, 0, 0)) return null;

            // TakeSnapshot schreibt asynchron – kurz auf die Datei warten
            for (int i = 0; i < 40 && !File.Exists(tmp); i++)
                await Task.Delay(50);
            if (!File.Exists(tmp)) return null;
            await Task.Delay(100); // Datei fertig schreiben lassen

            string thumbFile = $"preset_{slot}.jpg";
            string thumbPath = Path.Combine(Storage.ThumbDir, thumbFile);
            SaveDownscaledJpeg(tmp, thumbPath, 320);
            return thumbFile;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static void SaveDownscaledJpeg(string sourcePath, string targetPath, int width)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(sourcePath);
        img.DecodePixelWidth = width;
        img.EndInit();
        img.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(targetPath);
        encoder.Save(fs);
    }

    private static Preset? PresetFromSender(object sender)
        => (sender as FrameworkElement)?.Tag as Preset;

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetFromSender(sender) is { } preset)
            await Safe(() => _visca.PresetRecallAsync((byte)preset.Slot));
    }

    private async void PresetRecallMenu_Click(object sender, RoutedEventArgs e)
    {
        if (PresetFromSender(sender) is { } preset)
            await Safe(() => _visca.PresetRecallAsync((byte)preset.Slot));
    }

    private async void PresetUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (PresetFromSender(sender) is not { } preset) return;
        if (!_visca.IsConnected) return;

        await Safe(() => _visca.PresetSetAsync((byte)preset.Slot));
        var thumb = await CaptureThumbnailAsync(preset.Slot);
        if (thumb is not null)
        {
            // Bild-Cache umgehen: Property kurz nullen, damit das Binding neu lädt
            preset.ThumbnailFile = null;
            preset.ThumbnailFile = thumb;
        }
        Storage.SavePresets(_presets);
    }

    private void PresetRename_Click(object sender, RoutedEventArgs e)
    {
        if (PresetFromSender(sender) is not { } preset) return;

        var dialog = new NameDialog("Preset umbenennen", "Neuer Name:", preset.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        preset.Name = dialog.EnteredName;
        Storage.SavePresets(_presets);
    }

    private async void PresetDelete_Click(object sender, RoutedEventArgs e)
    {
        if (PresetFromSender(sender) is not { } preset) return;

        var result = MessageBox.Show(this, $"Preset '{preset.Name}' wirklich löschen?", "Preset löschen",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        await Safe(() => _visca.PresetClearAsync((byte)preset.Slot));
        if (preset.ThumbnailPath is { } tp)
        {
            try { if (File.Exists(tp)) File.Delete(tp); } catch { }
        }
        _presets.Remove(preset);
        Storage.SavePresets(_presets);
    }
}

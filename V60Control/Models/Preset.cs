using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace V60Control.Models;

public class Preset : INotifyPropertyChanged
{
    private string _name = "";
    private string? _thumbnailFile;

    /// <summary>Kamerainterner Preset-Slot (1–254).</summary>
    public int Slot { get; set; }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>Dateiname des Thumbnails (relativ zum Thumbnail-Ordner).</summary>
    public string? ThumbnailFile
    {
        get => _thumbnailFile;
        set { _thumbnailFile = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThumbnailPath)); }
    }

    /// <summary>Absoluter Pfad zum Thumbnail (für Binding).</summary>
    public string? ThumbnailPath =>
        ThumbnailFile is null ? null : System.IO.Path.Combine(Services.Storage.ThumbDir, ThumbnailFile);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

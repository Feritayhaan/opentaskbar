using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OpenTaskbar.Services.Widgets;

public sealed record MediaSnapshot(bool HasMedia, string Title, string Artist, string SourceApp, bool IsPlaying, ImageSource? Thumbnail);

/// <summary>Tracks Windows GlobalSystemMediaTransportControls (Now Playing) and sends playback commands.</summary>
public sealed class MediaSessionService
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _initializing;

    public MediaSessionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public MediaSnapshot Current { get; private set; } = new(false, "", "", "", false, null);
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (_manager is not null || _initializing) return;
        _initializing = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(async () => await AttachCurrentSessionAsync());
            await AttachCurrentSessionAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Media session manager unavailable: {0}", ex.Message);
        }
        finally
        {
            _initializing = false;
        }
    }

    public async Task RefreshAsync()
    {
        if (_manager is null) await InitializeAsync();
        else await ReadSnapshotAsync();
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_session is null) return;
        try { await _session.TryTogglePlayPauseAsync(); } catch { }
        await ReadSnapshotAsync();
    }

    /// <summary>Pauses playback only when something is currently playing; returns whether a pause was sent.</summary>
    public async Task<bool> PauseIfPlayingAsync()
    {
        if (_session is null || !Current.IsPlaying) return false;
        try { await _session.TryPauseAsync(); } catch { return false; }
        await ReadSnapshotAsync();
        return true;
    }

    public async Task PreviousAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipPreviousAsync(); } catch { }
        await ReadSnapshotAsync();
    }

    public async Task NextAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipNextAsync(); } catch { }
        await ReadSnapshotAsync();
    }

    private async Task AttachCurrentSessionAsync()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_Updated;
            _session.PlaybackInfoChanged -= Session_Updated;
        }

        _session = _manager?.GetCurrentSession();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += Session_Updated;
            _session.PlaybackInfoChanged += Session_Updated;
        }

        await ReadSnapshotAsync();
    }

    private void Session_Updated(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        _dispatcher.BeginInvoke(async () => await ReadSnapshotAsync());
    }

    private async Task ReadSnapshotAsync()
    {
        var session = _session;
        if (session is null)
        {
            Current = new MediaSnapshot(false, "", "", "", false, null);
            Changed?.Invoke();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var title = props?.Title?.Trim() ?? "";
            var artist = props?.Artist?.Trim() ?? "";
            var sourceApp = FormatSourceApp(session.SourceAppUserModelId);
            ImageSource? thumb = null;
            if (props?.Thumbnail is not null)
            {
                thumb = await LoadThumbnailAsync(props.Thumbnail);
            }

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
            {
                Current = new MediaSnapshot(false, "", "", "", false, null);
            }
            else
            {
                Current = new MediaSnapshot(true, title, string.IsNullOrEmpty(artist) ? sourceApp : artist, sourceApp, isPlaying, thumb);
            }
        }
        catch
        {
            Current = new MediaSnapshot(false, "", "", "", false, null);
        }

        Changed?.Invoke();
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using var stream = await streamRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSourceApp(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        var clean = appId;
        var bang = clean.IndexOf('!');
        if (bang > 0) clean = clean[..bang];
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        var dot = clean.LastIndexOf('.');
        if (dot >= 0 && dot < clean.Length - 1) clean = clean[(dot + 1)..];
        return clean;
    }
}

namespace NebulaBridge.Services;

/// <summary>Applies the configured free-space floor before optional cache writes.</summary>
public sealed class CacheStorageSafety(AcquisitionJobStore store)
{
    public bool CanWrite(long bytes)
    {
        try
        {
            var fullPath = Path.GetFullPath(store.RootPath);
            var drive = DriveInfo.GetDrives()
                .Where(candidate => fullPath.StartsWith(candidate.Name, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Name.Length)
                .FirstOrDefault();
            if (drive is null || !drive.IsReady) return false;
            var available = drive.AvailableFreeSpace;
            var floorMb = NebulaBridgePlugin.Instance?.Configuration.PlaybackCacheMinimumFreeSpaceMb ?? 2048;
            return HasCapacity(available, bytes, floorMb);
        }
        catch { return false; }
    }

    internal static bool HasCapacity(long availableBytes, long requestedBytes, int floorMb) =>
        requestedBytes >= 0 && availableBytes >= requestedBytes
        && availableBytes - requestedBytes >= floorMb * 1024L * 1024L;
}

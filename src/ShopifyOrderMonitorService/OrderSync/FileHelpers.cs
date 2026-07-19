using Microsoft.Extensions.Logging;

namespace ShopifyOrderMonitorService.OrderSync;

/// <summary>Shared file-system helpers used when writing sync artefacts atomically.</summary>
static class FileHelpers
{
    public static void TryDelete(ILogger logger, string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove temporary file {Path}.", path);
        }
    }
}

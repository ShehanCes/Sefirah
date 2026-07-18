using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface ISftpFeature : IFeature
{
    Task InitializeAsync(PairedDevice device, SftpServerInfo info);

    void Remove(string deviceId);

    void RemoveAll();

    /// <summary>
    /// Prompts the user if platform prerequisites for storage access are missing (e.g. macFUSE/sshfs on macOS).
    /// </summary>
    Task EnsurePrerequisitesAsync();
}

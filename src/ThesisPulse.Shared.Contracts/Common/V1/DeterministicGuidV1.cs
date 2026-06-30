using System.Security.Cryptography;
using System.Text;

namespace ThesisPulse.Shared.Contracts.Common.V1;

public static class DeterministicGuidV1
{
    public static Guid Create(Guid namespaceUid, string name)
    {
        if (namespaceUid == Guid.Empty)
        {
            throw new ArgumentException("Namespace UID is required.", nameof(namespaceUid));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var namespaceBytes = namespaceUid.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(name.Trim());
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);
        var hash = SHA256.HashData(input);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace ClassroomShift;

/// <summary>Accidental-system check, NOT authentication or anti-tamper security.</summary>
public static class ShiftUserIdentity
{
    public static string Normalize(string username) => username.Trim().ToUpperInvariant();
    public static string Hash(string username)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(username)))).ToLowerInvariant();
}
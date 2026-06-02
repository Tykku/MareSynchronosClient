using System.Security.Cryptography;
using System.Text;

namespace MareStandaloneClient;

public static class Crypto
{
    public static string GetHash256(this string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).Replace("-", "", StringComparison.Ordinal);
    }
}

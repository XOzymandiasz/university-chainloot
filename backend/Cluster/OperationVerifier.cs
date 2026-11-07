using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChainLoot.Backend.Cluster;

public static class OperationVerifier
{
    public static byte[] Message(SignedOperation op) => Encoding.UTF8.GetBytes(
        string.Join(char.ConvertFromUtf32(10), "chainloot:v1", op.OperationId, op.Type, op.AssetId,
            op.Sender, op.Recipient, op.Nonce.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static bool Verify(SignedOperation op)
    {
        if (op.OperationId is null || op.Type is null || op.AssetId is null || op.Sender is null || op.Recipient is null || op.PublicKey is null || op.Signature is null) return false;
        if (!Guid.TryParseExact(op.OperationId, "D", out _) ||
            op.Type is not ("issue" or "transfer" or "redeem") ||
            !Regex.IsMatch(op.AssetId, "^[0-9]{1,39}$") ||
            !Regex.IsMatch(op.Sender, "^0x[0-9a-f]{40}$") ||
            !(op.Recipient == "" || Regex.IsMatch(op.Recipient, "^0x[0-9a-f]{40}$")) ||
            op.Nonce < 1 || op.PublicKey.Length > 512 || op.Signature.Length > 256)
            return false;
        if (op.AssetId.Length > 1 && op.AssetId[0] == '0') return false;
        if ((op.Type == "issue" || op.Type == "transfer") && op.Recipient.Length == 0) return false;
        if (op.Type == "redeem" && op.Recipient.Length != 0) return false;
        try
        {
            var publicKey = Convert.FromBase64String(op.PublicKey);
            var hash = SHA256.HashData(publicKey);
            var address = "0x" + Convert.ToHexString(hash[^20..]).ToLowerInvariant();
            if (address != op.Sender) return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || ecdsa.KeySize != 256) return false;
            return ecdsa.VerifyData(Message(op), Convert.FromBase64String(op.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}

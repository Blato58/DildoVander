using System.Security.Cryptography;

namespace Kufr.Backend.Features.DeviceMedia;

// Receive-owner only. A repeated control can recover its acknowledgement, but
// never reapply changed availability or borrow another generation's receipt.
internal sealed class DeviceMediaControlLedger
{
    private sealed record Receipt(byte[] Hash, int Status = 0, string? Code = null);
    private readonly Dictionary<Guid, Receipt> receipts = [];
    internal const int Capacity = 128;

    internal bool IsReplay(Guid request, ReadOnlySpan<byte> body)
    {
        var hash = SHA256.HashData(body);
        if (receipts.TryGetValue(request, out var previous))
        {
            if (!CryptographicOperations.FixedTimeEquals(hash, previous.Hash))
            {
                throw new InvalidDataException("media_control_conflict");
            }
            return true;
        }
        if (request == Guid.Empty || receipts.Count == Capacity)
        {
            throw new InvalidDataException("media_control_capacity");
        }
        receipts.Add(request, new(hash));
        return false;
    }

    internal void Complete(Guid request, int status, string? code = null)
    {
        if (!receipts.TryGetValue(request, out var previous) || previous.Status != 0 || status is not (204 or 409))
        {
            throw new InvalidDataException("media_control_receipt_mismatch");
        }
        receipts[request] = previous with { Status = status, Code = code };
    }

    internal (int Status, string? Code) Result(Guid request)
    {
        if (!receipts.TryGetValue(request, out var receipt) || receipt.Status == 0)
        {
            throw new InvalidDataException("media_control_receipt_missing");
        }
        return (receipt.Status, receipt.Code);
    }
}

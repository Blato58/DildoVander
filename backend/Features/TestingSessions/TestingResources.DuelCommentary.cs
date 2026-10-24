namespace Kufr.Backend.Features.Show;

internal sealed partial class DuelStore
{
    internal DuelView? ClaimCommentary(string id)
    {
        var result = Read(id);
        using var connection = Open();
        return Execute(connection, null,
            "UPDATE show_duel_commentary SET state='claimed' WHERE duel_id=$id AND state='pending'", ("$id", id)) == 1 ? result : null;
    }
}

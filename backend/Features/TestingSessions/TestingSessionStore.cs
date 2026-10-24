using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.TestingSessions;

// Documents, ownership and deletion fences live in an additive, isolated store.
// Every lookup includes authenticated owner; untrusted IDs never select a path.
internal sealed class TestingSessionStore
{
    private readonly string connectionString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();
    internal TestingSessionStore(string root)
    {
        Directory.CreateDirectory(root);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root,"sessions.sqlite"),
            Pooling = false, DefaultTimeout = 15 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS testing_documents(kind TEXT NOT NULL,id TEXT NOT NULL,owner TEXT NOT NULL,
                document TEXT NOT NULL,PRIMARY KEY(kind,id));
            CREATE INDEX IF NOT EXISTS testing_owners ON testing_documents(owner,kind);
            CREATE TABLE IF NOT EXISTS testing_owner_fences(owner TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS testing_tombstones(owner TEXT NOT NULL,id TEXT NOT NULL,PRIMARY KEY(owner,id));
            """;
        command.ExecuteNonQuery();
    }
    internal T[] List<T>(string owner,string kind)
    {
        lock(gate)
        {
            using var c=Open(); using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT document FROM testing_documents WHERE owner=$owner AND kind=$kind";
            cmd.Parameters.AddWithValue("$owner",owner); cmd.Parameters.AddWithValue("$kind",kind);
            using var reader=cmd.ExecuteReader(); var result=new List<T>();
            while(reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0),Json)!);
            return result.ToArray();
        }
    }
    internal T Read<T>(string owner,string kind,string id) => TryRead<T>(owner,kind,id)
        ?? throw new TestingException(404,"test_resource_unavailable");
    internal T? TryRead<T>(string owner,string kind,string id)
    {
        lock(gate)
        {
            using var c=Open(); using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT document FROM testing_documents WHERE owner=$owner AND kind=$kind AND id=$id";
            cmd.Parameters.AddWithValue("$owner",owner);cmd.Parameters.AddWithValue("$kind",kind);cmd.Parameters.AddWithValue("$id",id);
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json,Json) : default;
        }
    }
    internal void Put<T>(string owner,string kind,string id,T value)
    {
        lock(gate)
        {
            using var c=Open(); using var tx=c.BeginTransaction();
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO testing_documents(kind,id,owner,document)
                SELECT $kind,$id,$owner,$document
                WHERE NOT EXISTS(SELECT 1 FROM testing_owner_fences WHERE owner=$owner)
                AND NOT EXISTS(SELECT 1 FROM testing_tombstones WHERE owner=$owner AND id=$id)
                ON CONFLICT(kind,id) DO UPDATE SET document=excluded.document WHERE owner=excluded.owner;
                """;
            cmd.Parameters.AddWithValue("$kind",kind);cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$owner",owner);
            cmd.Parameters.AddWithValue("$document",JsonSerializer.Serialize(value,Json));
            if(cmd.ExecuteNonQuery()!=1) throw new TestingException(409,"test_resource_deleted");
            tx.Commit();
        }
    }
    internal void Remove(string owner,string kind,string id)
    {
        lock(gate)
        {
            using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT OR IGNORE INTO testing_tombstones VALUES($owner,$id); DELETE FROM testing_documents WHERE owner=$owner AND kind=$kind AND id=$id";
            cmd.Parameters.AddWithValue("$owner",owner);cmd.Parameters.AddWithValue("$kind",kind);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();tx.Commit();
        }
    }
    internal void Fence(string owner)
    {
        lock(gate)
        {
            using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO testing_owner_fences VALUES($owner)";
            cmd.Parameters.AddWithValue("$owner",owner);cmd.ExecuteNonQuery();
        }
    }
    internal bool TryStartUnlessFenced(string owner,Action start)
    {
        lock(gate)
        {
            if(IsFenced(owner))return false;
            start();
            return true;
        }
    }
    internal void PurgeOwnerContent(string owner)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="DELETE FROM testing_documents WHERE owner=$owner AND EXISTS(SELECT 1 FROM testing_owner_fences WHERE owner=$owner)";
        cmd.Parameters.AddWithValue("$owner",owner);cmd.ExecuteNonQuery();
    }
    internal bool IsFenced(string owner)
    {
        using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM testing_owner_fences WHERE owner=$owner";
        cmd.Parameters.AddWithValue("$owner",owner);return (long)cmd.ExecuteScalar()! !=0;
    }
    internal static string OwnerKey(string owner)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..32];
    private SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
}

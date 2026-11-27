using CharacterConceptAI.Server.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CharacterConceptAI.Server.BackgroundJobs;

// Dedicated SQL Server session lock held for the complete provider call, isolated from
// AppDbContext's connection so EF Core EnableRetryOnFailure reconnects can never drop the lock.
internal sealed class SqlServerGpuLease : IAsyncDisposable
{
    private readonly SqlConnection? _connection;
    private readonly bool _usesSql;
    public bool IsAcquired { get; private set; }

    private SqlServerGpuLease(SqlConnection? connection, bool usesSql)
    {
        _connection = connection;
        _usesSql = usesSql;
    }

    public static async Task<SqlServerGpuLease> TryAcquireAsync(AppDbContext db, CancellationToken ct, int lockTimeoutMs = 0)
    {
        if (!db.Database.IsSqlServer())
        {
            return new SqlServerGpuLease(null, false) { IsAcquired = true };
        }

        var connStr = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            return new SqlServerGpuLease(null, false) { IsAcquired = true };
        }

        var conn = new SqlConnection(connStr);
        try
        {
            await conn.OpenAsync(ct);
            await using var command = conn.CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = 'CharacterConceptAI.GpuExecution',
                    @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout;
                SELECT @result;
                """;
            command.Parameters.AddWithValue("@timeout", Math.Max(0, lockTimeoutMs));
            var acquired = Convert.ToInt32(await command.ExecuteScalarAsync(ct)) >= 0;
            if (!acquired)
            {
                await conn.DisposeAsync();
                return new SqlServerGpuLease(null, true) { IsAcquired = false };
            }
            return new SqlServerGpuLease(conn, true) { IsAcquired = true };
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_usesSql || _connection is null) return;
        try
        {
            if (IsAcquired && _connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = _connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "EXEC sys.sp_releaseapplock @Resource = 'CharacterConceptAI.GpuExecution', @LockOwner = 'Session';";
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch
        {
            // Closing/disposing the dedicated SQL session automatically releases Session-scoped applocks.
        }
        finally
        {
            IsAcquired = false;
            await _connection.DisposeAsync();
        }
    }
}

using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace GuestAuth;

public sealed class GuestStore(NpgsqlDataSource database)
{
    private readonly PasswordHasher<string> hasher = new();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = database.CreateCommand(
            """
            CREATE TABLE IF NOT EXISTS guest_devices (
                id varchar(16) PRIMARY KEY,
                password_hash text NOT NULL,
                subject varchar(16) NOT NULL,
                created_at bigint NOT NULL
            )
            """
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasAccountsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = database.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM guest_devices)"
        );
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = database.CreateCommand(
            "SELECT 1 FROM guest_devices LIMIT 1"
        );
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<DeviceRegistration> LoginAsync(
        DeviceCredential credential,
        Func<CancellationToken, Task<string>> establishSubject,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlConnection connection = await database.OpenConnectionAsync(
            cancellationToken
        );
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            cancellationToken
        );
        // Serialize first migration per device, including concurrent requests and container restarts.
        await using (
            NpgsqlCommand acquire = new(
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
                connection,
                transaction
            )
        )
        {
            acquire.Parameters.AddWithValue(credential.Id);
            await acquire.ExecuteNonQueryAsync(cancellationToken);
        }

        DeviceRegistration? existing = null;
        await using (
            NpgsqlCommand find = new(
                "SELECT password_hash, subject, created_at FROM guest_devices WHERE id = $1",
                connection,
                transaction
            )
        )
        {
            find.Parameters.AddWithValue(credential.Id);
            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existing = new(
                    credential.Id,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2)
                );
            }
        }

        if (existing is not null)
        {
            if (
                this.hasher.VerifyHashedPassword(
                    credential.Id,
                    existing.PasswordHash,
                    credential.Password
                ) == PasswordVerificationResult.Failed
            )
            {
                throw new LoginFailure(StatusCodes.Status401Unauthorized);
            }

            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        // Never establish an old account from PreviousUserId, a player ID or device ID alone.
        string subject = await establishSubject(cancellationToken);
        DeviceRegistration registration = new(
            credential.Id,
            this.hasher.HashPassword(credential.Id, credential.Password),
            subject,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );
        await using NpgsqlCommand insert = new(
            "INSERT INTO guest_devices (id, password_hash, subject, created_at) VALUES ($1,$2,$3,$4)",
            connection,
            transaction
        );
        insert.Parameters.AddWithValue(registration.Id);
        insert.Parameters.AddWithValue(registration.PasswordHash);
        insert.Parameters.AddWithValue(registration.Subject);
        insert.Parameters.AddWithValue(registration.CreatedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return registration;
    }
}

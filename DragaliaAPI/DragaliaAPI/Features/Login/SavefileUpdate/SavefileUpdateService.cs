using System.Diagnostics;
using DragaliaAPI.Database;
using DragaliaAPI.Database.Entities;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Shared.PlayerDetails;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace DragaliaAPI.Features.Login.SavefileUpdate;

/// <summary>
/// Service to apply <see cref="ISavefileUpdate"/> instances.
/// </summary>
public partial class SavefileUpdateService : ISavefileUpdateService
{
    private readonly ApiContext context;
    private readonly IPlayerIdentityService playerIdentityService;
    private readonly IEnumerable<ISavefileUpdate> savefileUpdates;
    private readonly ILogger<SavefileUpdateService> logger;
    private readonly IOptionsMonitor<EventOptions> eventOptions;

    public SavefileUpdateService(
        ApiContext context,
        IPlayerIdentityService playerIdentityService,
        IEnumerable<ISavefileUpdate> savefileUpdates,
        ILogger<SavefileUpdateService> logger,
        IOptionsMonitor<EventOptions> eventOptions
    )
    {
        this.context = context;
        this.playerIdentityService = playerIdentityService;
        this.savefileUpdates = savefileUpdates;
        this.logger = logger;
        this.eventOptions = eventOptions;
    }

    public async Task UpdateSavefile()
    {
        DbPlayer? player = await this.context.Players.FindAsync(
            this.playerIdentityService.ViewerId
        );

        if (player is null)
        {
            Log.CouldNotFindPlayer(this.logger, this.playerIdentityService.AccountId);

            return;
        }

        Stopwatch s = new();

        // Start a transaction; some savefile updates use LinqToDb.EntityFrameworkCore, which applies immediately
        // without SaveChanges. This means if something goes wrong during the update these changes will persist.
        // However, LinqToDb will inherit the EFC transaction if one is active. So we can make this operation
        // atomic by wrapping it in a transaction.

        await using IDbContextTransaction transaction =
            await this.context.Database.BeginTransactionAsync();

        foreach (ISavefileUpdate update in this.savefileUpdates.OrderBy(x => x.SavefileVersion))
        {
            if (player.SavefileVersion < update.SavefileVersion)
            {
                s.Restart();
                await update.Apply();
                s.Stop();

                player.SavefileVersion = update.SavefileVersion;

                Log.AppliedSavefileUpdateTookMs(this.logger, update, s.ElapsedMilliseconds);
            }
        }

        // Clear only the selected entry; retain event progress and claimed reward flags.
        if (this.eventOptions.CurrentValue.DisabledMemoryEventIds.Count > 0)
        {
            DbPlayerUserData userData = await this.context.PlayerUserData.SingleAsync();
            if (
                this.eventOptions.CurrentValue.DisabledMemoryEventIds.Contains(
                    userData.ActiveMemoryEventId
                )
            )
            {
                userData.ActiveMemoryEventId = 0;
            }
        }

        // Do not need an UpdateDataList as this is called during /load/index
        await this.context.SaveChangesAsync();

        await transaction.CommitAsync();
    }

    private static partial class Log
    {
        [LoggerMessage(LogLevel.Information, "Could not find player {player}")]
        public static partial void CouldNotFindPlayer(ILogger logger, string player);

        [LoggerMessage(LogLevel.Information, "Applied savefile update {update} (took: {time} ms)")]
        public static partial void AppliedSavefileUpdateTookMs(
            ILogger logger,
            ISavefileUpdate update,
            long time
        );
    }
}

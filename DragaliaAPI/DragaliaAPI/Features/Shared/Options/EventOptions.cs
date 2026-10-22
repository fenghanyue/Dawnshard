namespace DragaliaAPI.Features.Shared.Options;

public class EventOptions
{
    public List<EventRunInformation> EventList { get; init; } = [];

    // Original events exposed through the compendium keep their original currencies and rewards.
    // They are deliberately separate from EventData.IsMemoryEvent, which changes those rules.
    public List<int> ArchivedEventIds { get; init; } = [];

    public bool IsArchived(int eventId) => this.ArchivedEventIds.Contains(eventId);

    public EventRunInformation? FindRun(int eventId) =>
        this.IsArchived(eventId)
            ? new()
            {
                Id = eventId,
                Start = DateTimeOffset.UnixEpoch,
                End = DateTimeOffset.MaxValue,
            }
            : this.EventList.Find(x => x.Id == eventId);

    public IEnumerable<EventRunInformation> GetActiveEvents(TimeProvider timeProvider) =>
        this
            .EventList.Select(x => x.Id)
            .Concat(this.ArchivedEventIds)
            .Distinct()
            .Select(this.FindRun)
            .OfType<EventRunInformation>()
            .Where(x => x.IsActive(timeProvider));
}

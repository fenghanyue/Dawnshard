"""User-selected duration for new events and their concurrent summon banners."""

from datetime import datetime, timedelta

DEFAULT_DURATION_DAYS = 999


def default_end(start: datetime) -> datetime:
    return start + timedelta(days=DEFAULT_DURATION_DAYS, seconds=-1)

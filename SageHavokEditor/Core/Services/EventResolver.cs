using System;
using System.Collections.Generic;
using SageHavokEditor.Models.ViewModels;

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Single source of truth for turning a Havok event id (the integer index a
    /// transition / trigger / property stores) into its human-readable name, and
    /// back again. Backed by the live event table, so renames, adds and deletes
    /// are reflected without any explicit invalidation.
    ///
    /// Replaces the scattered <c>EventList.ToDictionary(...)</c> + <c>$"Event {id}"</c>
    /// fallbacks that previously let a bare id like <c>#495</c> leak into the UI
    /// with no way to trace it.
    /// </summary>
    public sealed class EventResolver
    {
        private readonly IList<IdNamePair> _events;

        // id -> entry cache, rebuilt only when the backing list size changes.
        // We cache the IdNamePair (not its name) so in-place renames stay live.
        private Dictionary<string, IdNamePair> _byId = new();
        private int _cachedCount = -1;

        public EventResolver(IList<IdNamePair> events)
        {
            _events = events ?? Array.Empty<IdNamePair>();
        }

        private void EnsureFresh()
        {
            if (_cachedCount == _events.Count) return;
            var map = new Dictionary<string, IdNamePair>(_events.Count);
            foreach (var e in _events)
                if (!string.IsNullOrEmpty(e.Id))
                    map[e.Id] = e;
            _byId = map;
            _cachedCount = _events.Count;
        }

        /// <summary>The event name for an id, or null if the id is empty/unknown.</summary>
        public string? Resolve(string? eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return null;
            EnsureFresh();
            // Membership can change without the count changing (rare swap); fall
            // back to a rebuild before giving up.
            if (!_byId.TryGetValue(eventId, out var e))
            {
                _cachedCount = -1;
                EnsureFresh();
                if (!_byId.TryGetValue(eventId, out e)) return null;
            }
            return string.IsNullOrEmpty(e.Name) ? null : e.Name;
        }

        /// <summary>
        /// Display name for an id, safe for a field that shows the name on its own.
        /// Returns the name, or a clearly-marked placeholder for an unresolved id
        /// (e.g. <c>‹unnamed #495›</c>) — never the name-like <c>Event 495</c>.
        /// </summary>
        public string Name(string? eventId)
        {
            var n = Resolve(eventId);
            if (n != null) return n;
            return string.IsNullOrEmpty(eventId) ? "" : $"‹unnamed #{eventId}›";
        }

        /// <summary>
        /// Combined label for surfaces that show a single piece of text per event
        /// (graph edge labels, context menus, detail headers): <c>Name (#id)</c>,
        /// so the id is always traceable. Unresolved ids show the placeholder.
        /// </summary>
        public string Label(string? eventId)
        {
            var n = Resolve(eventId);
            if (n != null) return $"{n} (#{eventId})";
            return string.IsNullOrEmpty(eventId) ? "" : $"‹unnamed #{eventId}›";
        }

        /// <summary>First event id whose name matches (case-insensitive), or null.</summary>
        public string? IdOf(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            EnsureFresh();
            foreach (var e in _events)
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    return e.Id;
            return null;
        }
    }
}

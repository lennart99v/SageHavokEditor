using System;
using System.Collections.Generic;
using System.Linq;
using SageHavokEditor.Models;
using SageHavokEditor.Models.ViewModels;

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Finds every site in a behaviour graph that references a given event id.
    ///
    /// An event id is a bare positional index into
    /// <c>hkbBehaviorGraphStringData.eventNames</c>, and it turns up in a lot more
    /// places than the obvious transition <c>eventId</c>. Missing any of them makes
    /// a cross-reference quietly incomplete, which is worse than having none — you
    /// conclude an event is unused when it isn't. The sites are:
    ///
    ///   listens  state transition          eventId
    ///   listens  wildcard transition       eventId            (fires from any state)
    ///   listens  trigger/initiate interval enterEventId / exitEventId   (INSIDE a transition)
    ///   listens  state machine             returnToPreviousStateEventId, randomTransitionEventId,
    ///                                      transitionToNextHigher/LowerStateEventId
    ///   listens  event-driven modifier     eventId
    ///   sends    state enter/exit notify   enterNotifyEvents / exitNotifyEvents → events[].id
    ///   sends    clip annotation           triggers[].event.id
    ///   sends    state machine             eventToSendWhenStateOrTransitionChanges.id
    ///   sends    modifier                  eventToSend
    ///
    /// The interval ids and the notify-event arrays are the easy ones to miss: both
    /// live in objects nested *below* an entry in <c>ObjectMap</c>, so a scan over
    /// top-level params alone never sees them.
    /// </summary>
    public static class EventCrossReference
    {
        public const string Listens = "Listens";
        public const string Sends = "Sends";

        /// <summary>Top-level params holding an event id the owning object reacts to.</summary>
        private static readonly Dictionary<string, string> ListenParams = new()
        {
            ["eventId"] = "eventId",
            ["returnToPreviousStateEventId"] = "returnToPreviousStateEventId",
            ["randomTransitionEventId"] = "randomTransitionEventId",
            ["transitionToNextHigherStateEventId"] = "transitionToNextHigherStateEventId",
            ["transitionToNextLowerStateEventId"] = "transitionToNextLowerStateEventId",
        };

        /// <summary>Top-level params holding an event id the owning object emits.</summary>
        private static readonly Dictionary<string, string> SendParams = new()
        {
            ["eventToSend"] = "eventToSend",
            ["eventToSendWhenStateOrTransitionChanges"] = "eventToSendWhenStateOrTransitionChanges",
        };

        /// <summary>
        /// Every reference to <paramref name="eventIndex"/>, ordered listens-then-sends.
        /// Returns an empty list (not a placeholder row) when nothing references it.
        /// </summary>
        public static List<EventUsageEntry> Find(HavokManager manager, string eventIndex)
        {
            var results = new List<EventUsageEntry>();
            if (manager == null || string.IsNullOrEmpty(eventIndex) || eventIndex == "-1")
                return results;

            foreach (var sm in manager.ObjectMap.Values.Where(o => o.ClassName == "hkbStateMachine"))
            {
                var smName = Name(sm);
                var smStates = ResolveStates(manager, sm);

                // ── per-state transitions ─────────────────────────────────
                foreach (var stateObj in smStates.Values)
                {
                    var fromName = Name(stateObj);
                    foreach (var tr in TransitionsOf(manager, stateObj, "transitions"))
                        Inspect(manager, results, eventIndex, tr, sm, smName, smStates,
                                "Transition", $"{fromName}  →  ", stateObj.Id, "hkbStateMachineStateInfo");
                }

                // ── wildcard transitions (fire from any state in this SM) ──
                foreach (var tr in TransitionsOf(manager, sm, "wildcardTransitions"))
                    Inspect(manager, results, eventIndex, tr, sm, smName, smStates,
                            "Wildcard", "★ any state  →  ", sm.Id, "hkbStateMachine");

                // ── state machine's own event ids ─────────────────────────
                foreach (var p in sm.Params)
                {
                    if (p.Value != eventIndex) continue;
                    if (ListenParams.ContainsKey(p.Name) && p.Name != "eventId")
                        results.Add(new EventUsageEntry
                        {
                            UsageType = "Property",
                            Direction = Listens,
                            Description = $"{smName}  [{p.Name}]",
                            Detail = "state machine control event",
                            ObjectId = sm.Id,
                            ClassName = "hkbStateMachine",
                        });
                }

                // eventToSendWhenStateOrTransitionChanges is an inline struct, not a plain value
                var evSend = sm.Params.FirstOrDefault(p => p.Name == "eventToSendWhenStateOrTransitionChanges");
                if (IdOfInlineEvent(evSend) == eventIndex)
                    results.Add(new EventUsageEntry
                    {
                        UsageType = "Property",
                        Direction = Sends,
                        Description = $"{smName}  [eventToSendWhenStateOrTransitionChanges]",
                        Detail = "sent whenever this machine changes state",
                        ObjectId = sm.Id,
                        ClassName = "hkbStateMachine",
                    });

                // ── states that SEND on enter/exit ────────────────────────
                foreach (var stateObj in smStates.Values)
                {
                    foreach (var (paramName, label) in new[]
                             {
                                 ("enterNotifyEvents", "on enter"),
                                 ("exitNotifyEvents", "on exit"),
                             })
                    {
                        var arrRef = Val(stateObj, paramName);
                        if (IsNull(arrRef) || !manager.TryResolve(arrRef, out var arrObj) || arrObj == null)
                            continue;

                        var evParam = arrObj.Params.FirstOrDefault(p => p.Name == "events");
                        if (evParam?.Children == null) continue;

                        foreach (var evt in evParam.Children)
                        {
                            if (Val(evt, "id") != eventIndex) continue;
                            results.Add(new EventUsageEntry
                            {
                                UsageType = "Notify",
                                Direction = Sends,
                                Description = $"{Name(stateObj)}  sends {label}",
                                Detail = $"in {smName}",
                                ObjectId = stateObj.Id,
                                ClassName = "hkbStateMachineStateInfo",
                            });
                        }
                    }
                }
            }

            // ── clip annotations ──────────────────────────────────────────
            foreach (var clip in manager.ObjectMap.Values.Where(o => o.ClassName == "hkbClipGenerator"))
            {
                var trigRef = Val(clip, "triggers");
                if (IsNull(trigRef) || !manager.TryResolve(trigRef, out var trigArr) || trigArr == null)
                    continue;

                var tp = trigArr.Params.FirstOrDefault(p => p.Name == "triggers");
                if (tp?.Children == null) continue;

                foreach (var tr in tp.Children)
                {
                    var evParam = tr.Params.FirstOrDefault(p => p.Name == "event");
                    var inner = evParam?.Children?.FirstOrDefault();
                    if (inner == null || Val(inner, "id") != eventIndex) continue;

                    var t = Val(tr, "localTime");
                    var anim = Val(clip, "animationName");
                    results.Add(new EventUsageEntry
                    {
                        UsageType = "Trigger",
                        Direction = Sends,
                        Description = $"{Name(clip)}  at t={(string.IsNullOrEmpty(t) ? "?" : t)}",
                        Detail = string.IsNullOrEmpty(anim) ? "clip annotation" : anim,
                        ObjectId = clip.Id,
                        ClassName = "hkbClipGenerator",
                    });
                }
            }

            // ── generic modifier / misc params ────────────────────────────
            foreach (var obj in manager.ObjectMap.Values)
            {
                if (obj.ClassName == "hkbStateMachine") continue; // already covered above

                foreach (var p in obj.Params)
                {
                    var isListen = ListenParams.ContainsKey(p.Name);
                    var isSend = SendParams.ContainsKey(p.Name);
                    if (!isListen && !isSend) continue;

                    // plain value, or an inline event struct carrying an id
                    var hit = p.Value == eventIndex || IdOfInlineEvent(p) == eventIndex;
                    if (!hit) continue;

                    results.Add(new EventUsageEntry
                    {
                        UsageType = "Property",
                        Direction = isSend ? Sends : Listens,
                        Description = $"{Name(obj)}  [{p.Name}]",
                        Detail = obj.ClassName,
                        ObjectId = obj.Id,
                        ClassName = obj.ClassName,
                    });
                }
            }

            return results
                .OrderBy(r => r.Direction == Listens ? 0 : 1)
                .ThenBy(r => r.UsageType, StringComparer.Ordinal)
                .ThenBy(r => r.Description, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Usage count per event id, for the "all events" overview.</summary>
        public static Dictionary<string, int> CountAll(HavokManager manager, IEnumerable<string> eventIds)
        {
            var counts = new Dictionary<string, int>();
            foreach (var id in eventIds)
                counts[id] = Find(manager, id).Count;
            return counts;
        }

        // ─────────────────────────────────────────────────────────────────
        // transition inspection
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Checks one transition for the event in all four positions it can appear:
        /// the firing <c>eventId</c>, and the enter/exit ids of the trigger and
        /// initiate intervals.
        /// </summary>
        private static void Inspect(
            HavokManager manager, List<EventUsageEntry> results, string eventIndex,
            HkObject tr, HkObject sm, string smName, Dictionary<string, HkObject> smStates,
            string usageType, string fromLabel, string ownerId, string ownerClass)
        {
            var toStateId = Val(tr, "toStateId");
            smStates.TryGetValue(toStateId, out var toStateObj);
            var toName = toStateObj != null ? Name(toStateObj) : $"stateId:{toStateId}";

            if (Val(tr, "eventId") == eventIndex)
            {
                results.Add(new EventUsageEntry
                {
                    UsageType = usageType,
                    Direction = Listens,
                    Description = fromLabel + toName,
                    Detail = BuildDetail(manager, smName, tr, toStateObj),
                    ObjectId = ownerId,
                    ClassName = ownerClass,
                    EventId = eventIndex,
                    ToStateObjectId = toStateObj?.Id ?? "",
                });
            }

            foreach (var (intervalName, shortName) in new[]
                     {
                         ("triggerInterval", "trigger"),
                         ("initiateInterval", "initiate"),
                     })
            {
                var iv = tr.Params.FirstOrDefault(p => p.Name == intervalName)?.Children?.FirstOrDefault();
                if (iv == null) continue;

                foreach (var (field, verb) in new[]
                         {
                             ("enterEventId", "opens"),
                             ("exitEventId", "closes"),
                         })
                {
                    if (Val(iv, field) != eventIndex) continue;
                    results.Add(new EventUsageEntry
                    {
                        UsageType = "Interval",
                        Direction = Listens,
                        Description = $"{verb} {shortName} window of  {fromLabel}{toName}",
                        Detail = $"in {smName}  ({intervalName}.{field})",
                        ObjectId = ownerId,
                        ClassName = ownerClass,
                    });
                }
            }
        }

        /// <summary>
        /// Secondary line for a transition: the owning machine, plus — when the
        /// transition dives into a nested state machine — the nested state it
        /// actually lands on. <c>toNestedStateId</c> is how two events that share a
        /// destination state still select different sub-branches, so leaving it out
        /// makes such pairs look identical.
        /// </summary>
        private static string BuildDetail(HavokManager manager, string smName, HkObject tr, HkObject toStateObj)
        {
            var detail = $"in {smName}";

            var toNested = Val(tr, "toNestedStateId");
            var fromNested = Val(tr, "fromNestedStateId");

            if (!string.IsNullOrEmpty(toNested) && toNested != "-1")
            {
                var nestedSm = toStateObj != null ? FindNestedStateMachine(manager, toStateObj) : null;
                if (nestedSm != null)
                {
                    var nestedStates = ResolveStates(manager, nestedSm);
                    var label = nestedStates.TryGetValue(toNested, out var ns) ? Name(ns) : $"stateId:{toNested}";
                    detail += $"   ↳ nested: {label}";
                }
                else
                {
                    detail += $"   ↳ nested stateId {toNested}";
                }
            }

            if (!string.IsNullOrEmpty(fromNested) && fromNested != "-1" && fromNested != "0")
                detail += $"   (from nested {fromNested})";

            return detail;
        }

        /// <summary>
        /// Walks down from a state's generator to the state machine underneath it.
        /// The generator is usually a wrapper — a modifier generator, a manual
        /// selector, a blender — so the machine can be several links down.
        /// </summary>
        public static HkObject FindNestedStateMachine(HavokManager manager, HkObject stateObj, int maxDepth = 6)
        {
            var start = Val(stateObj, "generator");
            if (IsNull(start) || !manager.TryResolve(start, out var gen) || gen == null) return null;
            return Descend(manager, gen, maxDepth, new HashSet<string>());
        }

        private static readonly string[] ChildGeneratorParams =
            { "generator", "generators", "children", "referenceBehavior" };

        private static HkObject Descend(HavokManager manager, HkObject node, int depth, HashSet<string> seen)
        {
            if (node == null || depth <= 0 || !seen.Add(node.Id)) return null;
            if (node.ClassName == "hkbStateMachine") return node;

            foreach (var p in node.Params)
            {
                if (!ChildGeneratorParams.Contains(p.Name)) continue;
                foreach (var token in HkRefList.Tokens(p.Value))
                {
                    if (!manager.TryResolve(token, out var child) || child == null) continue;
                    var found = Descend(manager, child, depth - 1, seen);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // helpers
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// stateId → state object for one machine. stateIds are unique only within a
        /// machine, so callers must never resolve a transition target through a
        /// global scan.
        /// </summary>
        public static Dictionary<string, HkObject> ResolveStates(HavokManager manager, HkObject stateMachine)
        {
            var map = new Dictionary<string, HkObject>();
            var statesRef = Val(stateMachine, "states");
            if (IsNull(statesRef)) return map;

            foreach (var sref in HkRefList.Tokens(statesRef))
            {
                if (!manager.TryResolve(sref, out var st) || st == null) continue;
                var sid = Val(st, "stateId");
                if (!string.IsNullOrEmpty(sid)) map.TryAdd(sid, st);
            }
            return map;
        }

        private static IEnumerable<HkObject> TransitionsOf(HavokManager manager, HkObject owner, string paramName)
        {
            var arrRef = Val(owner, paramName);
            if (IsNull(arrRef) || !manager.TryResolve(arrRef, out var arr) || arr == null)
                return Enumerable.Empty<HkObject>();

            return arr.Params.FirstOrDefault(p => p.Name == "transitions")?.Children
                   ?? Enumerable.Empty<HkObject>();
        }

        /// <summary>Event id carried by an inline <c>hkbEvent</c>-shaped struct param.</summary>
        private static string IdOfInlineEvent(HkParam param) =>
            param?.Children?.FirstOrDefault()?.Params.FirstOrDefault(p => p.Name == "id")?.Value;

        private static string Val(HkObject obj, string paramName) =>
            obj?.Params.FirstOrDefault(p => p.Name == paramName)?.Value ?? "";

        private static string Name(HkObject obj) =>
            obj == null ? "" : (Val(obj, "name") is { Length: > 0 } n ? n : obj.Id);

        private static bool IsNull(string reference) =>
            string.IsNullOrEmpty(reference) || reference == "null";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using SageHavokEditor.Core;
using SageHavokEditor.Models;

namespace SageHavokEditor.UI
{
    // ONLY keep these here if they don't exist in other files in your project!
    // If you have BehaviorNodeData.cs, DELETE these lines from this file.
    /*
    public enum NodeType { Root, StateMachine, State, Generator, Transition, Modifier }
    public class BehaviorNodeData { ... }
    */

    public class BehaviorTreeBuilder
    {
        private readonly HavokManager _manager;

        /// <summary>
        /// Every object already expanded somewhere in the tree being built. A
        /// behaviour graph is a DAG, not a tree: the same generator hangs off
        /// many states, and expanding it once per path multiplies. Guarding on
        /// the path currently being walked — which is what this did — stops a
        /// cycle but not the multiplication, and vanilla 0_master.hkx reaches
        /// hkbClipGenerator "MRh_Unequip" by 513,980 distinct paths. The builder
        /// passed 20 million nodes there and was still going: that is the hang
        /// reported as "opening 0_master fills my RAM and never finishes".
        /// Expanding each object once is also the truer picture — the repeats
        /// are one shared object, and an edit to any of them edits all of them.
        /// </summary>
        private readonly HashSet<string> _expanded = new();

        public BehaviorTreeBuilder(HavokManager manager)
        {
            _manager = manager;
        }

        public BehaviorNodeData BuildTree(string filter = "")
        {
            _expanded.Clear();

            var rootNode = new BehaviorNodeData { Name = "Behavior Graph", Type = NodeType.Root, IsExpanded = true };

            // Top-level = state machines nothing else points at. References from
            // hkbBehaviorGraph don't count — its rootGenerator ref is what makes
            // a machine top-level in the first place.
            var childRefs = BuildChildRefSet();
            var topLevelSMs = _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine" && !childRefs.Contains(o.Id))
                .OrderBy(o => GetName(o));

            foreach (var sm in topLevelSMs)
            {
                var smNode = BuildStateMachine(sm);
                if (ApplyFilter(smNode, filter.ToLower()))
                {
                    rootNode.Children.Add(smNode);
                }
            }

            if (string.IsNullOrWhiteSpace(filter)) ExpandWithinBudget(rootNode);

            return rootNode;
        }

        /// <summary>
        /// How many nodes a freshly opened tree may show. Every visible node is a
        /// realised WPF visual, and the TreeView doesn't virtualise (its item
        /// template hosts children in a StackPanel, which measures with infinite
        /// height), so "expand everything" means building one visual per node
        /// before the window can paint: vanilla-sized behaviours run to thousands.
        /// </summary>
        private const int ExpansionBudget = 500;

        /// <summary>
        /// Opens the tree breadth-first until the budget is spent, so a small file
        /// still comes up fully expanded the way it always did, and a large one
        /// opens as far down as it can afford — the rest expands on click.
        /// </summary>
        private static void ExpandWithinBudget(BehaviorNodeData root)
        {
            root.IsExpanded = true;
            var realised = 1 + root.Children.Count;

            var queue = new Queue<BehaviorNodeData>(root.Children);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node.Children.Count == 0) continue;
                if (realised + node.Children.Count > ExpansionBudget) continue;

                node.IsExpanded = true;
                realised += node.Children.Count;
                foreach (var child in node.Children) queue.Enqueue(child);
            }
        }

        private bool ApplyFilter(BehaviorNodeData node, string filter)
        {
            // If the search box is empty, show EVERYTHING
            if (string.IsNullOrWhiteSpace(filter))
            {
                node.IsVisible = true;
                foreach (var child in node.Children)
                {
                    ApplyFilter(child, filter); // Ensure all descendants are also visible
                }
                return true;
            }

            bool matches = node.Name.ToLower().Contains(filter);
            bool childMatches = false;

            foreach (var child in node.Children)
            {
                if (ApplyFilter(child, filter)) childMatches = true;
            }

            node.IsVisible = matches || childMatches;
            // A match deeper in the tree is only findable if the path to it is open.
            if (childMatches) node.IsExpanded = true;
            return node.IsVisible;
        }

        private HashSet<string> BuildChildRefSet()
        {
            var refs = new HashSet<string>();
            foreach (var parent in _manager.ObjectMap.Values)
            {
                if (parent.ClassName == "hkbBehaviorGraph") continue;
                foreach (var p in parent.Params)
                    foreach (var tok in HkRefList.Tokens(p.Value))
                        if (tok.StartsWith("#"))
                            refs.Add(tok);
            }
            return refs;
        }

        /// <summary>
        /// Stands in for an object that is already in the tree: a leaf, but one
        /// that still carries the object, so clicking it opens in the property
        /// editor exactly what the first occurrence opens.
        ///
        /// The marker leads rather than trails. The tree pane is a fixed ~220px
        /// and the nodes that repeat are the deeply indented ones, so anything
        /// appended to the name sits off the right edge \u2014 opening 0_master.hkx
        /// in the editor, names at that depth were already clipped mid-word.
        /// </summary>
        private static BehaviorNodeData SharedRef(string label, NodeType type, HkObject obj) =>
            new() { Name = "\u2197 " + label, Type = type, Object = obj };

        private BehaviorNodeData BuildStateMachine(HkObject sm)
        {
            if (!_expanded.Add(sm.Id))
                return SharedRef(GetName(sm), NodeType.StateMachine, sm);

            var node = new BehaviorNodeData { Name = GetName(sm), Type = NodeType.StateMachine, Object = sm };

            var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
            if (statesParam != null)
            {
                var ids = HkRefList.Tokens(statesParam.Value);
                foreach (var id in ids)
                {
                    if (_manager.TryResolve(id, out var state) && state != null)
                        node.Children.Add(BuildState(state, sm));
                }
            }
            return node;
        }

        private BehaviorNodeData BuildState(HkObject state, HkObject parentMachine)
        {
            var stateNode = new BehaviorNodeData { Name = GetName(state), Type = NodeType.State, Object = state };

            var genParam = state.Params?.FirstOrDefault(p => p.Name == "generator");
            if (genParam != null && _manager.TryResolve(genParam.Value, out var gen))
            {
                var genFolder = new BehaviorNodeData { Name = "Logic (Generator)", Type = NodeType.Generator };
                var resolvedGen = ResolveGenerator(gen);
                if (resolvedGen != null) genFolder.Children.Add(resolvedGen);
                stateNode.Children.Add(genFolder);
            }

            var transParam = state.Params?.FirstOrDefault(p => p.Name == "transitions");
            if (transParam != null && _manager.TryResolve(transParam.Value, out var transArray) && transArray != null)
            {
                var transFolder = new BehaviorNodeData { Name = "Transitions", Type = NodeType.Transition };
                foreach (var p in transArray.Params)
                {
                    if (_manager.TryResolve(p.Value, out var tr) && tr != null)
                    {
                        var targetName = GetTargetStateName(tr, parentMachine);
                        transFolder.Children.Add(new BehaviorNodeData { Name = $"→ {targetName}", Object = tr });
                    }
                }
                if (transFolder.Children.Count > 0) stateNode.Children.Add(transFolder);
            }
            return stateNode;
        }

        private BehaviorNodeData? ResolveGenerator(HkObject? generator)
        {
            if (generator == null) return null;
            if (generator.ClassName == "hkbStateMachine") return BuildStateMachine(generator);

            var label = $"{GetName(generator)} ({generator.ClassName})";
            if (!_expanded.Add(generator.Id))
                return SharedRef(label, NodeType.Generator, generator);

            var node = new BehaviorNodeData { Name = label, Object = generator };

            // Generic recursion: follow every #ref in every param, so Bethesda
            // classes (pDefaultGenerator, ChildrenA, pClipGenerator, …) resolve
            // the same as the stock hkb param names.
            foreach (var param in generator.Params)
            {
                foreach (var tok in HkRefList.Tokens(param.Value))
                {
                    if (tok.StartsWith("#") && _manager.TryResolve(tok, out var child))
                    {
                        var resolved = ResolveGenerator(child);
                        if (resolved != null) node.Children.Add(resolved);
                    }
                }
            }
            return node;
        }

        private string GetTargetStateName(HkObject transition, HkObject parentMachine)
        {
            var toStateIdParam = transition.Params?.FirstOrDefault(p => p.Name == "toStateId");
            if (toStateIdParam == null) return "Unknown Target";

            var targetState = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbStateMachineStateInfo" &&
                                o.Params.Any(p => p.Name == "stateId" && p.Value == toStateIdParam.Value));

            return targetState != null ? GetName(targetState) : $"State ID: {toStateIdParam.Value}";
        }

        private string GetName(HkObject? obj)
        {
            if (obj == null) return "Null Object";
            var nameParam = obj.Params?.FirstOrDefault(p => p.Name == "name");
            return nameParam?.Value ?? obj.Id;
        }

    }
}

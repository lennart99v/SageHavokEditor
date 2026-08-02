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

        public BehaviorTreeBuilder(HavokManager manager)
        {
            _manager = manager;
        }

        public BehaviorNodeData BuildTree(string filter = "")
        {
            var rootNode = new BehaviorNodeData { Name = "Behavior Graph", Type = NodeType.Root };

            // Top-level = state machines nothing else points at. References from
            // hkbBehaviorGraph don't count — its rootGenerator ref is what makes
            // a machine top-level in the first place.
            var childRefs = BuildChildRefSet();
            var topLevelSMs = _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine" && !childRefs.Contains(o.Id))
                .OrderBy(o => GetName(o));

            foreach (var sm in topLevelSMs)
            {
                var smNode = BuildStateMachine(sm, new HashSet<string>());
                if (ApplyFilter(smNode, filter.ToLower()))
                {
                    rootNode.Children.Add(smNode);
                }
            }

            return rootNode;
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

        private BehaviorNodeData BuildStateMachine(HkObject sm, HashSet<string> path)
        {
            var node = new BehaviorNodeData { Name = GetName(sm), Type = NodeType.StateMachine, Object = sm };
            if (!path.Add(sm.Id)) return node;  // cycle guard

            var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
            if (statesParam != null)
            {
                var ids = HkRefList.Tokens(statesParam.Value);
                foreach (var id in ids)
                {
                    if (_manager.TryResolve(id, out var state) && state != null)
                        node.Children.Add(BuildState(state, sm, path));
                }
            }
            path.Remove(sm.Id);
            return node;
        }

        private BehaviorNodeData BuildState(HkObject state, HkObject parentMachine, HashSet<string> path)
        {
            var stateNode = new BehaviorNodeData { Name = GetName(state), Type = NodeType.State, Object = state };

            var genParam = state.Params?.FirstOrDefault(p => p.Name == "generator");
            if (genParam != null && _manager.TryResolve(genParam.Value, out var gen))
            {
                var genFolder = new BehaviorNodeData { Name = "Logic (Generator)", Type = NodeType.Generator };
                var resolvedGen = ResolveGenerator(gen, path);
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

        private BehaviorNodeData? ResolveGenerator(HkObject? generator, HashSet<string> path)
        {
            if (generator == null) return null;
            if (generator.ClassName == "hkbStateMachine") return BuildStateMachine(generator, path);

            var node = new BehaviorNodeData { Name = $"{GetName(generator)} ({generator.ClassName})", Object = generator };
            if (!path.Add(generator.Id)) return node;  // cycle guard

            // Generic recursion: follow every #ref in every param, so Bethesda
            // classes (pDefaultGenerator, ChildrenA, pClipGenerator, …) resolve
            // the same as the stock hkb param names.
            foreach (var param in generator.Params)
            {
                foreach (var tok in HkRefList.Tokens(param.Value))
                {
                    if (tok.StartsWith("#") && _manager.TryResolve(tok, out var child))
                    {
                        var resolved = ResolveGenerator(child, path);
                        if (resolved != null) node.Children.Add(resolved);
                    }
                }
            }
            path.Remove(generator.Id);
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

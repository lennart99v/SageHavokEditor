using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Models;

namespace SkyrimHavokEditor.UI
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

            // Logic to find top-level state machines
            var topLevelSMs = _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine" && !IsReferencedAsChild(o))
                .OrderBy(o => GetName(o));

            foreach (var sm in topLevelSMs)
            {
                var smNode = BuildStateMachine(sm);
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

        private bool IsReferencedAsChild(HkObject obj)
        {
            // We only care if it's referenced as a sub-component of another behavior object
            return _manager.ObjectMap.Values.Any(parent =>
                parent != obj &&
                (parent.ClassName == "hkbStateMachine" || (parent.ClassName?.Contains("Generator") ?? false)) &&
                parent.Params.Any(p => p.Value == obj.Id));
        }

        private BehaviorNodeData BuildStateMachine(HkObject sm)
        {
            var node = new BehaviorNodeData { Name = GetName(sm), Type = NodeType.StateMachine, Object = sm };

            var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
            if (statesParam != null)
            {
                var ids = statesParam.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var id in ids)
                {
                    if (_manager.TryResolve(id, out var state))
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
                genFolder.Children.Add(ResolveGenerator(gen));
                stateNode.Children.Add(genFolder);
            }

            var transParam = state.Params?.FirstOrDefault(p => p.Name == "transitions");
            if (transParam != null && _manager.TryResolve(transParam.Value, out var transArray))
            {
                var transFolder = new BehaviorNodeData { Name = "Transitions", Type = NodeType.Transition };
                foreach (var p in transArray.Params)
                {
                    if (_manager.TryResolve(p.Value, out var tr))
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

            var node = new BehaviorNodeData { Name = $"{GetName(generator)} ({generator.ClassName})", Object = generator };

            foreach (var param in generator.Params)
            {
                if (IsLinkParameter(param.Name) && _manager.TryResolve(param.Value, out var child))
                {
                    var resolved = ResolveGenerator(child);
                    if (resolved != null) node.Children.Add(resolved);
                }
            }
            return node;
        }

        private bool IsLinkParameter(string name) =>
            name == "generator" || name == "modifier" || name == "modifiers" || name == "rootGenerator";

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

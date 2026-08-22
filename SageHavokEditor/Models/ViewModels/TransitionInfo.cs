using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SageHavokEditor.Models;

namespace SageHavokEditor.Models.ViewModels
{
    public class TransitionInfo
    {
        public string FromState { get; set; } = "";
        public string ToState { get; set; } = "";
        public string EventName { get; set; } = "";
        public string EventId { get; set; } = "";
        public string BlendDuration { get; set; } = "";
        public string Flags { get; set; } = "";
        public string TransitionEffect { get; set; } = "";

        /// <summary>
        /// The hkbStateMachineTransitionInfo this row came from.
        ///
        /// Most of a transition's fields — toNestedStateId, fromNestedStateId,
        /// priority, condition, triggerInterval, initiateInterval — live here, NOT on
        /// the hkbBlendingTransitionEffect that <see cref="TransitionEffect"/> points
        /// at (that only carries duration / blendCurve / endMode /
        /// toGeneratorStartTimeFraction). Reading them off the effect silently yields
        /// nothing. Transitions are inline structs with no id of their own, so the
        /// object reference is the only way to get back to them.
        /// </summary>
        public HkObject? Source { get; set; }

        /// <summary>
        /// The state machine that owns this transition. Needed to resolve toStateId
        /// and toNestedStateId, both of which are only unique within a machine.
        /// </summary>
        public HkObject? OwnerStateMachine { get; set; }
    }
}

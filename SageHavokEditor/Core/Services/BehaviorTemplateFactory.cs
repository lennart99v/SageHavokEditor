using System.Collections.Generic;
using System.IO;
using HKX2;

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Builds the minimal valid behavior file: hkRootLevelContainer → hkbBehaviorGraph
    /// → graph data / string data / variable value set, with an empty root state machine
    /// as the root generator. Serialized through HKX2's own XmlSerializer so signatures,
    /// defaults, and object ids come out exactly as in a real SSE file — the root
    /// container lands on #0050, matching the toplevelobject the save pipeline writes.
    /// </summary>
    public static class BehaviorTemplateFactory
    {
        public static void WriteMinimalBehaviorXml(string graphName, string rootMachineName, string outPath)
        {
            // Enum-typed fields default to 0, which is already the vanilla choice for all
            // three (VARIABLE_MODE_DISCARD_WHEN_INACTIVE, START_STATE_MODE_DEFAULT,
            // SELF_TRANSITION_MODE_NO_TRANSITION). The -1s are the Havok "no event/variable"
            // sentinels — HKX2's zero defaults would reference event 0 instead.
            var rootMachine = new hkbStateMachine
            {
                m_name = rootMachineName,
                m_eventToSendWhenStateOrTransitionChanges = new hkbEvent { m_id = -1 },
                m_returnToPreviousStateEventId = -1,
                m_randomTransitionEventId = -1,
                m_transitionToNextHigherStateEventId = -1,
                m_transitionToNextLowerStateEventId = -1,
                m_syncVariableIndex = -1,
                m_maxSimultaneousTransitions = 32,
            };

            var graph = new hkbBehaviorGraph
            {
                m_name = graphName,
                m_rootGenerator = rootMachine,
                m_data = new hkbBehaviorGraphData
                {
                    m_stringData = new hkbBehaviorGraphStringData(),
                    m_variableInitialValues = new hkbVariableValueSet(),
                },
            };

            var root = new hkRootLevelContainer
            {
                m_namedVariants = new List<hkRootLevelContainerNamedVariant?>
                {
                    new hkRootLevelContainerNamedVariant
                    {
                        m_name = "hkbBehaviorGraph",
                        m_className = "hkbBehaviorGraph",
                        m_variant = graph,
                    }
                }
            };

            using var fs = File.Create(outPath);
            new XmlSerializer().Serialize(root, HKXHeader.SkyrimSE(), fs);
        }
    }
}

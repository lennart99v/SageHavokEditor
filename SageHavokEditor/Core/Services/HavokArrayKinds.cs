using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HKX2;
using SageHavokEditor.Models;
using Type = System.Type;   // HKX2 declares its own Type enum

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Whether an array member holds inline structs or pointers to other objects.
    ///
    /// The distinction is invisible to reflection — both kinds are declared
    /// <c>IList&lt;T&gt;</c> over a class implementing <see cref="IHavokObject"/> — and
    /// invisible in a loaded file once the array is empty, which is exactly when the
    /// property editor needs it: an empty inline array and an empty ref array look
    /// identical in the data, so "+ Add element" had nothing to key on.
    ///
    /// HKX2 does know, in the one place that matters: <c>WriteXml</c> calls either
    /// <c>WriteClassArray</c> (nested hkobjects) or <c>WriteClassPointerArray</c>
    /// (#id text) per member, and each call is preceded by a <c>nameof(m_member)</c>
    /// string literal. So the kind is read out of the method's IL, keyed by that
    /// literal. That is the same authority the editor round-trips through, rather
    /// than a rule inferred about it.
    ///
    /// The obvious inference — pointer arrays point at hkReferencedObject
    /// descendants, struct arrays don't — is right for 141 of HKX2's 143 array
    /// members and wrong for hkpSerializedTrack1nInfo's two, which point at plain
    /// IHavokObject elements. Physics classes that never occur in a behaviour file,
    /// but there's no reason to carry a rule with known exceptions when the exact
    /// answer is this cheap.
    /// </summary>
    public static class HavokArrayKinds
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<Type, IReadOnlyDictionary<string, HkArrayKind>> _byType = new();

        /// <summary>
        /// Array kind per hkparam name (the m_ prefix stripped, as HKX2's XML writes
        /// it) for one class, inherited members included. Members that aren't arrays
        /// are absent; a class HKX2 doesn't serialize returns an empty map.
        /// </summary>
        public static IReadOnlyDictionary<string, HkArrayKind> ForType(Type type)
        {
            lock (_lock)
            {
                if (_byType.TryGetValue(type, out var cached)) return cached;
                var map = Scan(type);
                _byType[type] = map;
                return map;
            }
        }

        private static IReadOnlyDictionary<string, HkArrayKind> Scan(Type type)
        {
            var map = new Dictionary<string, HkArrayKind>(StringComparer.Ordinal);

            // Base first, so a derived override of the same member name wins. None
            // exist today; the ordering just keeps that from being a silent surprise.
            var chain = new List<Type>();
            for (var t = type; t != null && typeof(IHavokObject).IsAssignableFrom(t); t = t.BaseType)
                chain.Insert(0, t);

            foreach (var t in chain)
            {
                var write = t.GetMethod("WriteXml",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (write == null) continue;
                ScanMethod(write, map);
            }

            return map;
        }

        private static void ScanMethod(MethodInfo method, Dictionary<string, HkArrayKind> map)
        {
            byte[]? il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch (Exception) { return; }   // no managed body to read — nothing to learn
            if (il == null) return;

            var module = method.Module;
            string? lastString = null;

            foreach (var (op, operandAt) in Walk(il))
            {
                if (op == OpCodes.Ldstr)
                {
                    try { lastString = module.ResolveString(BitConverter.ToInt32(il, operandAt)); }
                    catch (Exception) { lastString = null; }
                    continue;
                }

                if (op != OpCodes.Call && op != OpCodes.Callvirt) continue;
                if (lastString == null) continue;

                string name;
                try { name = module.ResolveMethod(BitConverter.ToInt32(il, operandAt))?.Name ?? ""; }
                catch (Exception) { continue; }   // a token this module can't resolve alone

                var kind = name switch
                {
                    "WriteClassArray" => HkArrayKind.InlineStruct,
                    "WriteClassPointerArray" => HkArrayKind.Pointer,
                    _ => HkArrayKind.None
                };
                if (kind == HkArrayKind.None) continue;

                // nameof(m_states) → the hkparam name "states"
                var param = lastString.StartsWith("m_", StringComparison.Ordinal)
                    ? lastString.Substring(2)
                    : lastString;
                map[param] = kind;
            }
        }

        // ── Minimal IL reader ────────────────────────────────────────────────────
        // Enough to step instruction to instruction and hand back where each
        // operand starts. The opcode table is built from OpCodes itself rather
        // than transcribed, so it can't drift out of date.

        private static Dictionary<ushort, OpCode>? _opcodes;

        private static Dictionary<ushort, OpCode> Opcodes()
        {
            if (_opcodes != null) return _opcodes;
            var table = new Dictionary<ushort, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is OpCode op)
                    table[unchecked((ushort)op.Value)] = op;
            return _opcodes = table;
        }

        private static IEnumerable<(OpCode Op, int OperandAt)> Walk(byte[] il)
        {
            var table = Opcodes();
            int pos = 0;

            while (pos < il.Length)
            {
                ushort code = il[pos++];
                if (code == 0xFE)
                {
                    if (pos >= il.Length) yield break;
                    code = (ushort)(0xFE00 | il[pos++]);
                }

                // An opcode we don't know means we've lost the instruction
                // boundary; everything after it would be noise, so stop.
                if (!table.TryGetValue(code, out var op)) yield break;

                int size = OperandSize(op.OperandType, il, pos);
                if (size < 0 || pos + size > il.Length) yield break;

                yield return (op, pos);
                pos += size;
            }
        }

        private static int OperandSize(OperandType type, byte[] il, int at) => type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => at + 4 <= il.Length
                ? 4 + 4 * BitConverter.ToInt32(il, at)
                : -1,
            _ => -1,
        };
    }
}

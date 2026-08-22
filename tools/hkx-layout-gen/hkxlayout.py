"""Shared layout model for the HKX2 autogen classes.

The classes under libs/HKX2Library/HKX2/Autogen were dumped from the Skyrim SE
(64-bit) runtime, so their Read/Write bodies carry padding constants baked to an
8-byte pointer.  This module rebuilds the Havok layout rules from the metadata
comments that ship above each class, so the same padding can be recomputed for a
4-byte pointer (Skyrim LE).

The model is validated by reproducing the recorded 64-bit offsets and class
sizes exactly -- see validate.py.
"""

import glob
import os
import re

HDR_RE = re.compile(
    r'^\s*//\s*(\w+)\s+Signatire:\s*0x([0-9a-fA-F]+)\s+size:\s*(\d+)\s+flags:\s*(\S*)')
MEM_RE = re.compile(
    r'^\s*//\s*(m_\w+)\s+m_class:\s*(\S*)\s+Type\.(\w+)\s+Type\.(\w+)\s+'
    r'arrSize:\s*(\d+)\s+offset:\s*(\d+)\s+flags:\s*(\S*)\s*enum:\s*(\S*)?')
CLS_RE = re.compile(r'^\s*public\s+(?:partial\s+)?class\s+(\w+)\s*:\s*([\w<>?, ]+)')
FIELD_RE = re.compile(
    r'^\s*(?:public|private|internal|protected)\s+(?:override\s+)?'
    r'([\w<>?., ]+?)\s+(m_\w+)\s*\{\s*set;\s*get;\s*\}')

# Classes whose generated bodies contain variable-length reads or explicit
# NotImplementedException throws.  They are type-metadata / runtime-only classes
# that never appear in a serialised behaviour, character, project, skeleton or
# animation file, and they are already unsupported for Skyrim SE today.
UNSUPPORTED = {
    'hkClass', 'hkClassEnum', 'hkClassMember', 'hkCustomAttributes',
    'hkCustomAttributesAttribute', 'hkPackfileSectionHeader',
    'hkaFootstepAnalysisInfo', 'hkbCharacter',
}


def parse_classes(hkx_root):
    """Parse every Autogen/Manual class into {name: {...}}."""
    classes = {}
    files = (glob.glob(os.path.join(hkx_root, 'Autogen', '*.cs'))
             + glob.glob(os.path.join(hkx_root, 'Manual', '*.cs')))
    for path in files:
        lines = open(path, encoding='utf-8-sig').read().splitlines()
        name = size = base = None
        members, fields = [], {}
        for line in lines:
            m = HDR_RE.match(line)
            if m and name is None:
                name, size = m.group(1), int(m.group(3))
                continue
            m = MEM_RE.match(line)
            if m:
                members.append(dict(name=m.group(1), cls=m.group(2), t=m.group(3),
                                    sub=m.group(4), arr=int(m.group(5)),
                                    off=int(m.group(6)), flags=m.group(7)))
                continue
            m = CLS_RE.match(line)
            if m and name is not None and m.group(1) == name and base is None:
                for b in (x.strip() for x in m.group(2).split(',')):
                    if b.startswith('IEquatable') or b == 'IHavokObject':
                        continue
                    base = b
                continue
            m = FIELD_RE.match(line)
            if m and name is not None:
                fields[m.group(2)] = m.group(1).strip()
        if name:
            classes[name] = dict(size=size, base=base, members=members,
                                 fields=fields, path=path)
    return classes


def prim(t, sub, P):
    """(size, align) for a Havok primitive type at pointer size P."""
    table = {
        'TYPE_VOID': (0, 1), 'TYPE_BOOL': (1, 1), 'TYPE_CHAR': (1, 1),
        'TYPE_INT8': (1, 1), 'TYPE_UINT8': (1, 1),
        'TYPE_INT16': (2, 2), 'TYPE_UINT16': (2, 2), 'TYPE_HALF': (2, 2),
        'TYPE_INT32': (4, 4), 'TYPE_UINT32': (4, 4), 'TYPE_REAL': (4, 4),
        'TYPE_INT64': (8, 8), 'TYPE_UINT64': (8, 8),
        'TYPE_ULONG': (P, P), 'TYPE_POINTER': (P, P),
        'TYPE_FUNCTIONPOINTER': (P, P),
        'TYPE_CSTRING': (P, P), 'TYPE_STRINGPTR': (P, P),
        'TYPE_VECTOR4': (16, 16), 'TYPE_QUATERNION': (16, 16),
        'TYPE_MATRIX3': (48, 16), 'TYPE_ROTATION': (48, 16),
        'TYPE_QSTRANSFORM': (48, 16),
        'TYPE_MATRIX4': (64, 16), 'TYPE_TRANSFORM': (64, 16),
        'TYPE_ARRAY': (P + 8, P), 'TYPE_HOMOGENEOUSARRAY': (P + 8, P),
        'TYPE_SIMPLEARRAY': (P + 4, P),
        'TYPE_VARIANT': (2 * P, P),
        'TYPE_RELARRAY': (4, 2), 'TYPE_ZERO': (0, 1),
    }
    if t in ('TYPE_ENUM', 'TYPE_FLAGS'):
        return table.get(sub, (None, None))
    return table.get(t, (None, None))


class Layout:
    """Computes class sizes/alignments/member offsets at a given pointer size."""

    def __init__(self, classes):
        self.classes = classes
        self._memo = {}
        self._members = {}
        # Extra vtable pointers contributed by multiple inheritance, derived
        # from the recorded 64-bit offsets (see derive_extra_vtables).
        self.extra = {}

    def align_of(self, name, P):
        return self.calc(name, P)[1]

    def size_of(self, name, P):
        return self.calc(name, P)[0]

    def members_of(self, name, P):
        self.calc(name, P)
        return self._members.get((name, P), [])

    def calc(self, name, P, path=()):
        key = (name, P)
        if key in self._memo:
            return self._memo[key]
        if name in path:                      # cycle guard
            return (0, 1)
        c = self.classes.get(name)
        if c is None:
            return (None, None)

        if c['base']:
            off, align = self.calc(c['base'], P, path + (name,))
            if off is None:
                self._memo[key] = (None, None)
                return self._memo[key]
            align = max(align, 1)
        elif name == 'hkBaseObject':
            off, align = P, P                 # vtable pointer
        else:
            off, align = 0, 1

        off += self.extra.get(name, 0) * P    # extra vtables

        placed = []
        for m in c['members']:
            if m['t'] == 'TYPE_STRUCT' or (m['t'] == 'TYPE_ENUM' and m['sub'] == 'TYPE_STRUCT'):
                sz, al = self.calc(m['cls'], P, path + (name,))
            else:
                sz, al = prim(m['t'], m['sub'], P)
            if sz is None:
                self._memo[key] = (None, None)
                return self._memo[key]
            if m['arr'] > 0:
                sz *= m['arr']
            al = max(al, 1)
            if 'ALIGN_16' in m['flags']:
                al = max(al, 16)
            elif 'ALIGN_8' in m['flags']:
                al = max(al, 8)
            off = (off + al - 1) // al * al
            placed.append((m, off))
            align = max(align, al)
            off += sz

        total = (off + align - 1) // align * align if align else off
        if total == 0:
            total = 1                         # an empty C++ class occupies 1 byte
        self._memo[key] = (total, align)
        self._members[key] = placed
        return self._memo[key]

    def derive_extra_vtables(self, rounds=4):
        """Infer per-class extra vtable pointers from the recorded 64-bit data.

        Classes that multiply-inherit (e.g. BSRagdollContactListenerModifier,
        which is both an hkbModifier and an hkpContactListener) carry one extra
        vtable pointer per additional polymorphic base.  The dumped metadata
        does not record them, but the gap they leave before the first member
        does.  Extra vtables are pointer-sized, so recording the count is enough
        to reproduce them at any pointer size.
        """
        for _ in range(rounds):
            changed = False
            for name, c in sorted(self.classes.items()):
                if c['size'] is None or not c['members']:
                    continue
                self._memo.clear()
                self._members.clear()
                self.calc(name, 8)
                placed = self._members.get((name, 8), [])
                if not placed:
                    continue
                m0, off0 = placed[0]
                gap = m0['off'] - off0
                if gap > 0 and gap % 8 == 0 and 'ALIGN_16' not in m0['flags']:
                    self.extra[name] = self.extra.get(name, 0) + gap // 8
                    changed = True
            if not changed:
                break
        self._memo.clear()
        self._members.clear()
        return self.extra

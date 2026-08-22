"""Rewrite the HKX2 autogen Read/Write bodies to be pointer-size aware.

Every `br.Position += N;` in libs/HKX2Library/HKX2/Autogen is alignment padding
computed for Skyrim SE's 8-byte pointer.  This script simulates each generated
body, proves it can reproduce every one of those constants from Havok's
alignment rules, then rewrites the ones that differ under a 4-byte pointer
(Skyrim LE) as `br.Position += des.Padding(pad64, pad32);`.

Run with --check to validate without writing.
"""

import argparse
import os
import re
import sys

from hkxlayout import Layout, parse_classes, prim, UNSUPPORTED

HKX_ROOT = os.path.join(os.path.dirname(__file__),
                        '..', '..', 'libs', 'HKX2Library', 'HKX2')

PAD_RE = re.compile(r'^(\s*)(br|bw)\.Position \+= (\d+);\s*$')

# (size, align) for the fixed-width statement forms, independent of pointer size.
FIXED = {
    'ReadSingle': (4, 4), 'ReadBoolean': (1, 1), 'ReadByte': (1, 1),
    'ReadSByte': (1, 1), 'ReadInt16': (2, 2), 'ReadUInt16': (2, 2),
    'ReadHalf': (2, 2), 'ReadInt32': (4, 4), 'ReadUInt32': (4, 4),
    'ReadInt64': (8, 8), 'ReadUInt64': (8, 8),
    'ReadVector4': (16, 16), 'ReadQuaternion': (16, 16),
    'ReadQSTransform': (48, 16), 'ReadMatrix3': (48, 16),
    'ReadRotation': (48, 16), 'ReadTransform': (64, 16), 'ReadMatrix4': (64, 16),
}
# element (size, align) for the CStyleArray forms
CSTYLE_ELEM = {
    'Byte': (1, 1), 'SByte': (1, 1), 'Boolean': (1, 1),
    'Int16': (2, 2), 'UInt16': (2, 2), 'Half': (2, 2),
    'Int32': (4, 4), 'UInt32': (4, 4), 'Single': (4, 4),
    'Int64': (8, 8), 'UInt64': (8, 8),
    'Vector4': (16, 16), 'Quaternion': (16, 16),
    'QSTransform': (48, 16), 'Matrix3': (48, 16), 'Matrix4': (64, 16),
    'Transform': (64, 16),
}
# Order matters: the Array forms must be tested before the pointer forms, since
# "ReadClassPointerArray" contains "ReadClassPointer".
CSTYLE_RE = re.compile(r'des\.Read(\w+?)CStyleArray(?:<[^>]*>)?\(br,\s*(\d+)\)')
ARRAY_RE = re.compile(r'des\.Read(?:\w+?)Array(?:<[^>]*>)?\(br\)')
PTR_RE = re.compile(
    r'des\.Read(?:ClassPointer(?:<[^>]*>)?|StringPointer|CString|EmptyPointer)\(br\)')
DES_FIXED_RE = re.compile(r'des\.Read(\w+)\(br\)')
BR_FIXED_RE = re.compile(r'br\.(Read\w+)\(\)')
ASCII_N_RE = re.compile(r'br\.ReadASCII\((\d+)\)')


class Unsupported(Exception):
    pass


def ulong_members(cls, classes):
    """Names of this class's own hkUlong members."""
    return {m['name'] for m in classes[cls]['members'] if m['t'] == 'TYPE_ULONG'}


def apply_usize(line, names):
    """Rewrite hkUlong accesses to the pointer-sized reader/writer."""
    m = re.match(r'^(\s*)(m_\w+)\s*=\s*br\.ReadUInt64\(\);\s*$', line)
    if m and m.group(2) in names:
        return f'{m.group(1)}{m.group(2)} = br.ReadUSize();'
    m = re.match(r'^(\s*)bw\.WriteUInt64\((m_\w+)\);\s*$', line)
    if m and m.group(2) in names:
        return f'{m.group(1)}bw.WriteUSize({m.group(2)});'
    return line


def classify(line, cls, classes):
    """Return a kind tuple describing what one statement consumes.

    ('pad', n)                 -- an existing `Position += n`
    ('base', name)             -- base.Read
    ('struct', name)           -- a nested struct member
    ('fixed', size, align)     -- pointer-size independent
    ('ptr',)                   -- one pointer
    ('array',)                 -- an hkArray (ptr + size + capacity)
    ('cstyle', size, align)    -- C-style array, fixed element
    ('ptrcstyle', n)           -- C-style array of pointers
    ('bytes', n)               -- raw ReadBytes(n)
    """
    s = line.strip()
    if not s or s.startswith('//'):
        return None

    m = PAD_RE.match(line)
    if m:
        return ('pad', int(m.group(3)))

    if s.startswith('base.Read(') or s.startswith('base.Write('):
        return ('base', classes[cls]['base'])

    if 'NotImplementedException' in s or re.search(r'br\.ReadASCII\(\)', s):
        raise Unsupported(s)

    m = ASCII_N_RE.search(s)
    if m:
        return ('bytes', int(m.group(1)))

    m = re.match(r'^(?:m_\w+\s*=\s*)?(m_\w+)\.Read\(des, br\);$', s)
    if m:
        ftype = classes[cls]['fields'].get(m.group(1))
        if ftype is None:
            raise Unsupported('unknown field type for ' + s)
        return ('struct', ftype.rstrip('?'))

    m = re.search(r'br\.ReadBytes\((\d+)\)', s)
    if m:
        return ('bytes', int(m.group(1)))

    # hkUlong is pointer-sized (8 bytes on x64, 4 on x86) but the dump emitted
    # an unconditional ReadUInt64; ulong_members() rewrites the call site.
    m = re.match(r'^(m_\w+)\s*=\s*br\.ReadUInt64\(\);$', s)
    if m and m.group(1) in ulong_members(cls, classes):
        return ('ptr',)

    m = CSTYLE_RE.search(s)
    if m:
        kind, n = m.group(1), int(m.group(2))
        if kind == 'ClassPointer':
            return ('ptrcstyle', n)
        if kind == 'Struct':
            g = re.search(r'ReadStructCStyleArray<(\w+)>', s)
            if not g:
                raise Unsupported(s)
            return ('structarr', g.group(1), n)
        if kind not in CSTYLE_ELEM:
            raise Unsupported(s)
        sz, al = CSTYLE_ELEM[kind]
        return ('cstyle', sz * n, al)

    if ARRAY_RE.search(s):
        return ('array',)

    if PTR_RE.search(s):
        return ('ptr',)

    m = DES_FIXED_RE.search(s)
    if m and 'Read' + m.group(1) in FIXED:
        return ('fixed',) + FIXED['Read' + m.group(1)]

    m = BR_FIXED_RE.search(s)
    if m and m.group(1) in FIXED:
        return ('fixed',) + FIXED[m.group(1)]

    raise Unsupported(s)


def consume(kind, P, lay):
    """(size, align) that a statement occupies at pointer size P."""
    k = kind[0]
    if k == 'base':
        return (lay.size_of(kind[1], P), lay.align_of(kind[1], P))
    if k == 'struct':
        return (lay.size_of(kind[1], P), lay.align_of(kind[1], P))
    if k == 'fixed':
        return (kind[1], kind[2])
    if k == 'ptr':
        return (P, P)
    if k == 'array':
        return (P + 8, P)
    if k == 'cstyle':
        return (kind[1], kind[2])
    if k == 'ptrcstyle':
        return (P * kind[1], P)
    if k == 'structarr':
        sz, al = lay.calc(kind[1], P)
        return (sz * kind[2], al)
    if k == 'bytes':
        return (kind[1], 1)
    raise Unsupported(str(kind))


def roundup(v, a):
    return (v + a - 1) // a * a


def split_body(name, body_lines, classes, simple=False):
    """Split a body into (statement lines, kinds, original pad map).

    The pad map is {statement index it precedes: recorded 64-bit constant},
    with the key len(kinds) meaning trailing padding.  In `simple` mode any
    non-padding line counts as a statement without being classified -- used for
    Write bodies, whose statement order mirrors the Read body exactly.
    """
    align16 = {m['name'] for m in classes[name]['members']
               if 'ALIGN_16' in m['flags']} if not simple else set()
    stmts, kinds, pads = [], [], {}
    for line in body_lines:
        if simple:
            m = PAD_RE.match(line)
            k = ('pad', int(m.group(3))) if m else (
                None if not line.strip() else ('stmt',))
        else:
            k = classify(line, name, classes)
        if k is None:
            stmts.append(('raw', line))
            continue
        if k[0] == 'pad':
            idx = sum(1 for s in stmts if s[0] == 'stmt')
            if idx in pads:
                raise Unsupported('consecutive padding statements')
            pads[idx] = k[1]
        else:
            # An ALIGN_16 member aligns to 16 at either pointer size.  It has to
            # come from the metadata: when the 64-bit layout happens to be
            # 16-aligned already the padding is 0 there and only the 32-bit
            # layout reveals the flag.
            m = re.match(r'^\s*(m_\w+)\s*[=.]', line)
            if m and m.group(1) in align16:
                k = k + ('align16',)
            stmts.append(('stmt', line))
            kinds.append(k)
    return stmts, kinds, pads


def plan_class(name, body_lines, classes, lay):
    """Simulate one body and derive the padding needed at each pointer size.

    Returns {statement index: (pad64, pad32)}, where the key len(kinds) is the
    trailing padding.  Every 64-bit value produced here must reproduce the
    constant already present in the generated source, otherwise the layout
    model is wrong for this class and Unsupported is raised.
    """
    stmts, kinds, orig = split_body(name, body_lines, classes)

    size8, al8 = lay.calc(name, 8)
    size4, al4 = lay.calc(name, 4)
    if size8 != classes[name]['size']:
        raise Unsupported(f'class size {size8} != recorded {classes[name]["size"]}')

    # A multiply-inherited class carries one extra vtable pointer per additional
    # polymorphic base; the generated body skips it with a plain Position +=.
    pending8 = lay.extra.get(name, 0) * 8
    pending4 = lay.extra.get(name, 0) * 4
    base_idx = 1 if kinds and kinds[0][0] == 'base' else 0

    pos8 = pos4 = 0
    plan = {}
    for i, k in enumerate(kinds + [None]):
        # The extra vtable pointers sit immediately after the base class, and
        # the generated body skips them as part of the padding at that point.
        extra8, extra4 = (pending8, pending4) if i == base_idx else (0, 0)
        if i == base_idx:
            pending8 = pending4 = 0
        at8, at4 = pos8 + extra8, pos4 + extra4

        if k is None:
            p8, p4 = size8 - at8, size4 - at4
        else:
            _, a8 = consume(k, 8, lay)
            _, a4 = consume(k, 4, lay)
            if k[-1] == 'align16':
                a8 = a4 = 16
            p8, p4 = roundup(at8, a8) - at8, roundup(at4, a4) - at4
            # ALIGN_16 members align absolutely, at either pointer size.
            if i in orig and orig[i] != extra8 + p8:
                p8, p4 = roundup(at8, 16) - at8, roundup(at4, 16) - at4

        p8 += extra8
        p4 += extra4
        recorded = orig.get(i, 0)
        if p8 != recorded:
            raise Unsupported(
                f'pad before stmt {i}: recorded {recorded}, modelled {p8} '
                f'(pos8={pos8}, kind={k})')
        if p8 or p4:
            plan[i] = (p8, p4)
        pos8 += p8
        pos4 += p4
        if k is not None:
            pos8 += consume(k, 8, lay)[0]
            pos4 += consume(k, 4, lay)[0]

    if pos8 != size8:
        raise Unsupported(f'end pos8 {pos8} != recorded size {size8}')
    if pos4 != size4:
        raise Unsupported(f'end pos4 {pos4} != computed size {size4}')
    if set(orig) - set(plan):
        raise Unsupported(f'unconsumed source padding at {sorted(set(orig) - set(plan))}')
    return plan


def rebuild_body(name, body_lines, classes, plan, var, ser):
    """Re-emit a body with the planned padding, validating the source padding."""
    stmts, kinds, orig = split_body(name, body_lines, classes, simple=True)
    for i, recorded in orig.items():
        if plan.get(i, (0, 0))[0] != recorded:
            raise Unsupported(f'{var} body padding disagrees with Read body at {i}')

    names = ulong_members(name, classes)
    out, idx = [], 0
    indent = '            '
    for what, line in stmts + [('end', None)]:
        if what != 'raw':
            pad = plan.get(idx)
            if pad:
                p8, p4 = pad
                expr = str(p8) if p8 == p4 else f'{ser}.Padding({p8}, {p4})'
                out.append(f'{indent}{var}.Position += {expr};')
            idx += 1
        if what == 'end':
            break
        out.append(apply_usize(line, names) if names else line)
    return '\n'.join(out)


BODY_RE = {
    'Read': re.compile(
        r'(public (?:override|virtual) void Read\(PackFileDeserializer des, '
        r'BinaryReaderEx br\)\s*\n\s*\{\n)(.*?)(\n        \})', re.S),
    'Write': re.compile(
        r'(public (?:override|virtual) void Write\(PackFileSerializer s, '
        r'BinaryWriterEx bw\)\s*\n\s*\{\n)(.*?)(\n        \})', re.S),
}


def emit_se_only(names):
    """Write the list of classes whose layout is still 64-bit only."""
    out = os.path.join(HKX_ROOT, 'PointerSizeSupport.cs')
    body = '\n'.join(f'            "{n}",' for n in names)
    text = f'''using System.Collections.Generic;

namespace HKX2
{{
    /// <summary>
    /// Havok classes whose generated Read/Write bodies still carry padding
    /// hardcoded to Skyrim SE's 8-byte pointer, so they cannot be read from or
    /// written to a 32-bit (Skyrim LE) packfile.
    ///
    /// They fall into three groups: hkp* physics/ragdoll classes whose layout
    /// the generator cannot derive (multiple inheritance and vtable-only
    /// interfaces), Havok type-metadata and runtime-only classes that never
    /// appear in a serialised file, and hkbGeneratorSyncInfo, whose SE body has
    /// a pre-existing 8-byte over-read.
    ///
    /// None of them occur in behaviour, character, project, skeleton or
    /// animation files.  Generated by tools/hkx-layout-gen/relayout.py.
    /// </summary>
    public static class PointerSizeSupport
    {{
        public static readonly IReadOnlySet<string> SkyrimSeOnlyClasses = new HashSet<string>
        {{
{body}
        }};

        /// <summary>True if <paramref name="className"/> can be read from or
        /// written to a packfile with the given pointer size.</summary>
        public static bool Supports(string className, int pointerSize)
            => pointerSize == 8 || !SkyrimSeOnlyClasses.Contains(className);
    }}
}}
'''
    open(out, 'w', encoding='utf-8-sig', newline='\r\n').write(text)
    print(f'wrote {out} ({len(names)} SE-only classes)')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--check', action='store_true',
                    help='validate only, do not rewrite')
    args = ap.parse_args()

    classes = parse_classes(HKX_ROOT)
    lay = Layout(classes)
    lay.derive_extra_vtables()

    ok = skipped = rewritten = 0
    failures = []
    changed_pads = 0

    for name, c in sorted(classes.items()):
        path = c['path']
        if os.path.basename(os.path.dirname(path)) != 'Autogen':
            continue
        if name in UNSUPPORTED:
            skipped += 1
            continue

        with open(path, 'rb') as fh:
            data = fh.read()
        bom = data.startswith(b'\xef\xbb\xbf')
        raw = data.decode('utf-8-sig')
        crlf = '\r\n' in raw
        txt = raw.replace('\r\n', '\n')
        mread = BODY_RE['Read'].search(txt)
        if not mread:
            skipped += 1
            continue

        mwrite = BODY_RE['Write'].search(txt)
        if not mwrite:
            skipped += 1
            continue

        try:
            plan = plan_class(name, mread.group(2).splitlines(), classes, lay)
            bodies = {
                'Read': rebuild_body(name, mread.group(2).splitlines(),
                                     classes, plan, 'br', 'des'),
                'Write': rebuild_body(name, mwrite.group(2).splitlines(),
                                      classes, plan, 'bw', 's'),
            }
        except Unsupported as e:
            failures.append((name, str(e)))
            continue
        except Exception as e:                       # noqa: BLE001
            failures.append((name, f'{type(e).__name__}: {e}'))
            continue

        ok += 1
        differing = sum(1 for p8, p4 in plan.values() if p8 != p4)
        if not differing and not ulong_members(name, classes):
            continue                                  # layout identical, no diff

        changed_pads += differing
        rewritten += 1
        if args.check:
            continue

        new_txt = txt
        for kind in ('Write', 'Read'):                # later offsets first
            m = BODY_RE[kind].search(new_txt)
            new_txt = new_txt[:m.start(2)] + bodies[kind] + new_txt[m.end(2):]
        if crlf:
            new_txt = new_txt.replace('\n', '\r\n')
        open(path, 'w', encoding='utf-8-sig' if bom else 'utf-8',
             newline='').write(new_txt)

    if not args.check:
        emit_se_only(sorted([n for n, _ in failures] + sorted(UNSUPPORTED)))

    print(f'modelled OK      : {ok}')
    print(f'skipped          : {skipped}')
    print(f'{"would rewrite" if args.check else "rewritten":17}: {rewritten}')
    print(f'padding sites {"differing" if args.check else "changed"}: {changed_pads}')
    print(f'failures         : {len(failures)}')
    for n, e in failures:
        print(f'   {n}: {e}')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())

// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Svg.Expressions;
using Svg.Transforms;

namespace Svg.SourceEditing;

/// <summary>What a command editing a parsed copy of a drawing is handed.</summary>
public sealed class SvgDomEditContext
{
    private readonly Dictionary<SvgElement, SvgElement> _replaced = new(ReferenceEqualityComparer.Instance);

    internal SvgDomEditContext(SvgDocument document) => Document = document;

    /// <summary>The copy, read from the file's own text, which is all a command may change.</summary>
    public SvgDocument Document { get; }

    /// <summary>The element of the copy that an address key of the file names, or null.</summary>
    public SvgElement? Element(string key) => SvgElementAddress.Parse(key)?.Resolve(Document);

    /// <summary>Puts <paramref name="replacement"/> where <paramref name="original"/> is, as the same element of the file.</summary>
    /// <remarks>
    /// Written as the original's tag renamed, so its place, its comments and the order of what it is
    /// written with all stay; only what differs is written into it. A replacement put in any other
    /// way is a new element written fresh, and the original is taken out with whatever was around it.
    /// </remarks>
    public void Replace(SvgElement original, SvgElement replacement)
    {
        if (original?.Parent is not { } parent)
        {
            throw new ArgumentException("The drawing itself cannot be replaced.", nameof(original));
        }

        if (replacement is null || replacement.Parent is { })
        {
            throw new ArgumentException("A replacement has to be an element that is not in the drawing yet.", nameof(replacement));
        }

        var index = parent.Children.IndexOf(original);

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, replacement);

        _replaced[replacement] = _replaced.TryGetValue(original, out var first) ? first : original;
    }

    /// <summary>Each replacement, and the element of the file it continues.</summary>
    internal IReadOnlyDictionary<SvgElement, SvgElement> Replaced => _replaced;
}

/// <summary>
/// Runs a command on a parsed copy of a drawing and writes what it changed back into the file's
/// text, the rest of which is left as it was.
/// </summary>
/// <remarks>
/// The command runs twice, on two copies whose lifted expressions stand in as different values. A
/// value that comes out different was worked out from a stand-in: it is written as the expression
/// where it is one copied whole, and refused otherwise, since the number it would freeze into the file
/// is not what the drawing shows. What the command reads through anything else — a visibility, a
/// typeface, the bound scene a host hands it — it has to judge for itself.
///
/// What comes back is checked by reading the edited file again and comparing it with what the
/// command made, so an edit the file cannot say — a value a sheet overrides, a sum the writer spells
/// differently — is refused instead of written wrong. Belongs inside a commit, which puts the
/// document back on a refusal.
/// </remarks>
public static class SvgDomEdit
{
    private static readonly string s_lifted = SvgExpressionAttributes.Namespace + ":";

    private const string From = ":from";

    /// <summary>Runs <paramref name="command"/> on a copy of <paramref name="source"/> and writes what it changed.</summary>
    /// <param name="command">Edits the copy it is handed, and answers null or the sentence refusing it. It runs twice and has to do the same thing both times.</param>
    /// <param name="landed">The file keys of what was written, moved or put in, as <see cref="SvgSourceEdits.Apply(SvgSourceDocument, Func{string, string?}, out IReadOnlyList{string})"/> gives them.</param>
    /// <param name="note">What a value written now shadows, or null.</param>
    /// <returns>The sentence refusing the edit, or null where it was made or changed nothing.</returns>
    public static string? Apply(SvgSourceDocument source, Func<SvgDomEditContext, string?> command, out IReadOnlyList<string> landed, out string? note)
    {
        landed = Array.Empty<string>();
        note = null;

        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (source.Document.Root is not { } file)
        {
            return Misaligned;
        }

        var text = source.ToText();

        if (Run.Of(text, file, 0, out var refusal) is not { } first)
        {
            return refusal;
        }

        if (first.Execute(command) is { } refused)
        {
            return refused;
        }

        if (Run.Of(text, file, 1, out refusal) is not { } second)
        {
            return refusal;
        }

        if (second.Execute(command) is { } || !Steady(first, second))
        {
            return Unsteady;
        }

        var diff = new Diff(source, first, second);

        if (diff.Plan() is { } cannot)
        {
            return cannot;
        }

        if (diff.Steps.Count == 0)
        {
            return null;
        }

        if (new SvgSourceEdits(string.Empty, diff.Steps).Apply(source, key => key, out var written) is { } unwritten)
        {
            return unwritten;
        }

        if (diff.Verify(source.ToText()) is { } mismatch)
        {
            return $"What the command made would not read back from the file: {mismatch}.";
        }

        landed = written;
        note = diff.Notes.Count > 0 ? string.Join(" ", diff.Notes.Distinct()) : null;

        return null;
    }

    private const string Misaligned = "This drawing's file and what it reads as do not line up element for element, so a command cannot write into it.";

    private const string Unsteady = "The command did not do the same thing on two readings of the drawing, so what it would write cannot be trusted.";

    /// <summary>Whether the two runs made the same elements of the same ones.</summary>
    private static bool Steady(Run first, Run second)
    {
        if (first.After.Count != second.After.Count)
        {
            return false;
        }

        for (var i = 0; i < first.After.Count; i++)
        {
            var (one, other) = (first.After[i].Element, second.After[i].Element);

            if (SvgElementNames.NameOf(one) != SvgElementNames.NameOf(other)
                || one.Children.Count != other.Children.Count
                || first.Origin(one) != second.Origin(other))
            {
                return false;
            }
        }

        return true;
    }

    private static SvgDocument Load(string text, int run)
    {
        var document = SvgDocumentCompatibilityLoader.FromSvg<SvgDocument>(text);

        SvgExpressionSentinels.Mark(document, run);

        return document;
    }

    /// <summary>One reading of the file, and what the command did to it.</summary>
    private sealed class Run
    {
        private readonly Dictionary<SvgElement, int> _index = new(ReferenceEqualityComparer.Instance);

        private Run(SvgDocument document)
        {
            Context = new SvgDomEditContext(document);
            Before = SvgElementState.Capture(document);

            for (var i = 0; i < Before.Count; i++)
            {
                _index[Before[i].Element] = i;
            }
        }

        public SvgDomEditContext Context { get; }

        public IReadOnlyList<SvgElementState> Before { get; }

        public IReadOnlyList<SvgElementState> After { get; private set; } = Array.Empty<SvgElementState>();

        public static Run? Of(string text, XElement file, int run, out string? refusal)
        {
            SvgDocument document;

            try
            {
                document = Load(text, run);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                refusal = $"This drawing does not read as SVG, so a command cannot edit it: {failure.Message}";

                return null;
            }

            // The keys a plan names are the file's, and here they are the copy's too.
            refusal = Aligned(file, document) ? null : Misaligned;

            return refusal is null ? new Run(document) : null;
        }

        public string? Execute(Func<SvgDomEditContext, string?> command)
        {
            var refusal = command(Context);

            After = SvgElementState.Capture(Context.Document);

            return refusal;
        }

        /// <summary>Where in <see cref="Before"/> the element of the file an element continues is, or null for a new one.</summary>
        public int? Origin(SvgElement element)
            => _index.TryGetValue(element, out var at) ? at
                : Context.Replaced.TryGetValue(element, out var original) && _index.TryGetValue(original, out var was) ? was
                : null;

        private static bool Aligned(XElement file, SvgElement element)
        {
            var name = SvgElementNames.NameOf(element);
            var children = file.Elements().ToList();

            if (!string.Equals(file.Name.LocalName, name.Substring(name.IndexOf(':') + 1), StringComparison.Ordinal)
                || children.Count != element.Children.Count)
            {
                return false;
            }

            for (var i = 0; i < children.Count; i++)
            {
                if (!Aligned(children[i], element.Children[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A lifted value of the file, as each run stood it in.</summary>
    private readonly record struct Binding(string Name, string Expression, object First, object Second);

    /// <summary>The steps that turn the file into what the first run made, and what to compare it with afterwards.</summary>
    private sealed class Diff
    {
        private readonly SvgSourceDocument _source;
        private readonly Run _first;
        private readonly Run _second;
        private readonly Dictionary<SvgElement, int> _at = new(ReferenceEqualityComparer.Instance);
        private readonly List<Binding> _bindings = new();
        private readonly HashSet<(SvgElement, string)> _carried = new();
        private readonly HashSet<SvgElement> _fresh = new(ReferenceEqualityComparer.Instance);
        private int _handles;

        public Diff(SvgSourceDocument source, Run first, Run second)
        {
            (_source, _first, _second) = (source, first, second);

            for (var i = 0; i < first.After.Count; i++)
            {
                _at[first.After[i].Element] = i;
            }

            for (var i = 0; i < first.Before.Count; i++)
            {
                var (one, other) = (first.Before[i], second.Before[i]);

                foreach (var lifted in one.CustomAttributes)
                {
                    if (Liftable(lifted.Key) is not { } name)
                    {
                        continue;
                    }

                    var (a, b) = name == SvgExpressionAttributes.ContentName
                        ? ((object?)one.Content, (object?)other.Content)
                        : (Own(one, name), Own(other, name));

                    // A stand-in the two runs share, a visibility say, says nothing about where a value came from.
                    if (a is { } && b is { } && !SvgValueSpelling.Precise(a, b))
                    {
                        _bindings.Add(new Binding(name, lifted.Value, a, b));
                    }
                }
            }
        }

        public List<SvgSourceEdit> Steps { get; } = new();

        public List<string> Notes { get; } = new();

        public string? Plan()
        {
            var kept = new HashSet<SvgElement>(_first.After.Select(state => state.Element), ReferenceEqualityComparer.Instance);
            var continued = new HashSet<SvgElement>(_first.Context.Replaced.Values, ReferenceEqualityComparer.Instance);

            if (continued.FirstOrDefault(kept.Contains) is { } twice)
            {
                return $"A <{SvgElementNames.NameOf(twice)}> the command replaced is still in the drawing.";
            }

            foreach (var now in _first.After)
            {
                if (_first.Origin(now.Element) is { } origin && Kept(now, _first.Before[origin]) is { } refusal)
                {
                    return refusal;
                }
            }

            // Inside one already taken out, a removal is already done, and the plan passes over it.
            foreach (var was in _first.Before)
            {
                if (!kept.Contains(was.Element) && !continued.Contains(was.Element))
                {
                    Steps.Add(new SvgSourceEdit.Remove(was.Key));
                }
            }

            return null;
        }

        /// <summary>The steps for an element of the file the command kept, or replaced.</summary>
        private string? Kept(SvgElementState now, SvgElementState was)
        {
            var element = now.Element;
            var twin = _second.After[_at[element]];
            var tag = SvgElementNames.NameOf(element);

            if (SvgAttributeEditor.Resolve(_source.Document, was.Key) is not { } file)
            {
                return SvgElementEditor.Missing(was.Key);
            }

            if (tag != SvgElementNames.NameOf(was.Element))
            {
                Steps.Add(new SvgSourceEdit.Retag(was.Key, tag));
            }

            return Attributes(now, twin, was, file, tag)
                   ?? Custom(now, was, file, tag)
                   ?? Content(now, twin, was, tag)
                   ?? (Same(now.Styles, was.Styles) ? null : $"<{tag}> holds a declaration the command changed but no attribute shows, which cannot be written.")
                   ?? Children(now, was);
        }

        private string? Attributes(SvgElementState now, SvgElementState twin, SvgElementState was, XElement file, string tag)
        {
            var element = now.Element;
            var writes = new List<(string Name, string? Value)>();

            foreach (var name in was.Attributes.Keys.Concat(now.Attributes.Keys).Distinct())
            {
                var (before, after) = (Own(was, name), Own(now, name));

                if (SvgValueSpelling.Precise(before, after))
                {
                    continue;
                }

                // A replacement takes away what only the element it replaces is read with.
                if ((Named(element, name, file) ?? Named(was.Element, name, file)) is not { } attribute)
                {
                    return $"'{name}' on <{tag}> is held by no attribute the element is read with.";
                }

                was.CustomAttributes.TryGetValue(SvgExpressionAttributes.KeyFor(name), out var expression);

                string? text;

                if (after is null)
                {
                    if (expression is { })
                    {
                        return Driven(name, tag, expression);
                    }

                    text = null;
                }
                else if (name == SvgTransformExpression.Name
                         && before is SvgTransformCollection prior
                         && (expression ?? Written(file, name)) is { } authored
                         && Compose(element, prior, (SvgTransformCollection)after, Own(twin, name) as SvgTransformCollection, authored) is { } composed)
                {
                    // Terms put around what was there are written around the text it was written as:
                    // an expression's stand-ins differ between the runs, and a literal keeps its
                    // spelling while the whole still reads as what the command made.
                    if (expression is { })
                    {
                        text = composed;
                        element.CustomAttributes[SvgExpressionAttributes.KeyFor(name)] = composed;
                    }
                    else if (SvgValueSpelling.Spell(element, name, after, composed, out var why) is { } spelt)
                    {
                        text = spelt;
                    }
                    else
                    {
                        return why;
                    }
                }
                else if (expression is { } && name == SvgTransformExpression.Name)
                {
                    return Driven(name, tag, expression);
                }
                else if (!SvgValueSpelling.Precise(after, Own(twin, name)))
                {
                    // Another expression's value copied whole, which may replace the one that was here.
                    if (Carry(element, name, after, Own(twin, name)) is not { } carried)
                    {
                        return Tainted(name, tag, _bindings);
                    }

                    text = carried;
                }
                else if (expression is { })
                {
                    return Driven(name, tag, expression);
                }
                else if (SvgValueSpelling.Spell(element, name, after, null, out var why) is { } spelt)
                {
                    text = spelt;

                    if (Shadowed(element, name, tag) is { } shadow)
                    {
                        Notes.Add(shadow);
                    }
                }
                else
                {
                    return why;
                }

                writes.Add((attribute, text));
            }

            if (writes.Count > 0)
            {
                Steps.Add(new SvgSourceEdit.SetAttributes(was.Key, writes));
            }

            return null;
        }

        /// <summary>What the command did to what the element is written with that it holds no value for.</summary>
        private string? Custom(SvgElementState now, SvgElementState was, XElement file, string tag)
        {
            var writes = new List<(string Name, string? Value)>();

            foreach (var key in was.CustomAttributes.Keys.Concat(now.CustomAttributes.Keys).Distinct())
            {
                now.CustomAttributes.TryGetValue(key, out var after);
                was.CustomAttributes.TryGetValue(key, out var before);

                if (after == before || Ignored(key, now.Element))
                {
                    continue;
                }

                string? name;

                if (key.StartsWith(s_lifted, StringComparison.Ordinal))
                {
                    var local = key.Substring(s_lifted.Length);

                    // Where a value was written follows the value.
                    if (local.EndsWith(From, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Liftable(key) is { } lifted)
                    {
                        // A copy of another element's expression that the carry found too.
                        if (_carried.Contains((now.Element, lifted)) && now.Element.CustomAttributes.TryGetValue(key, out var carried) && carried == after)
                        {
                            continue;
                        }

                        return $"The command changed which expression sets '{local}' on <{tag}>.";
                    }

                    // e:bounds, e:draws and the rest of what the extension writes as it is.
                    name = SvgExpressionDeclarations.NamespacePrefixFor(file.Document!.Root!, out _) + ":" + local;
                }
                else if (SvgStyleAttributeNames.IsStyleOnly(key))
                {
                    if (SvgAttributeEditor.Styled(file, key) is null)
                    {
                        return $"'{key}' on <{tag}> would have to be added to its style, which a command does not write yet.";
                    }

                    name = key;
                }
                else if (key.LastIndexOf(':') is var colon and > 0)
                {
                    if (Prefixed(file, key.Substring(0, colon), key.Substring(colon + 1)) is not { } spelt)
                    {
                        return $"'{key}' on <{tag}> is in a namespace this drawing gives no prefix to.";
                    }

                    name = spelt;
                }
                else
                {
                    name = key;
                }

                writes.Add((name, after));
            }

            if (writes.Count > 0)
            {
                Steps.Add(new SvgSourceEdit.SetAttributes(was.Key, writes));
            }

            return null;
        }

        private string? Content(SvgElementState now, SvgElementState twin, SvgElementState was, string tag)
        {
            if (now.Content == was.Content)
            {
                return null;
            }

            if (was.CustomAttributes.TryGetValue(SvgExpressionAttributes.KeyFor(SvgExpressionAttributes.ContentName), out var expression))
            {
                return $"The text of <{tag}> is set by {{{{ {expression} }}}}, which a command cannot change.";
            }

            var text = now.Content;

            if (now.Content != twin.Content)
            {
                if (now.Content is null || Carry(now.Element, SvgExpressionAttributes.ContentName, now.Content, twin.Content) is not { } carried)
                {
                    return Tainted("text", tag, _bindings);
                }

                text = carried;
            }

            Steps.Add(new SvgSourceEdit.SetContent(was.Key, text));

            return null;
        }

        /// <summary>Moves and inserts that put the element's children in the order the command left them.</summary>
        private string? Children(SvgElementState now, SvgElementState was)
        {
            var children = now.Element.Children.ToList();
            var placed = new (string Key, bool Stays)?[children.Count];
            var here = new List<(int Child, int Was)>();
            var positions = new Dictionary<SvgElement, int>(ReferenceEqualityComparer.Instance);

            for (var i = 0; i < was.Children.Count; i++)
            {
                positions[was.Children[i]] = i;
            }

            for (var i = 0; i < children.Count; i++)
            {
                if (_first.Origin(children[i]) is { } origin)
                {
                    placed[i] = (_first.Before[origin].Key, false);

                    if (positions.TryGetValue(_first.Before[origin].Element, out var position))
                    {
                        here.Add((i, position));
                    }
                }
            }

            // The most children that can stay put: the longest run already in order. Everything else moves.
            foreach (var stays in Increasing(here))
            {
                placed[stays] = (placed[stays]!.Value.Key, true);
            }

            string? anchor = null;

            for (var i = 0; i < children.Count; i++)
            {
                if (placed[i] is { Stays: true } stable)
                {
                    anchor = stable.Key;

                    continue;
                }

                var next = placed.Skip(i + 1).FirstOrDefault(each => each is { Stays: true });
                var (target, where) = anchor is { } ? (anchor, SvgElementDrop.After)
                    : next is { } after ? (after.Key, SvgElementDrop.Before)
                    : (was.Key, SvgElementDrop.Inside);

                if (placed[i] is { } moved)
                {
                    Steps.Add(new SvgSourceEdit.Move(moved.Key, target, where));
                    anchor = moved.Key;

                    continue;
                }

                if (Fresh(children[i], out var refusal) is not { } written)
                {
                    return refusal;
                }

                anchor = "+" + (_handles++).ToString(CultureInfo.InvariantCulture);
                Steps.Add(new SvgSourceEdit.Insert(target, where, written, anchor));
            }

            return null;
        }

        /// <summary>An element the command made, and everything in it, as markup.</summary>
        private XElement? Fresh(SvgElement element, out string? refusal)
        {
            refusal = null;

            foreach (var each in element.Descendants().Prepend(element).ToList())
            {
                var tag = SvgElementNames.NameOf(each);

                if (_first.Origin(each) is { })
                {
                    refusal = $"A <{tag}> already in the drawing was put inside one the command made, which is not written yet.";

                    return null;
                }

                _fresh.Add(each);

                var (now, twin) = (_first.After[_at[each]], _second.After[_at[each]]);

                foreach (var name in now.Attributes.Keys.Concat(twin.Attributes.Keys).Distinct())
                {
                    if (SvgExpressionAttributes.Lifted(each.CustomAttributes, name) is { } || SvgValueSpelling.Precise(Own(now, name), Own(twin, name)))
                    {
                        continue;
                    }

                    if (Own(now, name) is not { } after || Carry(each, name, after, Own(twin, name)) is null)
                    {
                        refusal = Tainted(name, tag, _bindings);

                        return null;
                    }
                }

                if (now.Content != twin.Content && (now.Content is null || Carry(each, SvgExpressionAttributes.ContentName, now.Content, twin.Content) is null))
                {
                    refusal = Tainted("text", tag, _bindings);

                    return null;
                }
            }

            return SvgFreshElementWriter.Write(element, _source.IndentUnit, out refusal);
        }

        /// <summary>
        /// The expression a value copied whole from a lifted one stands for, as it is written, put on
        /// <paramref name="element"/> under <paramref name="name"/>; null where it is not one copy of one.
        /// </summary>
        private string? Carry(SvgElement element, string name, object first, object? second)
        {
            var found = _bindings
                .Where(binding => Carries(binding.Name, name)
                                  && SvgValueSpelling.Precise(binding.First, first)
                                  && SvgValueSpelling.Precise(binding.Second, second))
                .Select(binding => binding.Expression)
                .Distinct()
                .ToList();

            if (found.Count != 1)
            {
                return null;
            }

            element.CustomAttributes[SvgExpressionAttributes.KeyFor(name)] = found[0];
            element.CustomAttributes[SvgExpressionAttributes.SourceKeyFor(name)] = SvgElement.StyleSpecificity_PresAttribute.ToString(CultureInfo.InvariantCulture);
            _carried.Add((element, name));

            return SvgExpressionAttributes.IsInArguments(name) ? found[0] : "{{ " + found[0] + " }}";
        }

        /// <summary>Whether what an expression in <paramref name="from"/> comes to can be written in <paramref name="to"/>.</summary>
        /// <remarks>Geometry is lifted only on a clip's shapes, and an argument list only where it was written.</remarks>
        private static bool Carries(string from, string to)
            => from == to
               || (!SvgExpressionAttributes.IsInArguments(from)
                   && !SvgExpressionAttributes.IsInArguments(to)
                   && SvgExpressionAttributes.IsSupported(to, elementName: null)
                   && SvgExpressionAttributes.TypeFor(from) == SvgExpressionAttributes.TypeFor(to));

        /// <summary>
        /// The driven transform written with what the command put in front of it and after it, or
        /// null where it changed the transform some other way.
        /// </summary>
        private static string? Compose(SvgElement element, SvgTransformCollection was, SvgTransformCollection now, SvgTransformCollection? twin, string authored)
        {
            if (twin is null || twin.Count != now.Count)
            {
                return null;
            }

            for (var start = 0; start + was.Count <= now.Count; start++)
            {
                if (!Enumerable.Range(0, was.Count).All(i => SvgValueSpelling.Precise(One(now[start + i]), One(was[i]))))
                {
                    continue;
                }

                var outside = Enumerable.Range(0, now.Count).Where(i => i < start || i >= start + was.Count).ToList();

                // Worked out from the stand-ins it surrounds, which the file does not hold.
                if (outside.Any(i => !SvgValueSpelling.Precise(One(now[i]), One(twin[i]))))
                {
                    return null;
                }

                var parts = new List<string>();

                foreach (var (from, to) in new[] { (0, start), (start + was.Count, now.Count) })
                {
                    if (to > from)
                    {
                        var terms = new SvgTransformCollection();

                        terms.AddRange(now.Skip(from).Take(to - from).Select(term => (SvgTransform)term.Clone()));

                        if (SvgValueSpelling.Spell(element, SvgTransformExpression.Name, terms, null, out _) is not { } spelt)
                        {
                            return null;
                        }

                        parts.Add(spelt);
                    }

                    if (from == 0)
                    {
                        parts.Add(authored.Trim());
                    }
                }

                return string.Join(" ", parts);
            }

            return null;
        }

        private string? Shadowed(SvgElement element, string name, string tag)
        {
            if (!SvgExpressionAttributes.IsInherited(name))
            {
                return null;
            }

            for (var at = element.Parent; at is { }; at = at.Parent)
            {
                if (SvgExpressionAttributes.Lifted(at.CustomAttributes, name) is { } expression)
                {
                    return $"The {name} written on <{tag}> now shadows {{{{ {expression} }}}} from <{SvgElementNames.NameOf(at)}>.";
                }
            }

            return null;
        }

        /// <summary>Reads the edited file again and says how it differs from what the command made, or null.</summary>
        public string? Verify(string text)
        {
            var expected = SvgElementState.Capture(_first.Context.Document);
            IReadOnlyList<SvgElementState> actual;

            try
            {
                actual = SvgElementState.Capture(Load(text, 0));
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                return $"it would not read as SVG: {failure.Message}";
            }

            if (expected.Count != actual.Count)
            {
                return $"{expected.Count} elements would come back as {actual.Count}";
            }

            // Under a sheet, an element the command made is matched by its rules once it is in the file.
            var sheet = expected.Any(state => SvgElementNames.NameOf(state.Element) == "style");

            for (var i = 0; i < expected.Count; i++)
            {
                var (made, read) = (expected[i], actual[i]);
                var tag = SvgElementNames.NameOf(made.Element);

                if (tag != SvgElementNames.NameOf(read.Element) || made.Children.Count != read.Children.Count)
                {
                    return $"a <{tag}> would come back as a <{SvgElementNames.NameOf(read.Element)}> with {read.Children.Count} children";
                }

                foreach (var name in made.Attributes.Keys.Concat(read.Attributes.Keys).Distinct())
                {
                    if (!SvgValueSpelling.Precise(Own(made, name), Own(read, name))
                        && !(sheet && Own(made, name) is null && _fresh.Contains(made.Element))
                        && !(name == "id" && Deduplicated(Own(made, name)) == Deduplicated(Own(read, name))))
                    {
                        return $"'{name}' on <{tag}> would read as something else";
                    }
                }

                foreach (var key in made.CustomAttributes.Keys.Concat(read.CustomAttributes.Keys).Distinct())
                {
                    if (Ignored(key, made.Element) || Unplaced(key, made, read))
                    {
                        continue;
                    }

                    if (!made.CustomAttributes.TryGetValue(key, out var was) | !read.CustomAttributes.TryGetValue(key, out var now) || was != now)
                    {
                        return $"<{tag}> would be written with '{key}' as '{now}' rather than '{was}'";
                    }
                }

                if (made.Content != read.Content)
                {
                    return $"the text of <{tag}> would read as something else";
                }

                if (!Same(made.Styles, read.Styles))
                {
                    return $"<{tag}> would hold other declarations";
                }
            }

            return null;
        }

        /// <summary>Whether a key says only where a value was written: a literal's, or one this carried, which the writer placed.</summary>
        private bool Unplaced(string key, SvgElementState made, SvgElementState read)
        {
            if (!key.EndsWith(From, StringComparison.Ordinal) || !key.StartsWith(s_lifted, StringComparison.Ordinal))
            {
                return false;
            }

            var lifted = key.Substring(0, key.Length - From.Length);
            var name = lifted.Substring(s_lifted.Length);

            return _carried.Contains((made.Element, name)) || (!made.CustomAttributes.ContainsKey(lifted) && !read.CustomAttributes.ContainsKey(lifted));
        }

        private static IEnumerable<int> Increasing(List<(int Child, int Was)> here)
        {
            // Patience sorting: tails[k] ends the best run of length k + 1 found so far.
            var tails = new List<int>();
            var before = new int[here.Count];

            for (var i = 0; i < here.Count; i++)
            {
                var (low, high) = (0, tails.Count);

                while (low < high)
                {
                    var middle = (low + high) / 2;

                    (low, high) = here[tails[middle]].Was < here[i].Was ? (middle + 1, high) : (low, middle);
                }

                before[i] = low > 0 ? tails[low - 1] : -1;

                if (low == tails.Count)
                {
                    tails.Add(i);
                }
                else
                {
                    tails[low] = i;
                }
            }

            for (var at = tails.Count > 0 ? tails[tails.Count - 1] : -1; at >= 0; at = before[at])
            {
                yield return here[at].Child;
            }
        }
    }

    /// <summary>The name a key lifts, or null where it lifts none.</summary>
    private static string? Liftable(string key)
        => key.StartsWith(s_lifted, StringComparison.Ordinal)
           && key.Substring(s_lifted.Length) is var name
           && (name == SvgExpressionAttributes.ContentName || SvgExpressionAttributes.IsSupported(name))
            ? name
            : null;

    /// <summary>What the parser keeps for itself rather than reads from what is written.</summary>
    private static bool Ignored(string key, SvgElement element)
        => key == "style"
           || (key == "tagName" && element is SvgUnknownElement)
           || (key.LastIndexOf(':') is var colon and > 0 && SvgBookkeepingFilterWriter.IsBookkeeping(key.Substring(0, colon)));

    /// <summary>A value of the element's own, with a paint left unset as no value at all.</summary>
    private static object? Own(SvgElementState state, string name)
        => state.Attributes.TryGetValue(name, out var value) && !ReferenceEquals(value, SvgPaintServer.NotSet) ? value : null;

    /// <summary>The attribute <paramref name="name"/> is written as on <paramref name="file"/>, or null where none is.</summary>
    private static string? Named(SvgElement element, string name, XElement file)
    {
        if (SvgElementFactory.FindProperty(element, name) is not { } property)
        {
            return null;
        }

        return property.AttributeNamespace switch
        {
            SvgNamespaces.XLinkNamespace when file.Attribute(XNamespace.Get(SvgNamespaces.XLinkNamespace) + name) is { }
                => Prefixed(file, SvgNamespaces.XLinkNamespace, name) ?? name,
            SvgNamespaces.XmlNamespace => "xml:" + name,
            _ => name
        };
    }

    /// <summary>The attribute <paramref name="name"/> as <paramref name="file"/> writes it, or null.</summary>
    /// <remarks>Not a style declaration's: a CSS transform is spelt otherwise, and writing one is refused anyway.</remarks>
    private static string? Written(XElement file, string name) => (string?)file.Attribute(name);

    /// <summary>A name in a namespace as <paramref name="file"/> spells it, or null where nothing above it declares one.</summary>
    private static string? Prefixed(XElement file, string ns, string local)
        => file.GetPrefixOfNamespace(XNamespace.Get(ns)) is { Length: > 0 } prefix ? prefix + ":" + local : null;

    private static SvgTransformCollection One(SvgTransform transform) => new() { transform };

    /// <summary>An id as written, without the <c>#1</c> the parser gives the second element written with it.</summary>
    /// <remarks>Which of the two gets it is the order they are read in, which a move or a removal changes.</remarks>
    private static string? Deduplicated(object? id)
        => id is string written && Regex.Match(written, @"#\d+$") is { Success: true } suffix ? written.Substring(0, suffix.Index) : id as string;

    private static bool Same(
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<int, string>>> one,
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<int, string>>> other)
        => one.Count == other.Count && one.All(pair => other.TryGetValue(pair.Key, out var rules) && rules.SequenceEqual(pair.Value));

    private static string Driven(string name, string tag, string expression)
        => $"'{name}' on <{tag}> is set by {(SvgExpressionAttributes.IsInArguments(name) ? expression : "{{ " + expression + " }}")}, which a command cannot change.";

    /// <remarks>With no expression in the drawing, the two runs differ because the command did.</remarks>
    private static string Tainted(string name, string tag, List<Binding> bindings)
        => bindings.Count == 0
            ? Unsteady
            : $"'{name}' on <{tag}> would be worked out from what an expression stands in for while the drawing is edited, rather than from its value.";
}

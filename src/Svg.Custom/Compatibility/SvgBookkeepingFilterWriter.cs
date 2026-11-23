// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Xml;

namespace Svg;

/// <summary>
/// Passes everything through except the custom attributes the loader keeps for itself, which
/// SVG.NET's writer would otherwise emit as attributes nobody wrote.
/// </summary>
/// <remarks>
/// A writer rather than a change to <see cref="SvgCustomAttributeCollection"/>, because the same
/// entries have to stay visible to a clone and to the JavaScript DOM state; only the file must not
/// see them. SVG.NET's <c>WriteAttributes</c> splits a key at its last colon, so a provenance key
/// <c>{expressions}:fill:from</c> arrives as attribute <c>from</c> in namespace
/// <c>{expressions}:fill</c> - which is how it is recognised here, apart from an authored
/// <c>e:bounds</c> or <c>e:draws</c>, whose namespace is the expression namespace itself.
/// </remarks>
internal sealed class SvgBookkeepingFilterWriter : XmlWriter
{
    private static readonly string s_provenancePrefix = SvgExpressionAttributes.Namespace + ":";

    private static readonly string s_rawTextDecorationNamespace = SvgStyleAttributeNames.RawTextDecorationAttributeKey.Substring(
        0,
        SvgStyleAttributeNames.RawTextDecorationAttributeKey.LastIndexOf(':'));

    private readonly XmlWriter _inner;
    private bool _skipping;

    private SvgBookkeepingFilterWriter(XmlWriter inner)
    {
        _inner = inner;
    }

    /// <summary>The writer to hand SVG.NET, wrapping <paramref name="writer"/> once.</summary>
    public static XmlWriter Wrap(XmlWriter writer)
        => writer is SvgBookkeepingFilterWriter ? writer : new SvgBookkeepingFilterWriter(writer);

    private static bool IsBookkeeping(string? ns)
        => ns is { Length: > 0 }
           && (ns.StartsWith(s_provenancePrefix, StringComparison.Ordinal)
               || string.Equals(ns, s_rawTextDecorationNamespace, StringComparison.Ordinal));

    public override WriteState WriteState => _inner.WriteState;

    public override XmlWriterSettings? Settings => _inner.Settings;

    public override XmlSpace XmlSpace => _inner.XmlSpace;

    public override string? XmlLang => _inner.XmlLang;

    public override void WriteStartAttribute(string? prefix, string localName, string? ns)
    {
        if (IsBookkeeping(ns))
        {
            _skipping = true;
            return;
        }

        _inner.WriteStartAttribute(prefix, localName, ns);
    }

    public override void WriteEndAttribute()
    {
        if (_skipping)
        {
            _skipping = false;
            return;
        }

        _inner.WriteEndAttribute();
    }

    public override void WriteString(string? text)
    {
        if (!_skipping)
        {
            _inner.WriteString(text);
        }
    }

    public override void WriteChars(char[] buffer, int index, int count)
    {
        if (!_skipping)
        {
            _inner.WriteChars(buffer, index, count);
        }
    }

    public override void WriteRaw(char[] buffer, int index, int count)
    {
        if (!_skipping)
        {
            _inner.WriteRaw(buffer, index, count);
        }
    }

    public override void WriteRaw(string data)
    {
        if (!_skipping)
        {
            _inner.WriteRaw(data);
        }
    }

    public override void WriteEntityRef(string name)
    {
        if (!_skipping)
        {
            _inner.WriteEntityRef(name);
        }
    }

    public override void WriteCharEntity(char ch)
    {
        if (!_skipping)
        {
            _inner.WriteCharEntity(ch);
        }
    }

    public override void WriteSurrogateCharEntity(char lowChar, char highChar)
    {
        if (!_skipping)
        {
            _inner.WriteSurrogateCharEntity(lowChar, highChar);
        }
    }

    public override void WriteWhitespace(string? ws)
    {
        if (!_skipping)
        {
            _inner.WriteWhitespace(ws);
        }
    }

    public override void WriteBase64(byte[] buffer, int index, int count)
    {
        if (!_skipping)
        {
            _inner.WriteBase64(buffer, index, count);
        }
    }

    public override void Flush() => _inner.Flush();

    public override string? LookupPrefix(string ns) => _inner.LookupPrefix(ns);

    public override void WriteCData(string? text) => _inner.WriteCData(text);

    public override void WriteComment(string? text) => _inner.WriteComment(text);

    public override void WriteDocType(string name, string? pubid, string? sysid, string? subset)
        => _inner.WriteDocType(name, pubid, sysid, subset);

    public override void WriteEndDocument() => _inner.WriteEndDocument();

    public override void WriteEndElement() => _inner.WriteEndElement();

    public override void WriteFullEndElement() => _inner.WriteFullEndElement();

    public override void WriteProcessingInstruction(string name, string? text) => _inner.WriteProcessingInstruction(name, text);

    public override void WriteStartDocument() => _inner.WriteStartDocument();

    public override void WriteStartDocument(bool standalone) => _inner.WriteStartDocument(standalone);

    public override void WriteStartElement(string? prefix, string localName, string? ns) => _inner.WriteStartElement(prefix, localName, ns);
}

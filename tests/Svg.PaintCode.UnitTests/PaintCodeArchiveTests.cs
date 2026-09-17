// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Svg.PaintCode.UnitTests;

public class PaintCodeArchiveTests
{
    [Fact]
    public void An_Integer_Comes_Back_As_A_Long()
    {
        var plist = new Bplist();

        Assert.Equal(42L, PaintCodeArchive.Read(plist.ToBytes(plist.Integer(42))));
    }

    [Fact]
    public void A_Wide_Integer_Keeps_Its_Value()
    {
        var plist = new Bplist();

        Assert.Equal(1234567890123L, PaintCodeArchive.Read(plist.ToBytes(plist.Integer(1234567890123L))));
    }

    [Fact]
    public void A_Real_Is_Read_Big_Endian()
    {
        var plist = new Bplist();

        Assert.Equal(-1.5499d, PaintCodeArchive.Read(plist.ToBytes(plist.Real(-1.5499d))));
    }

    [Fact]
    public void The_Three_Singletons_Are_Null_False_And_True()
    {
        var plist = new Bplist();
        var root = plist.Array(plist.Null(), plist.Boolean(false), plist.Boolean(true));

        Assert.Equal(new object?[] { null, false, true }, (List<object?>)PaintCodeArchive.Read(plist.ToBytes(root))!);
    }

    [Fact]
    public void An_Ascii_String_And_A_Utf16_String_Read_The_Same()
    {
        var plist = new Bplist();
        var root = plist.Array(plist.Ascii("overlay-error"), plist.Unicode("overlay-error"));

        var items = (List<object?>)PaintCodeArchive.Read(plist.ToBytes(root))!;

        Assert.Equal("overlay-error", items[0]);
        Assert.Equal("overlay-error", items[1]);
    }

    [Fact]
    public void A_String_Longer_Than_Fourteen_Characters_Uses_The_Long_Form_Count()
    {
        var plist = new Bplist();
        var text = "symbol-overlay-error-with-a-long-name";

        Assert.Equal(text, PaintCodeArchive.Read(plist.ToBytes(plist.Ascii(text))));
    }

    [Fact]
    public void An_Array_Longer_Than_Fourteen_Entries_Uses_The_Long_Form_Count()
    {
        var plist = new Bplist();
        var references = new int[20];

        for (var index = 0; index < references.Length; index++)
        {
            references[index] = plist.Integer(index);
        }

        var items = (List<object?>)PaintCodeArchive.Read(plist.ToBytes(plist.Array(references)))!;

        Assert.Equal(20, items.Count);
        Assert.Equal(19L, items[19]);
    }

    [Fact]
    public void A_Dictionary_Pairs_Its_Keys_With_Its_Values()
    {
        var plist = new Bplist();
        var root = plist.Dictionary(
            (plist.Ascii("name"), plist.Ascii("Bezier")),
            (plist.Ascii("width"), plist.Real(24.83d)));

        var entries = (Dictionary<string, object?>)PaintCodeArchive.Read(plist.ToBytes(root))!;

        Assert.Equal("Bezier", entries["name"]);
        Assert.Equal(24.83d, entries["width"]);
    }

    [Fact]
    public void A_Uid_Is_Its_Own_Type_Rather_Than_An_Integer()
    {
        var plist = new Bplist();

        Assert.Equal(7, Assert.IsType<PaintCodeUid>(PaintCodeArchive.Read(plist.ToBytes(plist.Uid(7)))).Index);
    }

    [Fact]
    public void Data_Comes_Back_As_Its_Bytes()
    {
        var plist = new Bplist();
        var bytes = Encoding.ASCII.GetBytes("0.98 0.18 0.40 1");

        Assert.Equal(bytes, (byte[])PaintCodeArchive.Read(plist.ToBytes(plist.Data(bytes)))!);
    }

    [Fact]
    public void A_File_Without_The_Header_Is_Refused()
    {
        var plist = new Bplist();
        var bytes = plist.ToBytes(plist.Integer(1));
        bytes[0] = (byte)'x';

        Assert.Contains("bplist00", Assert.Throws<PaintCodeException>(() => PaintCodeArchive.Read(bytes)).Message);
    }

    [Fact]
    public void A_File_Too_Short_To_Hold_A_Trailer_Is_Refused()
        => Assert.Throws<PaintCodeException>(() => PaintCodeArchive.Read(Encoding.ASCII.GetBytes("bplist00")));

    [Fact]
    public void An_Object_That_Contains_Itself_Is_Refused_Rather_Than_Recursed()
    {
        var plist = new Bplist();
        // Object 0 is an array whose only entry references object 0.
        plist.Array(0);
        var bytes = plist.ToBytes(0);

        Assert.Contains("contains itself", Assert.Throws<PaintCodeException>(() => PaintCodeArchive.Read(bytes)).Message);
    }
}

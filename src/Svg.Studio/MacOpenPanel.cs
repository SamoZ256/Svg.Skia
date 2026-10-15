// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Svg.Studio;

/// <summary>The system's Open panel, taking files only, among which a registered package counts.</summary>
/// <remarks>
/// Avalonia 12.1.3's file picker is handed a package's URL, makes a folder of it and keeps only files,
/// so a <c>.svgstudio</c> chosen there opened nothing; its folder picker takes any folder at all.
/// Delete this once the file picker keeps what the panel returned.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static class MacOpenPanel
{
    private const string Objc = "/usr/lib/libobjc.dylib";

    private const string UniformTypeIdentifiers =
        "/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers";

    private const nint ModalResponseOk = 1;

    /// <summary>The path chosen, or null when the panel was cancelled.</summary>
    /// <remarks>
    /// App-modal rather than a sheet on the window: a sheet reports back through an Objective-C
    /// block, which costs about forty more lines of interop to build from here.
    /// </remarks>
    public static string? Pick(string title, IEnumerable<string> extensions)
    {
        NativeLibrary.Load(UniformTypeIdentifiers);

        var pool = objc_autoreleasePoolPush();

        try
        {
            var panel = Send(Class("NSOpenPanel"), "openPanel");

            SendBool(panel, "setCanChooseFiles:", true);
            SendBool(panel, "setCanChooseDirectories:", false);
            SendId(panel, "setTitle:", Text(title));
            SendId(panel, "setMessage:", Text(title));

            // By extension rather than by identifier: com.svgskia.svgstudio is unknown to a build
            // run from the repository, while the extension finds it wherever the bundle registered it.
            // Among items, not the default of data, which a package is not: it came back as a dyn. type.
            var types = Send(Class("NSMutableArray"), "array");
            var item = SendId(Class("UTType"), "typeWithIdentifier:", Text("public.item"));

            foreach (var extension in extensions)
            {
                if (objc_msgSend_id_id(Class("UTType"), sel_registerName("typeWithFilenameExtension:conformingToType:"), Text(extension), item)
                    is var type and not 0)
                {
                    SendId(types, "addObject:", type);
                }
            }

            SendId(panel, "setAllowedContentTypes:", types);

            if (Send(panel, "runModal") != ModalResponseOk)
            {
                return null;
            }

            var path = Send(Send(panel, "URL"), "path");

            return path == 0 ? null : Marshal.PtrToStringUTF8(Send(path, "UTF8String"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static nint Class(string name) => objc_getClass(name);

    /// <remarks>Every string a method takes is an NSString; a C string where one is wanted crashed the panel.</remarks>
    private static nint Text(string text) =>
        objc_msgSend(Class("NSString"), sel_registerName("stringWithUTF8String:"), text);

    private static nint Send(nint receiver, string selector) => objc_msgSend(receiver, sel_registerName(selector));

    private static nint SendId(nint receiver, string selector, nint argument) =>
        objc_msgSend_id(receiver, sel_registerName(selector), argument);

    private static void SendBool(nint receiver, string selector, bool argument) =>
        objc_msgSend_bool(receiver, sel_registerName(selector), argument);

    // One declaration per signature it is called with: on arm64 objc_msgSend is not variadic, so
    // the arguments have to be passed exactly as the method takes them.
    [DllImport(Objc)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(Objc)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(Objc)]
    private static extern nint objc_msgSend(nint receiver, nint selector);

    [DllImport(Objc)]
    private static extern nint objc_msgSend(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_id(nint receiver, nint selector, nint argument);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_id_id(nint receiver, nint selector, nint first, nint second);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool argument);
}

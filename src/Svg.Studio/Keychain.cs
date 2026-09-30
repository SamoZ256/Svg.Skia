// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Svg.Studio;

/// <summary>A secret kept by the operating system, found again by a service and an account.</summary>
/// <remarks>
/// A failure the store reports is thrown as an <see cref="InvalidOperationException"/> carrying what it
/// said; a secret that is not there is <c>null</c> from <see cref="Get"/> and nothing from
/// <see cref="Remove"/>.
/// </remarks>
public abstract class Keychain
{
    /// <summary>This machine's store, or <c>null</c> where Studio knows of none it can use.</summary>
    /// <remarks>Settable so a test holds its secrets in memory rather than in the keychain of whoever runs it.</remarks>
    public static Keychain? Current { get; set; } = ForThisMachine();

    public abstract string? Get(string service, string account);

    public abstract void Set(string service, string account, string secret);

    public abstract void Remove(string service, string account);

    private static Keychain? ForThisMachine()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychain();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsKeychain();
        }

        return OperatingSystem.IsLinux() && SecretTool.Path is not null ? new SecretTool() : null;
    }

    /// <summary>Runs a store's command-line tool, with <paramref name="input"/> as its standard input.</summary>
    private static (int Exit, string Output, string Error) Run(string tool, string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"{tool} could not be started.");

        process.StandardInput.Write(input ?? "");
        process.StandardInput.Close();

        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        return (process.ExitCode, output, error.Result);
    }

    private static InvalidOperationException Failed(string what, (int Exit, string Output, string Error) run) =>
        new($"{what} failed ({run.Exit}): {run.Error.Trim()}");

    private sealed class MacKeychain : Keychain
    {
        private const string Security = "/usr/bin/security";

        /// <summary>What <c>security</c> exits with when there is no such item.</summary>
        private const int NotFound = 44;

        public override string? Get(string service, string account)
        {
            var run = Run(Security, null, "find-generic-password", "-s", service, "-a", account, "-w");

            return run.Exit switch
            {
                0 => run.Output.TrimEnd('\n'),
                NotFound => null,
                _ => throw Failed("Reading the keychain", run)
            };
        }

        /// <remarks>
        /// <c>add-generic-password</c> takes the secret only as its <c>-w</c> argument, where any process
        /// listing would show it, so the command goes through <c>security -i</c>'s standard input instead.
        /// </remarks>
        public override void Set(string service, string account, string secret)
        {
            var run = Run(
                Security,
                $"add-generic-password -U -s {Quoted(service)} -a {Quoted(account)} -w {Quoted(secret)}\n",
                "-i");

            if (run.Exit != 0)
            {
                throw Failed("Writing to the keychain", run);
            }
        }

        public override void Remove(string service, string account)
        {
            var run = Run(Security, null, "delete-generic-password", "-s", service, "-a", account);

            if (run.Exit is not (0 or NotFound))
            {
                throw Failed("Removing from the keychain", run);
            }
        }

        private static string Quoted(string text) =>
            text.Contains('\n') || text.Contains('\r')
                ? throw new ArgumentException("A keychain entry cannot hold a line break.", nameof(text))
                : "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private sealed class SecretTool : Keychain
    {
        public static readonly string? Path =
            (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => System.IO.Path.Combine(directory, "secret-tool"))
                .FirstOrDefault(File.Exists);

        public override string? Get(string service, string account)
        {
            // Exits 1 with nothing written both for "not there" and for a locked or missing
            // collection, so only something said on standard error is treated as a failure.
            var run = Run(Path!, null, "lookup", "service", service, "account", account);

            return run.Exit == 0 ? run.Output.TrimEnd('\n')
                : string.IsNullOrWhiteSpace(run.Error) ? null
                : throw Failed("Reading the secret service", run);
        }

        public override void Set(string service, string account, string secret)
        {
            var run = Run(Path!, secret, "store", $"--label={service}", "service", service, "account", account);

            if (run.Exit != 0)
            {
                throw Failed("Writing to the secret service", run);
            }
        }

        public override void Remove(string service, string account)
        {
            var run = Run(Path!, null, "clear", "service", service, "account", account);

            if (run.Exit != 0 && !string.IsNullOrWhiteSpace(run.Error))
            {
                throw Failed("Removing from the secret service", run);
            }
        }
    }

    /// <summary>Credential Manager, as a generic credential named <c>service/account</c>.</summary>
    private sealed class WindowsKeychain : Keychain
    {
        private const int Generic = 1;
        private const int LocalMachine = 2;
        private const int NotFound = 1168;

        public override string? Get(string service, string account)
        {
            if (!CredReadW(Target(service, account), Generic, 0, out var found))
            {
                return Marshal.GetLastWin32Error() == NotFound ? null : throw Failed("Reading Credential Manager");
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(found);

                return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(found);
            }
        }

        public override void Set(string service, string account, string secret)
        {
            var blob = Marshal.StringToCoTaskMemUni(secret);

            try
            {
                var credential = new Credential
                {
                    Type = Generic,
                    TargetName = Target(service, account),
                    CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(secret),
                    CredentialBlob = blob,
                    Persist = LocalMachine,
                    UserName = account
                };

                if (!CredWriteW(ref credential, 0))
                {
                    throw Failed("Writing to Credential Manager");
                }
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUnicode(blob);
            }
        }

        public override void Remove(string service, string account)
        {
            if (!CredDeleteW(Target(service, account), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            {
                throw Failed("Removing from Credential Manager");
            }
        }

        private static string Target(string service, string account) => $"{service}/{account}";

        private static InvalidOperationException Failed(string what) =>
            new($"{what} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastWin32Error())}");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWriteW(ref Credential credential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDeleteW(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}

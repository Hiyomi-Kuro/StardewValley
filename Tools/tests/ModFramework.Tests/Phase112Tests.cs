using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// Phase 1.1.2: assembly load integrity and edge-case verification.
///
/// Five themes, in the order the phase document defines them:
///   A. the bytes the preflight approved must be the bytes that are loaded (TOCTOU);
///   B. the ContentIdentity failure branches, and that "unverifiable" really rejects sharing;
///   C. the positive host-share path, proven to hand back the original host instance;
///   D. ECMA-335 Assembly Flags and PublicKey/Token blob combinations;
///   E. the fixed-point loop: convergence, and "did not converge" means nothing is loaded.
///
/// Two properties of this test process shape the fixtures:
///   * an assembly loaded once stays loaded for the whole process, and the preflight compares every
///     claim against the host snapshot, so two scenarios must never use the same assembly identity;
///   * every scenario therefore takes its own copy of a fixture renamed in place
///     (<see cref="TestSupport.PatchAscii"/> keeps the file length and the PE layout intact), and
///     the manifest, the file name and the internal name always agree.
///
/// Everything here is offline: no device, no game installation, no network, and no malformed
/// image is ever loaded into the AppDomain.
/// </summary>
internal static class Phase112Tests
{
    private const string EntrySource = "P112Entry";
    private const string PrivateSource = "P112Priv";
    private const string SharedSource = "P112SharedLib";
    private const string ConflictSource = "P112ConflictLib";

    internal static void Run(TestRunner t, string fixtureDir)
    {
        ContentIdentityStreams(t);
        ContentIdentityCallers(t, fixtureDir);
        HostPositiveShare(t, fixtureDir);
        AssemblyFlagMatrix(t);
        PreflightFixedPoint(t, fixtureDir);
        SnapshotBinding(t, fixtureDir);
    }

    // ================================================================== B. streams

    /// <summary>
    /// B: every failure branch of <see cref="ContentIdentity.CompareFiles"/> and
    /// <see cref="ContentIdentity.TryGetLength"/>, driven by a deterministic in-memory stream.
    ///
    /// Phase 1.1.1 added the <c>OpenForTests</c> seam but never used it, so none of these branches
    /// had a test. The implementation was already correct, so these are recorded as coverage added
    /// on a passing baseline, not as Red.
    /// </summary>
    private static void ContentIdentityStreams(TestRunner t)
    {
        var previous = ContentIdentity.OpenForTests;
        var data = new byte[64];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        var other = (byte[])data.Clone();
        other[5] = 0xFF;

        try
        {
            // ---- 1/2: the opener itself fails on either side -------------------------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["right"] = () => new ScriptedStream(data, data.Length),
            });
            string reason;
            var verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_left_open_failure_is_unverifiable", verdict == ContentVerdict.Unverifiable,
                "verdict=" + verdict + " reason=" + reason);

            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_right_open_failure_is_unverifiable", verdict == ContentVerdict.Unverifiable,
                "verdict=" + verdict + " reason=" + reason);

            // ---- 3: a read fails after some bytes -----------------------------------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length, throwAt: 10),
                ["right"] = () => new ScriptedStream(data, data.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_read_failure_after_bytes_is_unverifiable", verdict == ContentVerdict.Unverifiable,
                "verdict=" + verdict + " reason=" + reason);

            // ---- 4: legal partial reads must still complete the comparison ----------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length, chunk: 3),
                ["right"] = () => new ScriptedStream(data, data.Length, chunk: 1),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_legal_partial_reads_are_same", verdict == ContentVerdict.Same,
                "verdict=" + verdict + " reason=" + reason);

            // ---- 5: early EOF inside the recorded length ----------------------------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length, stopAfter: 32),
                ["right"] = () => new ScriptedStream(data, data.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_early_eof_is_unverifiable", verdict == ContentVerdict.Unverifiable,
                "verdict=" + verdict + " reason=" + reason);

            // ---- 6: the stream length disagrees with the recorded length ------------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length - 1),
                ["right"] = () => new ScriptedStream(data, data.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_length_mismatch_is_unverifiable", verdict == ContentVerdict.Unverifiable,
                "verdict=" + verdict + " reason=" + reason);

            // ---- 7: extra bytes after the recorded length --------------------------------
            var grown = new byte[data.Length + 4];
            Array.Copy(data, grown, data.Length);
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(grown, data.Length),
                ["right"] = () => new ScriptedStream(data, data.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_extra_bytes_is_unverifiable", verdict == ContentVerdict.Unverifiable,
                "verdict=" + verdict + " reason=" + reason);

            // ---- 8/9: the two real answers ------------------------------------------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length),
                ["right"] = () => new ScriptedStream(other, other.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_different_bytes_are_different", verdict == ContentVerdict.Different,
                "verdict=" + verdict + " reason=" + reason);

            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length),
                ["right"] = () => new ScriptedStream(data, data.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_identical_bytes_are_same", verdict == ContentVerdict.Same,
                "verdict=" + verdict + " reason=" + reason);

            // ---- 10: TryGetLength failures stay path-free --------------------------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>());
            long length;
            // A fabricated absolute path: the point is that it must not survive into the reason.
            var ok = ContentIdentity.TryGetLength("Z:\\fabricated\\placeholder\\x.dll", out length, out reason);
            t.Check("p112_b_trygetlength_open_failure_is_false", !ok && length == 0,
                "ok=" + ok + " reason=" + reason);

            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["bad"] = () => new ScriptedStream(data, data.Length, lengthThrows: true,
                    lengthMessage: "cannot stat Z:\\fabricated\\placeholder\\x.dll"),
            });
            ok = ContentIdentity.TryGetLength("bad", out length, out reason);
            t.Check("p112_b_trygetlength_length_failure_is_false", !ok, "ok=" + ok + " reason=" + reason);
            t.Check("p112_b_trygetlength_reason_is_path_free",
                reason.IndexOf("Z:\\", StringComparison.Ordinal) < 0 &&
                reason.IndexOf("fabricated", StringComparison.Ordinal) < 0,
                "reason=" + reason);

            // ---- 11: a throwing Dispose must not rewrite a decided verdict ----------------
            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length, disposeThrows: true),
                ["right"] = () => new ScriptedStream(data, data.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_dispose_failure_keeps_same", verdict == ContentVerdict.Same,
                "verdict=" + verdict + " reason=" + reason);

            ContentIdentity.OpenForTests = Opener(new Dictionary<string, Func<Stream>>
            {
                ["left"] = () => new ScriptedStream(data, data.Length, disposeThrows: true),
                ["right"] = () => new ScriptedStream(other, other.Length),
            });
            verdict = ContentIdentity.CompareFiles("left", data.Length, "right", data.Length, out reason);
            t.Check("p112_b_dispose_failure_keeps_different", verdict == ContentVerdict.Different,
                "verdict=" + verdict + " reason=" + reason);
        }
        finally
        {
            ContentIdentity.OpenForTests = previous;
        }
    }

    private static Func<string, Stream> Opener(Dictionary<string, Func<Stream>> factory)
    {
        return path =>
        {
            Func<Stream> make;
            if (!factory.TryGetValue(path, out make) || make == null)
            {
                throw new FileNotFoundException("the injected opener does not serve this path");
            }
            return make();
        };
    }

    /// <summary>
    /// A stream whose every awkward behaviour is a constructor argument, so each ContentIdentity
    /// branch is exercised by exactly one documented fixture instead of a real disk race.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _data;
        private readonly long _declaredLength;
        private readonly int _chunk;
        private readonly long _throwAt;
        private readonly long _stopAfter;
        private readonly bool _lengthThrows;
        private readonly string _lengthMessage;
        private readonly bool _disposeThrows;
        private long _position;

        internal ScriptedStream(
            byte[] data,
            long declaredLength,
            int chunk = int.MaxValue,
            long throwAt = -1,
            long stopAfter = long.MaxValue,
            bool lengthThrows = false,
            string lengthMessage = "length probe failed",
            bool disposeThrows = false)
        {
            _data = data;
            _declaredLength = declaredLength;
            _chunk = chunk;
            _throwAt = throwAt;
            _stopAfter = stopAfter;
            _lengthThrows = lengthThrows;
            _lengthMessage = lengthMessage;
            _disposeThrows = disposeThrows;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _lengthThrows ? throw new IOException(_lengthMessage) : _declaredLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_throwAt >= 0 && _position >= _throwAt)
            {
                throw new IOException("read failed after " + _position + " bytes");
            }
            if (_position >= _stopAfter)
            {
                return 0;
            }
            var limit = _data.Length < _stopAfter ? _data.Length : _stopAfter;
            var available = limit - _position;
            if (available <= 0)
            {
                return 0;
            }
            var take = (int)(available < _chunk ? available : _chunk);
            if (take > count)
            {
                take = count;
            }
            Array.Copy(_data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposeThrows)
            {
                throw new IOException("dispose failed");
            }
            base.Dispose(disposing);
        }
    }

    // ============================================================ B. caller behaviour

    /// <summary>
    /// B.12: the enum value is not the guarantee - the <b>callers</b> must reject sharing when the
    /// comparison could not be completed. Both call sites are exercised: the mod-to-mod comparison
    /// (<c>MF0206</c>) and the host content verification (<c>MF0207</c>).
    /// </summary>
    private static void ContentIdentityCallers(TestRunner t, string fixtureDir)
    {
        var entry = Path.Combine(fixtureDir, "P112Entry.dll");
        var priv = Path.Combine(fixtureDir, "P112Priv.dll");
        var shared = Path.Combine(fixtureDir, "P112SharedLib.dll");
        if (!File.Exists(entry) || !File.Exists(priv) || !File.Exists(shared))
        {
            t.Check("p112_b_caller_fixtures_present", false, "missing P112Entry/P112Priv/P112SharedLib");
            return;
        }

        var entryBytes = Rename(File.ReadAllBytes(entry), EntrySource, "B112Entry");
        var privBytes = Rename(File.ReadAllBytes(priv), PrivateSource, "B112Priv");
        var previous = ContentIdentity.OpenForTests;
        try
        {
            // (1) two mods declare identical bytes, but the byte comparison cannot run.
            ContentIdentity.OpenForTests = path =>
            {
                if (path.IndexOf("p112-b-blocked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new IOException("injected open failure for this mod");
                }
                return File.OpenRead(path);
            };

            var root = Temp.NewDirectory("p112-b-caller-mod");
            var first = BuildRenamedMod(root, "p112-b-canonical", "P112.B.CallerA",
                "B112Entry.dll", entryBytes, "B112Priv.dll", privBytes);
            var second = BuildRenamedMod(root, "p112-b-blocked", "P112.B.CallerB",
                "B112Entry.dll", entryBytes, "B112Priv.dll", privBytes);
            Loader.Run(root);
            var report = TestSupport.Report(root);
            t.Check("p112_b_mod_caller_rejects_unverifiable_content",
                TestSupport.FailedWith(report, "P112.B.CallerA", "MF0206") &&
                TestSupport.FailedWith(report, "P112.B.CallerB", "MF0206"),
                TestSupport.Line(report, "id=P112.B.CallerA ") + " / " + TestSupport.Line(report, "id=P112.B.CallerB "));
            t.Check("p112_b_mod_caller_loads_nothing",
                !File.Exists(Path.Combine(first, "entry-ran.txt")) &&
                !File.Exists(Path.Combine(second, "entry-ran.txt")),
                "no entry marker may exist when sharing cannot be proven");

            // (2) the host image cannot be read: identity matches, content is unprovable.
            var hostEntry = Rename(File.ReadAllBytes(entry), EntrySource, "J112Entry");
            var hostLibBytes = Rename(File.ReadAllBytes(shared), SharedSource, "T112SharedLib");
            var hostImageDirectory = Temp.NewDirectory("p112-b-host-image");
            var hostImage = Path.Combine(hostImageDirectory, "T112SharedLib.dll");
            File.WriteAllBytes(hostImage, hostLibBytes);

            ContentIdentity.OpenForTests = path =>
            {
                if (string.Equals(path, hostImage, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("injected open failure for the host image");
                }
                return File.OpenRead(path);
            };

            var previousSnapshot = AssemblyPreflight.HostSnapshotForTests;
            try
            {
                AssemblyPreflight.HostSnapshotForTests = new List<HostAssemblyIdentity>
                {
                    new HostAssemblyIdentity
                    {
                        SimpleName = "T112SharedLib",
                        VersionText = "1.0.0.0",
                        Culture = "",
                        Location = hostImage,
                    },
                };
                var hostRoot = Temp.NewDirectory("p112-b-caller-host");
                BuildRenamedMod(hostRoot, "p112-b-host", "P112.B.HostCaller",
                    "J112Entry.dll", hostEntry, "T112SharedLib.dll", hostLibBytes);
                Loader.Run(hostRoot);
                var hostReport = TestSupport.Report(hostRoot);
                t.Check("p112_b_host_caller_rejects_unverifiable_content",
                    TestSupport.FailedWith(hostReport, "P112.B.HostCaller", "MF0207"),
                    TestSupport.Line(hostReport, "id=P112.B.HostCaller "));
                t.Check("p112_b_host_caller_not_reported_as_shared",
                    !hostReport.Contains("preflight-host-share=T112SharedLib"),
                    TestSupport.Line(hostReport, "preflight-host"));
            }
            finally
            {
                AssemblyPreflight.HostSnapshotForTests = previousSnapshot;
            }
        }
        finally
        {
            ContentIdentity.OpenForTests = previous;
        }
    }

    // ================================================================ C. host sharing

    /// <summary>
    /// C: the positive host-share path, end to end.
    ///
    /// Phase 1.1.1 only tested the refusals. These checks load a real fixture assembly into the
    /// test process and prove that the loader hands the mod <b>that instance</b>: the same object,
    /// the host's own image, and no second <c>Assembly.LoadFrom</c>.
    /// </summary>
    private static void HostPositiveShare(TestRunner t, string fixtureDir)
    {
        var fixtureDll = Path.Combine(fixtureDir, "FixtureMod.dll");
        var hostPath = Path.Combine(fixtureDir, "P112HostLib.dll");
        if (!File.Exists(fixtureDll) || !File.Exists(hostPath))
        {
            t.Check("p112_c_host_fixtures_present", false, "missing FixtureMod.dll / P112HostLib.dll");
            return;
        }

        // The host instance: loaded by the test process, exactly as the game already has its own
        // assemblies loaded when a mod declares one of them.
        var hostAssembly = Assembly.LoadFrom(hostPath);
        var hostName = hostAssembly.GetName();

        var root = Temp.NewDirectory("p112-c-positive");
        var package = TestSupport.CreatePackage(root, "p112-c-positive-mod",
            TestSupport.ManifestJson("P112.C.Positive", "FixtureMod.dll", "FixtureMod.GoodEntry",
                TestSupport.PrivateAssemblies("P112HostLib.dll")));
        TestSupport.CopyAs(package, fixtureDll, "FixtureMod.dll");
        TestSupport.CopyAs(package, hostPath, "P112HostLib.dll");

        // ---- claim level: the preflight resolves it to the loaded instance ----------------
        var loaded = ManifestReader.Load(package);
        t.Check("p112_c_manifest_readable", loaded.Ok, loaded.Ok ? "" : loaded.Code + ": " + loaded.Message);
        if (loaded.Ok && loaded.Manifest != null)
        {
            var analysis = AssemblyPreflight.Analyze(new List<ModManifest> { loaded.Manifest! });
            var claim = analysis.FindClaim("P112.C.Positive", "P112HostLib.dll");
            t.Check("p112_c_host_claim_share_from_host",
                claim != null && claim.Resolution == AssemblyResolution.ShareFromHost,
                claim == null ? "claim missing" : claim.Resolution.ToString());
            t.Check("p112_c_host_claim_same_instance",
                claim != null && ReferenceEquals(claim.HostAssembly, hostAssembly),
                claim == null ? "claim missing" : "ReferenceEquals=" +
                    ReferenceEquals(claim.HostAssembly, hostAssembly));
            t.Check("p112_c_host_claim_points_at_the_host_image",
                claim != null && string.Equals(claim.CanonicalPath, hostAssembly.Location, StringComparison.Ordinal),
                claim == null ? "claim missing" : claim.CanonicalPath);
            t.Check("p112_c_host_claim_identity_matches_loaded_instance",
                claim != null &&
                string.Equals(claim.Identity.Name, hostName!.Name, StringComparison.Ordinal) &&
                string.Equals(claim.Identity.VersionText,
                    HostAssemblyIdentity.NormalizeVersion(hostName.Version), StringComparison.Ordinal),
                claim == null ? "claim missing" : claim.IdentityText);
        }

        // ---- end to end: the mod initializes from the shared instance --------------------
        var returned = Loader.Run(root);
        var report = TestSupport.Report(root);
        t.Check("p112_c_host_share_initializes",
            returned == null && report.Contains("id=P112.C.Positive state=Initialized"),
            TestSupport.Line(report, "id=P112.C.Positive "));
        t.Check("p112_c_host_share_reported",
            report.Contains("preflight-host-share=P112HostLib"), TestSupport.Line(report, "preflight-host-share"));
        t.Check("p112_c_host_share_loads_no_second_copy",
            CountLoadedAssemblies("P112HostLib") == 1,
            "loaded copies=" + CountLoadedAssemblies("P112HostLib"));
        t.Check("p112_c_host_share_issues_no_load_from",
            report.IndexOf("file=P112HostLib.dll", StringComparison.Ordinal) < 0,
            TestSupport.Line(report, "assembly-load="));

        // ---- negative: identity and content match, but there is no instance to hand over --
        var previousSnapshot = AssemblyPreflight.HostSnapshotForTests;
        try
        {
            AssemblyPreflight.HostSnapshotForTests = new List<HostAssemblyIdentity>
            {
                new HostAssemblyIdentity
                {
                    SimpleName = "P112HostLib",
                    VersionText = HostAssemblyIdentity.NormalizeVersion(hostName.Version),
                    Culture = "",
                    Location = hostPath,
                    Assembly = null,
                },
            };
            var nullRoot = Temp.NewDirectory("p112-c-null-instance");
            var nullPackage = TestSupport.CreatePackage(nullRoot, "p112-c-null",
                TestSupport.ManifestJson("P112.C.NullInstance", "FixtureMod.dll", "FixtureMod.GoodEntry",
                    TestSupport.PrivateAssemblies("P112HostLib.dll")));
            TestSupport.CopyAs(nullPackage, fixtureDll, "FixtureMod.dll");
            TestSupport.CopyAs(nullPackage, hostPath, "P112HostLib.dll");
            Loader.Run(nullRoot);
            var nullReport = TestSupport.Report(nullRoot);
            t.Check("p112_c_host_without_instance_is_rejected",
                TestSupport.FailedWith(nullReport, "P112.C.NullInstance", "MF0207"),
                TestSupport.Line(nullReport, "id=P112.C.NullInstance "));
        }
        finally
        {
            AssemblyPreflight.HostSnapshotForTests = previousSnapshot;
        }
    }

    private static int CountLoadedAssemblies(string simpleName)
    {
        var count = 0;
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < loaded.Length; i++)
        {
            var name = loaded[i].GetName();
            if (name != null && string.Equals(name.Name, simpleName, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    // ================================================================ D. assembly flags

    /// <summary>
    /// D: ECMA-335 II.23.1.2. With <c>Assembly.Flags.PublicKey</c> (0x0001) set the blob is a full
    /// public key; with it clear the blob is an 8-byte token. Phase 1.1.1 dispatched on "is the
    /// blob non-empty", which silently accepted three malformed combinations and mis-read a token
    /// as a key whenever the flag was set.
    /// </summary>
    private static void AssemblyFlagMatrix(TestRunner t)
    {
        AssemblyFileIdentity identity;
        string error;

        // ---- well formed: unsigned ------------------------------------------------------
        var unsigned = SyntheticPe.Create();
        var ok = ManagedAssemblyReader.TryReadIdentity(unsigned.Image, out identity, out error);
        t.Check("p112_d_unsigned_is_accepted_and_not_strong_named",
            ok && !identity.IsStrongNamed && identity.PublicKey.Length == 0 && identity.PublicKeyToken.Length == 0,
            ok ? identity.Describe() : error);

        // ---- well formed: an 8-byte token without the flag -------------------------------
        var tokenBytes = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        var token = SyntheticPe.Create(s =>
        {
            s.PublicKey = tokenBytes;
            s.AssemblyFlags = 0;
        });
        ok = ManagedAssemblyReader.TryReadIdentity(token.Image, out identity, out error);
        t.Check("p112_d_eight_byte_token_is_accepted",
            ok && identity.PublicKey.Length == 0 && identity.IsStrongNamed &&
            AssemblyIdentityComparison.BytesEqual(identity.PublicKeyToken, tokenBytes),
            ok ? identity.Describe() : error);
        t.Check("p112_d_token_describe_is_consistent",
            ok && identity.Describe().IndexOf("token:1122334455667788", StringComparison.Ordinal) >= 0,
            ok ? identity.Describe() : error);

        // ---- malformed: a token blob of the wrong length --------------------------------
        var seven = SyntheticPe.Create(s => s.PublicKey = new byte[7]);
        t.Check("p112_d_seven_byte_token_is_rejected", RejectsWith(seven.Image, "token", out var e1), e1);

        var nine = SyntheticPe.Create(s => s.PublicKey = new byte[9]);
        t.Check("p112_d_nine_byte_token_is_rejected", RejectsWith(nine.Image, "token", out var e2), e2);

        // ---- malformed: the PublicKey flag with no blob ---------------------------------
        var flagWithoutBlob = SyntheticPe.Create(s => s.AssemblyFlags = 0x0001);
        t.Check("p112_d_public_key_flag_without_blob_is_rejected",
            RejectsWith(flagWithoutBlob.Image, "empty", out var e3), e3);

        // ---- malformed: the PublicKey flag over an 8-byte token -------------------------
        var flagOverToken = SyntheticPe.Create(s =>
        {
            s.PublicKey = tokenBytes;
            s.AssemblyFlags = 0x0001;
        });
        t.Check("p112_d_public_key_flag_over_a_token_is_rejected",
            RejectsWith(flagOverToken.Image, "token", out var e4), e4);

        // ---- malformed: the PublicKey flag over a blob too short for the header ---------
        var flagOverShort = SyntheticPe.Create(s =>
        {
            s.PublicKey = new byte[10];
            s.AssemblyFlags = 0x0001;
        });
        t.Check("p112_d_public_key_flag_over_a_truncated_header_is_rejected",
            RejectsWith(flagOverShort.Image, "too short", out var e5), e5);

        // ---- well formed: a real RSA public key, cross-checked against the desktop BCL ---
        var realKey = BuildRsaPublicKeyBlob();
        var realKeyImage = SyntheticPe.Create(s =>
        {
            s.PublicKey = realKey;
            s.AssemblyFlags = 0x0001;
        });
        ok = ManagedAssemblyReader.TryReadIdentity(realKeyImage.Image, out identity, out error);
        t.Check("p112_d_full_public_key_is_accepted",
            ok && identity.HasFullPublicKey && AssemblyIdentityComparison.BytesEqual(identity.PublicKey, realKey),
            ok ? identity.Describe() : error);

        var expectedToken = DesktopPublicKeyToken(realKey);
        t.Check("p112_d_full_public_key_token_matches_ecma_335",
            ok && AssemblyIdentityComparison.BytesEqual(identity.PublicKeyToken, expectedToken),
            ok ? "derived=" + AssemblyFileIdentity.ToHex(identity.PublicKeyToken) +
                 " expected=" + AssemblyFileIdentity.ToHex(expectedToken) : error);

        // Independent oracle: the desktop AssemblyName API agrees that this is a public key and
        // computes the same token from it.
        try
        {
            var name = new AssemblyName("P112CrossCheck");
            name.SetPublicKey(realKey);
            var apiToken = name.GetPublicKeyToken();
            t.Check("p112_d_full_public_key_agrees_with_assembly_name_api",
                apiToken != null && AssemblyIdentityComparison.BytesEqual(apiToken, expectedToken) &&
                (name.Flags & AssemblyNameFlags.PublicKey) != 0,
                apiToken == null ? "no token" : "api=" + AssemblyFileIdentity.ToHex(apiToken));
        }
        catch (Exception ex)
        {
            // Not a failure of the framework: the oracle is unavailable on this desktop build.
            Console.WriteLine("NOTE p112_d_assembly_name_oracle_unavailable         " + ex.GetType().Name);
        }

        // ---- representation-only: a syntactically complete blob with junk key material ---
        // The reader validates the representation, not the key material; calling these 200 zero
        // bytes "a valid RSA key" would be a claim it cannot make. This keeps the Phase 1.1.1
        // boundary test meaningful instead of turning it into a false positive.
        var zeroKey = SyntheticPe.Create(s =>
        {
            s.PublicKey = new byte[200];
            s.AssemblyFlags = 0x0001;
        });
        ok = ManagedAssemblyReader.TryReadIdentity(zeroKey.Image, out identity, out error);
        t.Check("p112_d_zero_filled_key_blob_is_accepted_as_representation_only",
            ok && identity.HasFullPublicKey, ok ? identity.Describe() : error);

        // ---- no over-rejection: flags the specification allows -------------------------
        var retargetable = SyntheticPe.Create(s => s.AssemblyFlags = 0x0100);
        t.Check("p112_d_retargetable_flag_is_accepted", Accepts(retargetable.Image, out var r1), r1);

        var contentTypes = SyntheticPe.Create(s => s.AssemblyFlags = 0x0200);
        t.Check("p112_d_windows_runtime_content_type_is_accepted", Accepts(contentTypes.Image, out var r2), r2);

        var jitFlags = SyntheticPe.Create(s => s.AssemblyFlags = 0x4000 | 0x8000);
        t.Check("p112_d_jit_flags_are_accepted", Accepts(jitFlags.Image, out var r3), r3);

        // ---- identity: an unsigned assembly and a token-signed one are not the same ------
        var unsignedIdentity = new AssemblyFileIdentity { Name = "Same" };
        var tokenIdentity = new AssemblyFileIdentity { Name = "Same" };
        tokenIdentity.PublicKeyToken = tokenBytes;
        string difference;
        t.Check("p112_d_unsigned_and_token_signed_are_different_identities",
            !AssemblyIdentityComparison.SameIdentity(unsignedIdentity, tokenIdentity, out difference),
            "difference=" + difference);
    }

    private static bool Accepts(byte[] image, out string detail)
    {
        AssemblyFileIdentity identity;
        string error;
        var ok = ManagedAssemblyReader.TryReadIdentity(image, out identity, out error);
        detail = ok ? identity.Describe() : error;
        return ok;
    }

    private static bool RejectsWith(byte[] image, string expectedFragment, out string detail)
    {
        AssemblyFileIdentity identity;
        string error;
        var ok = ManagedAssemblyReader.TryReadIdentity(image, out identity, out error);
        detail = ok ? "accepted: " + identity.Describe() : error;
        return !ok && error.IndexOf(expectedFragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Builds a genuine ECMA-335 <c>PublicKeyBlob</c> from a freshly generated RSA key:
    /// SigAlgID, HashAlgID, cbPublicKey, then the CAPI <c>PUBLICKEYBLOB</c>.
    /// </summary>
    private static byte[] BuildRsaPublicKeyBlob()
    {
        using (var rsa = RSA.Create(2048))
        {
            var parameters = rsa.ExportParameters(false);
            var modulus = parameters.Modulus!;
            var exponent = parameters.Exponent!;
            var bitLength = modulus.Length * 8;

            var capi = new List<byte>();
            capi.Add(0x06);     // PUBLICKEYBLOB
            capi.Add(0x02);     // version 2
            capi.Add(0x00);
            capi.Add(0x00);
            AddU32(capi, 0x00002400);   // CALG_RSA_SIGN
            capi.Add((byte)'R');
            capi.Add((byte)'S');
            capi.Add((byte)'A');
            capi.Add((byte)'1');
            AddU32(capi, (uint)bitLength);
            var exponentValue = 0u;
            for (var i = 0; i < exponent.Length; i++)
            {
                exponentValue = (exponentValue << 8) | exponent[i];
            }
            AddU32(capi, exponentValue);
            for (var i = modulus.Length - 1; i >= 0; i--)
            {
                capi.Add(modulus[i]);   // little-endian modulus
            }

            var blob = new List<byte>();
            AddU32(blob, 0x00002400);          // SigAlgID
            AddU32(blob, 0x00008004);          // HashAlgID (SHA-1)
            AddU32(blob, (uint)capi.Count);    // cbPublicKey
            blob.AddRange(capi);
            return blob.ToArray();
        }
    }

    /// <summary>Independent token oracle: SHA-1 over the blob, last 8 bytes, reversed.</summary>
    private static byte[] DesktopPublicKeyToken(byte[] publicKeyBlob)
    {
        var digest = SHA1.HashData(publicKeyBlob);
        var token = new byte[8];
        for (var i = 0; i < token.Length; i++)
        {
            token[i] = digest[digest.Length - 1 - i];
        }
        return token;
    }

    private static void AddU32(List<byte> sink, uint value)
    {
        sink.Add((byte)(value & 0xFF));
        sink.Add((byte)((value >> 8) & 0xFF));
        sink.Add((byte)((value >> 16) & 0xFF));
        sink.Add((byte)((value >> 24) & 0xFF));
    }

    // ============================================================== E. fixed point

    /// <summary>
    /// E: the preflight fixed point must either converge inside a proven bound or fail closed.
    ///
    /// The two sets that drive the loop (<c>conflictSet</c> and <c>dependencySet</c>) are not
    /// monotone with respect to each other: a mod rejected as a conflict can become "dependency
    /// blocked" by its former partner, which removes it from the conflict set and lets the partner
    /// come back. That is a 2-cycle, not a convergence, and the previous code broke out of the loop
    /// and classified whatever state happened to be current.
    /// </summary>
    private static void PreflightFixedPoint(TestRunner t, string fixtureDir)
    {
        var fixtureDll = Path.Combine(fixtureDir, "FixtureMod.dll");
        var conflictV1 = Path.Combine(fixtureDir, "P112ConflictLib.v1.dll");
        var conflictV2 = Path.Combine(fixtureDir, "P112ConflictLib.v2.dll");
        var shared = Path.Combine(fixtureDir, "P112SharedLib.dll");
        var priv = Path.Combine(fixtureDir, "P112Priv.dll");
        if (!File.Exists(fixtureDll) || !File.Exists(conflictV1) || !File.Exists(conflictV2) ||
            !File.Exists(shared) || !File.Exists(priv))
        {
            t.Check("p112_e_fixtures_present", false,
                "missing FixtureMod.dll / P112ConflictLib / P112SharedLib / P112Priv");
            return;
        }

        var conflictV1Bytes = File.ReadAllBytes(conflictV1);
        var conflictV2Bytes = File.ReadAllBytes(conflictV2);
        var sharedBytes = File.ReadAllBytes(shared);
        var privBytes = File.ReadAllBytes(priv);

        // ---- 1. a stable graph converges and the report says so ------------------------
        var stableRoot = Temp.NewDirectory("p112-e-stable");
        BuildFixtureMod(stableRoot, "p112-e-s1", "P112.E.One", fixtureDll,
            "K112ConflictLib.dll", Rename(conflictV1Bytes, ConflictSource, "K112ConflictLib"), "");
        BuildFixtureMod(stableRoot, "p112-e-s2", "P112.E.Two", fixtureDll,
            "L112SharedLib.dll", Rename(sharedBytes, SharedSource, "L112SharedLib"), "");
        BuildFixtureMod(stableRoot, "p112-e-s3", "P112.E.Three", fixtureDll,
            "M112Priv.dll", Rename(privBytes, PrivateSource, "M112Priv"), "");
        var stableReturned = Loader.Run(stableRoot);
        var stableReport = TestSupport.Report(stableRoot);
        t.Check("p112_e_stable_graph_converges",
            TestSupport.Line(stableReport, "preflight-convergence=").EndsWith("converged", StringComparison.Ordinal),
            TestSupport.Line(stableReport, "preflight-convergence="));
        t.Check("p112_e_stable_graph_converges_in_one_round",
            stableReport.Contains("preflight-rounds=1"),
            TestSupport.Line(stableReport, "preflight-rounds="));
        t.Check("p112_e_stable_graph_loads_every_mod",
            stableReturned == null &&
            stableReport.Contains("id=P112.E.One state=Initialized") &&
            stableReport.Contains("id=P112.E.Two state=Initialized") &&
            stableReport.Contains("id=P112.E.Three state=Initialized"),
            TestSupport.Line(stableReport, "id=P112.E.One "));
        t.Check("p112_e_stable_graph_preflight_covers_the_pool",
            stableReport.Contains("preflight=assemblies=6 conflicts=0"),
            TestSupport.Line(stableReport, "preflight=assemblies="));

        // ---- 2. the conflict/dependency cycle: not convergent, nothing is loaded ------
        var cycleRoot = Temp.NewDirectory("p112-e-cycle");
        var cyclic = BuildConflictAndDependencyPair(cycleRoot, fixtureDll, conflictV1Bytes, conflictV2Bytes,
            "N112ConflictLib");
        Loader.Run(cycleRoot);
        var cycleReport = TestSupport.Report(cycleRoot);
        t.Check("p112_e_conflict_dependency_cycle_is_reported_as_exhausted",
            TestSupport.Line(cycleReport, "preflight-convergence=").EndsWith("exhausted", StringComparison.Ordinal),
            TestSupport.Line(cycleReport, "preflight-convergence="));
        t.Check("p112_e_cycle_loads_nothing",
            !File.Exists(Path.Combine(cyclic.Item1, "entry-ran.txt")) &&
            !File.Exists(Path.Combine(cyclic.Item2, "entry-ran.txt")),
            "no entry marker may exist for an unclassified graph");
        t.Check("p112_e_cycle_reports_a_stable_reason",
            TestSupport.FailedWith(cycleReport, "P112.E.Owner", "MF0208") &&
            TestSupport.FailedWith(cycleReport, "P112.E.Dependent", "MF0208") &&
            cycleReport.Contains("preflight-exhausted-reason="),
            TestSupport.Line(cycleReport, "id=P112.E.Owner ") + " / reason=" +
            TestSupport.Line(cycleReport, "preflight-exhausted-reason="));

        // ---- 3. an injected small round bound must fail closed, not load --------------
        var previousLimit = Loader.RoundLimitOverrideForTests;
        try
        {
            Loader.RoundLimitOverrideForTests = 1;
            var boundRoot = Temp.NewDirectory("p112-e-bound");
            var bounded = BuildConflictAndDependencyPair(boundRoot, fixtureDll, conflictV1Bytes, conflictV2Bytes,
                "O112ConflictLib");
            Loader.Run(boundRoot);
            var boundReport = TestSupport.Report(boundRoot);
            t.Check("p112_e_round_bound_exhaustion_loads_nothing",
                !File.Exists(Path.Combine(bounded.Item1, "entry-ran.txt")) &&
                !File.Exists(Path.Combine(bounded.Item2, "entry-ran.txt")),
                "a mod from a non-converged preflight must never be initialized");
            t.Check("p112_e_round_bound_exhaustion_is_reported",
                TestSupport.Line(boundReport, "preflight-convergence=").EndsWith("exhausted", StringComparison.Ordinal) &&
                boundReport.Contains("preflight-round-limit=1"),
                TestSupport.Line(boundReport, "preflight-convergence=") + " / " +
                TestSupport.Line(boundReport, "preflight-round-limit="));
            t.Check("p112_e_round_bound_rejects_every_candidate",
                TestSupport.FailedWith(boundReport, "P112.E.Owner", "MF0208") &&
                TestSupport.FailedWith(boundReport, "P112.E.Dependent", "MF0208"),
                TestSupport.Line(boundReport, "id=P112.E.Owner "));
        }
        finally
        {
            Loader.RoundLimitOverrideForTests = previousLimit;
        }

        // ---- 4. a plain conflict still converges and rejects both ---------------------
        var conflictRoot = Temp.NewDirectory("p112-e-conflict");
        BuildFixtureMod(conflictRoot, "p112-e-c1", "P112.E.ConfA", fixtureDll,
            "Q112ConflictLib.dll", Rename(conflictV1Bytes, ConflictSource, "Q112ConflictLib"), "");
        BuildFixtureMod(conflictRoot, "p112-e-c2", "P112.E.ConfB", fixtureDll,
            "Q112ConflictLib.dll", Rename(conflictV2Bytes, ConflictSource, "Q112ConflictLib"), "");
        Loader.Run(conflictRoot);
        var conflictReport = TestSupport.Report(conflictRoot);
        t.Check("p112_e_direct_conflict_converges_and_rejects_both",
            TestSupport.Line(conflictReport, "preflight-convergence=").EndsWith("converged", StringComparison.Ordinal) &&
            TestSupport.FailedWith(conflictReport, "P112.E.ConfA", "MF0206") &&
            TestSupport.FailedWith(conflictReport, "P112.E.ConfB", "MF0206"),
            TestSupport.Line(conflictReport, "id=P112.E.ConfA "));

        // ---- 5. three mods, two identical one different: every owner is rejected ------
        var tripleRoot = Temp.NewDirectory("p112-e-triple");
        var tripleV1 = Rename(conflictV1Bytes, ConflictSource, "R112ConflictLib");
        var tripleV2 = Rename(conflictV2Bytes, ConflictSource, "R112ConflictLib");
        BuildFixtureMod(tripleRoot, "p112-e-t1", "P112.E.T1", fixtureDll, "R112ConflictLib.dll", tripleV1, "");
        BuildFixtureMod(tripleRoot, "p112-e-t2", "P112.E.T2", fixtureDll, "R112ConflictLib.dll", tripleV1, "");
        BuildFixtureMod(tripleRoot, "p112-e-t3", "P112.E.T3", fixtureDll, "R112ConflictLib.dll", tripleV2, "");
        Loader.Run(tripleRoot);
        var tripleReport = TestSupport.Report(tripleRoot);
        t.Check("p112_e_multi_conflict_group_rejects_every_owner",
            TestSupport.FailedWith(tripleReport, "P112.E.T1", "MF0206") &&
            TestSupport.FailedWith(tripleReport, "P112.E.T2", "MF0206") &&
            TestSupport.FailedWith(tripleReport, "P112.E.T3", "MF0206"),
            TestSupport.Line(tripleReport, "id=P112.E.T1 "));

        // ---- 6. never load before the preflight is complete --------------------------
        var completeAt = TestSupport.LineIndex(stableReport, "preflight-stage=complete");
        var firstLoadAt = TestSupport.LineIndex(stableReport, "assembly-load=");
        t.Check("p112_e_no_load_before_the_preflight_completes",
            completeAt >= 0 && firstLoadAt > completeAt,
            "preflight-stage at " + completeAt + ", first assembly-load at " + firstLoadAt);

        // ---- 7. discovery order must not change the classification -------------------
        // Both runs must be free of already-loaded copies, so the graph conflicts: nothing is
        // loaded, and the second run sees exactly the AppDomain the first one saw.
        var forwardRoot = Temp.NewDirectory("p112-e-order-fwd");
        var orderV1 = Rename(conflictV1Bytes, ConflictSource, "S112ConflictLib");
        var orderV2 = Rename(conflictV2Bytes, ConflictSource, "S112ConflictLib");
        BuildFixtureMod(forwardRoot, "p112-e-a-first", "P112.E.OrderA", fixtureDll,
            "S112ConflictLib.dll", orderV1, "");
        BuildFixtureMod(forwardRoot, "p112-e-b-second", "P112.E.OrderB", fixtureDll,
            "S112ConflictLib.dll", orderV2, "");
        Loader.Run(forwardRoot);

        var reversedRoot = Temp.NewDirectory("p112-e-order-rev");
        BuildFixtureMod(reversedRoot, "p112-e-a-second", "P112.E.OrderA", fixtureDll,
            "S112ConflictLib.dll", orderV1, "");
        BuildFixtureMod(reversedRoot, "p112-e-b-first", "P112.E.OrderB", fixtureDll,
            "S112ConflictLib.dll", orderV2, "");
        Loader.Run(reversedRoot);

        t.Check("p112_e_classification_is_independent_of_discovery_order",
            string.Equals(TestSupport.Signature(TestSupport.Report(forwardRoot)),
                TestSupport.Signature(TestSupport.Report(reversedRoot)), StringComparison.Ordinal),
            TestSupport.Signature(TestSupport.Report(forwardRoot)) + " vs " +
            TestSupport.Signature(TestSupport.Report(reversedRoot)));
    }

    /// <summary>Mod package whose entry assembly is <c>FixtureMod.dll</c> (GoodEntry).</summary>
    private static string BuildFixtureMod(
        string root,
        string directoryName,
        string id,
        string fixtureDll,
        string privateFileName,
        byte[] privateImage,
        string extraJson)
    {
        var extra = privateFileName.Length > 0 ? TestSupport.PrivateAssemblies(privateFileName) : "";
        if (extraJson.Length > 0)
        {
            extra = extra.Length > 0 ? extra + ",\n  " + extraJson : extraJson;
        }
        var package = TestSupport.CreatePackage(root, directoryName,
            TestSupport.ManifestJson(id, "FixtureMod.dll", "FixtureMod.GoodEntry", extra));
        TestSupport.CopyAs(package, fixtureDll, "FixtureMod.dll");
        if (privateFileName.Length > 0)
        {
            File.WriteAllBytes(Path.Combine(package, privateFileName), privateImage);
        }
        return package;
    }

    /// <summary>
    /// The graph that makes the old loop oscillate: the two mods ship conflicting copies of the
    /// same library <b>and</b> the dependent one declares the owner as a required dependency, so
    /// removing the conflict partner also removes the dependency reason that removed it.
    /// </summary>
    private static Tuple<string, string> BuildConflictAndDependencyPair(
        string root,
        string fixtureDll,
        byte[] conflictV1,
        byte[] conflictV2,
        string simpleName)
    {
        var owner = BuildFixtureMod(root, "p112-e-owner", "P112.E.Owner", fixtureDll,
            simpleName + ".dll", Rename(conflictV1, ConflictSource, simpleName), "");
        var dependent = BuildFixtureMod(root, "p112-e-dependent", "P112.E.Dependent", fixtureDll,
            simpleName + ".dll", Rename(conflictV2, ConflictSource, simpleName),
            TestSupport.Dependency("P112.E.Owner"));
        return Tuple.Create(owner, dependent);
    }

    // ======================================================================= A. snapshot

    /// <summary>
    /// A: the bytes the preflight approved must be the bytes that are loaded.
    ///
    /// The deterministic "attack" is a test seam invoked exactly between the preflight and the
    /// <c>Assembly.LoadFrom</c>; no timing, no real race. Every check that fails here fails because
    /// the loader executed something the preflight did not approve.
    /// </summary>
    private static void SnapshotBinding(TestRunner t, string fixtureDir)
    {
        var entry = Path.Combine(fixtureDir, "P112Entry.dll");
        var entryV2 = Path.Combine(fixtureDir, "P112Entry.v2.dll");
        var priv = Path.Combine(fixtureDir, "P112Priv.dll");
        var shared = Path.Combine(fixtureDir, "P112SharedLib.dll");
        if (!File.Exists(entry) || !File.Exists(entryV2) || !File.Exists(priv) || !File.Exists(shared))
        {
            t.Check("p112_a_fixtures_present", false, "missing P112Entry/P112Priv/P112SharedLib");
            return;
        }

        var entryBytes = File.ReadAllBytes(entry);
        var entryV2Bytes = File.ReadAllBytes(entryV2);
        var privBytes = File.ReadAllBytes(priv);
        var sharedBytes = File.ReadAllBytes(shared);

        // ---- P112-01: the claim carries a snapshot of the bytes it hashed --------------
        var snapshotRoot = Temp.NewDirectory("p112-a-snapshot");
        var snapshotPackage = BuildRenamedMod(snapshotRoot, "p112-a-snapshot-mod", "P112.A.Snapshot",
            "A112Entry.dll", Rename(entryBytes, EntrySource, "A112Entry"),
            "A112Priv.dll", Rename(privBytes, PrivateSource, "A112Priv"));
        var staging = AssemblyStagingArea.TryCreate(snapshotRoot, out var stagingError);
        t.Check("p112_a_staging_area_is_created", staging != null, stagingError);
        if (staging != null)
        {
            var manifest = ManifestReader.Load(snapshotPackage);
            var analysis = AssemblyPreflight.Analyze(new List<ModManifest> { manifest.Manifest! }, staging);
            var claim = analysis.FindClaim("P112.A.Snapshot", "A112Entry.dll");
            t.Check("p112_a_claim_has_a_snapshot",
                claim != null && claim.StagedPath.Length > 0 && File.Exists(claim.StagedPath),
                claim == null ? "claim missing" : "staged=\"" + (claim.StagedPath ?? "") + "\"");
            if (claim != null && claim.StagedPath.Length > 0 && File.Exists(claim.StagedPath))
            {
                var stagedBytes = File.ReadAllBytes(claim.StagedPath);
                t.Check("p112_a_snapshot_length_matches_the_claim",
                    stagedBytes.Length == claim.Length,
                    "staged=" + stagedBytes.Length + " claim=" + claim.Length);
                t.Check("p112_a_snapshot_is_the_exact_preflighted_bytes",
                    string.Equals("sha256:" + TestSupport.Sha256Hex(stagedBytes), claim.Digest, StringComparison.Ordinal),
                    "claim=" + claim.Digest + " staged=sha256:" + TestSupport.Sha256Hex(stagedBytes));
                t.Check("p112_a_snapshot_is_not_the_declared_path",
                    !string.Equals(claim.StagedPath, claim.Path, StringComparison.Ordinal),
                    "the snapshot must not be the declared file");
                t.Check("p112_a_snapshot_fingerprint_is_recorded",
                    claim.SnapshotFingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
                    claim.SnapshotFingerprint.StartsWith("sha1:", StringComparison.Ordinal),
                    claim.SnapshotFingerprint);
            }
            else
            {
                t.Check("p112_a_snapshot_length_matches_the_claim", false, "no snapshot");
                t.Check("p112_a_snapshot_is_the_exact_preflighted_bytes", false, "no snapshot");
                t.Check("p112_a_snapshot_is_not_the_declared_path", false, "no snapshot");
                t.Check("p112_a_snapshot_fingerprint_is_recorded", false, "no snapshot");
            }
        }
        else
        {
            t.Check("p112_a_claim_has_a_snapshot", false, "no staging area");
            t.Check("p112_a_snapshot_length_matches_the_claim", false, "no staging area");
            t.Check("p112_a_snapshot_is_the_exact_preflighted_bytes", false, "no staging area");
            t.Check("p112_a_snapshot_is_not_the_declared_path", false, "no staging area");
            t.Check("p112_a_snapshot_fingerprint_is_recorded", false, "no staging area");
        }

        var previousHook = Loader.BeforeLoadForTests;

        // ---- P112-02 (1): replace the entry with a same-length, different identity ------
        var sameLengthBytes = Rename(entryBytes, EntrySource, "Q112Entry");
        var sameLengthRoot = Temp.NewDirectory("p112-a-same-length");
        var sameLengthPackage = BuildRenamedMod(sameLengthRoot, "p112-a-same-length-mod", "P112.A.SameLength",
            "A112Entry.dll", Rename(entryBytes, EntrySource, "A112Entry"),
            "A112Priv.dll", Rename(privBytes, PrivateSource, "A112Priv"));
        try
        {
            Loader.BeforeLoadForTests = claim =>
            {
                if (string.Equals(claim.DeclaredName, "A112Entry.dll", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(Path.Combine(sameLengthPackage, "A112Entry.dll"), sameLengthBytes);
                }
            };
            Loader.Run(sameLengthRoot);
        }
        finally
        {
            Loader.BeforeLoadForTests = previousHook;
        }
        var sameLengthReport = TestSupport.Report(sameLengthRoot);
        t.Check("p112_a_replaced_entry_with_same_length_identity_is_not_loaded",
            sameLengthReport.Contains("id=P112.A.SameLength state=Initialized"),
            TestSupport.Line(sameLengthReport, "id=P112.A.SameLength "));
        t.Check("p112_a_replaced_entry_never_reaches_the_name_check",
            !sameLengthReport.Contains("id=P112.A.SameLength state=Failed code=MF0203"),
            TestSupport.Line(sameLengthReport, "id=P112.A.SameLength "));
        t.Check("p112_a_replaced_entry_runs_the_approved_entry",
            ReadMarker(sameLengthPackage).EndsWith("|A", StringComparison.Ordinal),
            MarkerLine(sameLengthPackage));

        // ---- P112-02 (2): replace the entry with different content, same identity ------
        var contentRoot = Temp.NewDirectory("p112-a-content");
        var contentPackage = BuildRenamedMod(contentRoot, "p112-a-content-mod", "P112.A.Content",
            "C112Entry.dll", Rename(entryBytes, EntrySource, "C112Entry"),
            "C112Priv.dll", Rename(privBytes, PrivateSource, "C112Priv"));
        var contentReplacement = Rename(entryV2Bytes, EntrySource, "C112Entry");
        try
        {
            Loader.BeforeLoadForTests = claim =>
            {
                if (string.Equals(claim.DeclaredName, "C112Entry.dll", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(Path.Combine(contentPackage, "C112Entry.dll"), contentReplacement);
                }
            };
            Loader.Run(contentRoot);
        }
        finally
        {
            Loader.BeforeLoadForTests = previousHook;
        }
        var contentReport = TestSupport.Report(contentRoot);
        t.Check("p112_a_replaced_entry_content_is_not_executed",
            ReadMarker(contentPackage).EndsWith("|A", StringComparison.Ordinal),
            MarkerLine(contentPackage));
        t.Check("p112_a_replaced_entry_content_still_initializes_from_the_snapshot",
            contentReport.Contains("id=P112.A.Content state=Initialized"),
            TestSupport.Line(contentReport, "id=P112.A.Content "));

        // ---- P112-02 (3): replace a private assembly with an unloadable file -----------
        var privateRoot = Temp.NewDirectory("p112-a-private");
        var privatePackage = BuildRenamedMod(privateRoot, "p112-a-private-mod", "P112.A.Private",
            "D112Entry.dll", Rename(entryBytes, EntrySource, "D112Entry"),
            "D112Priv.dll", Rename(privBytes, PrivateSource, "D112Priv"));
        try
        {
            Loader.BeforeLoadForTests = claim =>
            {
                if (string.Equals(claim.DeclaredName, "D112Priv.dll", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(Path.Combine(privatePackage, "D112Priv.dll"), new byte[8]);
                }
            };
            Loader.Run(privateRoot);
        }
        finally
        {
            Loader.BeforeLoadForTests = previousHook;
        }
        var privateReport = TestSupport.Report(privateRoot);
        t.Check("p112_a_replaced_private_assembly_is_not_loaded",
            privateReport.Contains("id=P112.A.Private state=Initialized"),
            TestSupport.Line(privateReport, "id=P112.A.Private "));

        // ---- P112-02 (4): replace the staged artifact itself ---------------------------
        var stagedRoot = Temp.NewDirectory("p112-a-staged");
        var stagedPackage = BuildRenamedMod(stagedRoot, "p112-a-staged-mod", "P112.A.Staged",
            "E112Entry.dll", Rename(entryBytes, EntrySource, "E112Entry"),
            "E112Priv.dll", Rename(privBytes, PrivateSource, "E112Priv"));
        try
        {
            Loader.BeforeLoadForTests = claim =>
            {
                if (!string.Equals(claim.DeclaredName, "E112Entry.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                var target = claim.StagedPath.Length > 0 ? claim.StagedPath : claim.CanonicalPath;
                File.WriteAllBytes(target, Rename(entryV2Bytes, EntrySource, "E112Entry"));
            };
            Loader.Run(stagedRoot);
        }
        finally
        {
            Loader.BeforeLoadForTests = previousHook;
        }
        var stagedReport = TestSupport.Report(stagedRoot);
        t.Check("p112_a_modified_snapshot_is_refused",
            TestSupport.FailedWith(stagedReport, "P112.A.Staged", "MF0202") &&
            !File.Exists(Path.Combine(stagedPackage, "entry-ran.txt")),
            TestSupport.Line(stagedReport, "id=P112.A.Staged "));
        t.Check("p112_a_modified_snapshot_reason_names_the_snapshot",
            stagedReport.Contains("snapshotted"),
            TestSupport.Line(stagedReport, "id=P112.A.Staged "));

        // ---- P112-03: a short read during the preflight must not be approved -----------
        var shortRoot = Temp.NewDirectory("p112-a-short-read");
        var shortPackage = BuildRenamedMod(shortRoot, "p112-a-short-mod", "P112.A.ShortRead",
            "F112Entry.dll", Rename(entryBytes, EntrySource, "F112Entry"),
            "F112Priv.dll", Rename(privBytes, PrivateSource, "F112Priv"));
        var previousReader = AssemblyClaimReader.OpenForTests;
        try
        {
            AssemblyClaimReader.OpenForTests = path =>
            {
                if (path.EndsWith("F112Entry.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = File.ReadAllBytes(path);
                    return new ScriptedStream(bytes, bytes.Length, stopAfter: bytes.Length - 8);
                }
                return File.OpenRead(path);
            };
            Loader.Run(shortRoot);
        }
        finally
        {
            AssemblyClaimReader.OpenForTests = previousReader;
        }
        var shortReport = TestSupport.Report(shortRoot);
        t.Check("p112_a_preflight_short_read_is_rejected",
            TestSupport.FailedWith(shortReport, "P112.A.ShortRead", "MF0205") &&
            !File.Exists(Path.Combine(shortPackage, "entry-ran.txt")),
            TestSupport.Line(shortReport, "id=P112.A.ShortRead "));

        // ---- P112-03: a file that grew during the read stays bound to what was read ----
        var growthRoot = Temp.NewDirectory("p112-a-growth");
        var growthImage = Rename(entryBytes, EntrySource, "G112Entry");
        var growthBytes = new byte[growthImage.Length + 16];
        Array.Copy(growthImage, growthBytes, growthImage.Length);
        var growthPackage = BuildRenamedMod(growthRoot, "p112-a-growth-mod", "P112.A.Growth",
            "G112Entry.dll", growthImage, "G112Priv.dll", Rename(privBytes, PrivateSource, "G112Priv"));
        try
        {
            AssemblyClaimReader.OpenForTests = path =>
            {
                if (path.EndsWith("G112Entry.dll", StringComparison.OrdinalIgnoreCase))
                {
                    // The file announces its original length but now holds more bytes.
                    return new ScriptedStream(growthBytes, entryBytes.Length);
                }
                return File.OpenRead(path);
            };
            Loader.Run(growthRoot);
        }
        finally
        {
            AssemblyClaimReader.OpenForTests = previousReader;
        }
        var growthReport = TestSupport.Report(growthRoot);
        t.Check("p112_a_growth_during_read_loads_only_the_declared_length",
            growthReport.Contains("id=P112.A.Growth state=Initialized") &&
            ReadMarker(growthPackage).EndsWith("|A", StringComparison.Ordinal),
            TestSupport.Line(growthReport, "id=P112.A.Growth ") + " marker=" + MarkerLine(growthPackage));

        // ---- P112-01: several mods share one artifact, loaded once ---------------------
        var sharedRoot = Temp.NewDirectory("p112-a-shared");
        var sharedImage = Rename(sharedBytes, SharedSource, "U112SharedLib");
        BuildRenamedMod(sharedRoot, "p112-a-share-a", "P112.A.ShareA",
            "H112Entry.dll", Rename(entryBytes, EntrySource, "H112Entry"),
            "U112SharedLib.dll", sharedImage);
        BuildRenamedMod(sharedRoot, "p112-a-share-b", "P112.A.ShareB",
            "H112Entry.dll", Rename(entryBytes, EntrySource, "H112Entry"),
            "U112SharedLib.dll", sharedImage);
        Loader.Run(sharedRoot);
        var sharedReport = TestSupport.Report(sharedRoot);
        t.Check("p112_a_shared_artifact_initializes_both_mods",
            sharedReport.Contains("id=P112.A.ShareA state=Initialized") &&
            sharedReport.Contains("id=P112.A.ShareB state=Initialized"),
            TestSupport.Line(sharedReport, "id=P112.A.ShareA "));
        t.Check("p112_a_shared_artifact_is_loaded_once",
            CountLines(sharedReport, "assembly-load=", "file=U112SharedLib.dll") == 1,
            "load lines=" + CountLines(sharedReport, "assembly-load=", "file=U112SharedLib.dll"));
        t.Check("p112_a_shared_artifact_is_reported_as_shared",
            sharedReport.Contains("preflight-shared=U112SharedLib"),
            TestSupport.Line(sharedReport, "preflight-shared="));
    }

    /// <summary>
    /// Builds a mod package whose entry is a renamed copy of a <c>ConflictLib</c> fixture: the file
    /// name, the manifest's <c>entryAssembly</c> and the internal assembly name all agree, so the
    /// package is well formed while its identity is unique to this scenario.
    /// </summary>
    private static string BuildRenamedMod(
        string root,
        string directoryName,
        string id,
        string entryFileName,
        byte[] entryImage,
        string privateFileName,
        byte[] privateImage)
    {
        var extra = privateFileName.Length > 0 ? TestSupport.PrivateAssemblies(privateFileName) : "";
        var package = TestSupport.CreatePackage(root, directoryName,
            TestSupport.ManifestJson(id, entryFileName, "ConflictLib.Entry", extra));
        File.WriteAllBytes(Path.Combine(package, entryFileName), entryImage);
        if (privateFileName.Length > 0)
        {
            File.WriteAllBytes(Path.Combine(package, privateFileName), privateImage);
        }
        return package;
    }

    /// <summary>
    /// Renames an assembly in place: same length, same PE layout, different identity. A fresh copy
    /// per scenario is what keeps the process-wide AppDomain from making one test answer another.
    /// </summary>
    private static byte[] Rename(byte[] image, string from, string to)
    {
        return TestSupport.PatchAscii((byte[])image.Clone(), from, to);
    }

    private static string ReadMarker(string package)
    {
        var path = Path.Combine(package, "entry-ran.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static string MarkerLine(string package)
    {
        var marker = ReadMarker(package);
        return marker.Length == 0 ? "(no entry marker)" : marker;
    }

    private static int CountLines(string report, string first, string second)
    {
        var count = 0;
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf(first, StringComparison.Ordinal) >= 0 &&
                lines[i].IndexOf(second, StringComparison.Ordinal) >= 0)
            {
                count++;
            }
        }
        return count;
    }
}

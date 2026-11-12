using System;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A full-part <c>genexus_edit</c> takes the new text inline, and a caller that reaches the tool through a command
    /// line cannot send a 290 KB Design System style source that way. <c>contentPath</c> names a UTF-8 file instead; its
    /// text becomes <c>content</c> before routing. These tests pin what is read, what is refused, and that a refusal
    /// always says that no write was attempted.
    /// </summary>
    public class EditContentPathTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "gxmcp-contentpath-" + Guid.NewGuid().ToString("N"));

        public EditContentPathTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string Write(string name, string text) => Write(name, new UTF8Encoding(false).GetBytes(text));

        private static JObject FullWrite(string path) => new JObject
        {
            ["name"] = "SampleDesignSystem",
            ["part"] = "Styles",
            ["mode"] = "full",
            ["contentPath"] = path
        };

        // ---- what is read -----------------------------------------------------------------------------------------

        [Fact]
        public void Resolve_PutsTheFileTextInContent_ExactlyAsStored()
        {
            string text = "styles Sample {\r\n    .A {\r\n        color: red;\r\n    }\r\n}\r\n";
            JObject args = FullWrite(Write("crlf.txt", text));

            EditContentSource.Resolve(args);

            Assert.Equal(text, (string?)args["content"]);
        }

        [Theory]
        [InlineData("no final newline")]
        [InlineData("one\ntwo\n")]
        [InlineData("one\n\n\n")]
        [InlineData("mixed\r\nendings\nhere\r")]
        public void Resolve_NormalisesNothing(string text)
        {
            JObject args = FullWrite(Write("sample.txt", text));

            EditContentSource.Resolve(args);

            Assert.Equal(text, (string?)args["content"]);
        }

        [Fact]
        public void Resolve_KeepsNonAsciiText()
        {
            string text = "/* ação, é, ñ, ✓, 日本 */\n";
            JObject args = FullWrite(Write("unicode.txt", text));

            EditContentSource.Resolve(args);

            Assert.Equal(text, (string?)args["content"]);
        }

        [Fact]
        public void Resolve_DropsAUtf8ByteOrderMarkAndNothingElse()
        {
            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("styles X { }\n")).ToArray();
            JObject args = FullWrite(Write("bom.txt", withBom));

            EditContentSource.Resolve(args);

            Assert.Equal("styles X { }\n", (string?)args["content"]);
        }

        [Fact]
        public void Resolve_AFileOverTheLimitIsRefused_AtTheLimitItIsRead()
        {
            string path = Write("limit.txt", "12345");

            var atTheLimit = FullWrite(path);
            EditContentSource.Resolve(atTheLimit, maxBytes: 5);
            Assert.Equal("12345", (string?)atTheLimit["content"]);

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(path), maxBytes: 4));
            Assert.Equal("ContentPathTooLarge", ex.Code);
            Assert.Contains("No write was attempted", ex.Message);
            Assert.Contains("import_part", ex.Message);
            Assert.Contains("is 5 bytes; the limit is 4 bytes", ex.Message);
        }

        [Fact]
        public void TheLimit_IsOneMebibyte_BecauseTheRoutedCommandCarriesTheTextTwiceInOneSharedHostFrame()
        {
            Assert.Equal(1024L * 1024, EditContentSource.MaxBytes);
        }

        [Fact]
        public void Resolve_AFileJustOverTheDefaultLimitIsRefused()
        {
            string path = Write("big.txt", new byte[EditContentSource.MaxBytes + 1].Select(_ => (byte)'a').ToArray());

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(path)));

            Assert.Equal("ContentPathTooLarge", ex.Code);
        }

        // ---- the read itself is bounded, whatever the file does after the length check ---------------------------------

        /// <summary>A stream that reports a length and then holds a different amount: a file that grew, or a short read.</summary>
        private sealed class ScriptedStream : Stream
        {
            private readonly byte[] _data;
            private readonly long _reportedLength;
            private readonly int _maxPerRead;
            private long _position;

            internal ScriptedStream(byte[] data, long reportedLength, int maxPerRead = int.MaxValue, bool seekable = true)
            {
                _data = data;
                _reportedLength = reportedLength;
                _maxPerRead = maxPerRead;
                CanSeek = seekable;
            }

            public override bool CanRead => true;
            public override bool CanSeek { get; }
            public override bool CanWrite => false;
            public override long Length => _reportedLength;
            public override long Position { get => _position; set => _position = value; }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = (int)Math.Min(Math.Min(count, _maxPerRead), _data.Length - _position);
                if (n <= 0) return 0;
                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }
        }

        [Fact]
        public void ReadBoundedStream_ReadsWhatTheLengthSays()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("styles X { }\n");

            Assert.Equal(bytes, EditContentSource.ReadBoundedStream(new ScriptedStream(bytes, bytes.Length), 1024, "x.txt"));
        }

        [Fact]
        public void ReadBoundedStream_ASlowStreamIsReadToTheEnd()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("one two three four five");

            Assert.Equal(bytes, EditContentSource.ReadBoundedStream(new ScriptedStream(bytes, bytes.Length, maxPerRead: 3), 1024, "x.txt"));
        }

        [Fact]
        public void ReadBoundedStream_AFileThatGrewAfterTheLengthCheckIsRefused_NotReadPastTheLimit()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("0123456789");

            // It said 4 bytes, within the limit of 5, and holds 10: the read must not follow it.
            var ex = Assert.Throws<UsageException>(() => EditContentSource.ReadBoundedStream(new ScriptedStream(bytes, 4), 5, "x.txt"));

            Assert.Equal("ContentPathTooLarge", ex.Code);
            Assert.Contains("grew past the limit", ex.Message);
        }

        [Fact]
        public void ReadBoundedStream_AShortReadReturnsOnlyWhatWasRead()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abc");

            // It said 8 bytes and holds 3 (the file shrank): the buffer is cut to what was read.
            Assert.Equal(bytes, EditContentSource.ReadBoundedStream(new ScriptedStream(bytes, 8), 1024, "x.txt"));
        }

        [Fact]
        public void ReadBoundedStream_ALengthOverTheLimitIsRefusedBeforeAnythingIsRead()
        {
            var ex = Assert.Throws<UsageException>(() => EditContentSource.ReadBoundedStream(new ScriptedStream(new byte[3], 1000), 999, "x.txt"));

            Assert.Equal("ContentPathTooLarge", ex.Code);
            Assert.Contains("is 1000 bytes", ex.Message);
        }

        [Fact]
        public void ReadBoundedStream_ANonSeekableStreamIsNotAFile()
        {
            // A pipe or a device reports no length, so the limit could not hold for it: it is not read at all.
            var ex = Assert.Throws<UsageException>(() => EditContentSource.ReadBoundedStream(new ScriptedStream(new byte[3], 3, seekable: false), 1024, "pipe"));

            Assert.Equal("ContentPathInvalid", ex.Code);
            Assert.Contains("not a regular file", ex.Message);
        }

        // ---- a device or a named pipe is not a file, and must not be opened to find that out ----------------------------

        [Theory]
        [InlineData(@"\\.\pipe\gxmcp-worker")]
        [InlineData(@"\\.\PIPE\gxmcp-worker")]
        [InlineData(@"\\.\COM1")]
        [InlineData(@"\\.\C:\dir\file.txt")]
        [InlineData("//./pipe/gxmcp-worker")]
        [InlineData(@"\\?\pipe\gxmcp-worker")]
        [InlineData(@"\\?\GLOBALROOT\Device\NamedPipe\gxmcp-worker")]
        [InlineData(@"\\localhost\pipe\gxmcp-worker")]
        [InlineData(@"\\server\PIPE\gxmcp-worker")]
        [InlineData(@"\\server\mailslot\box")]
        [InlineData(@"\\?\UNC\server\pipe\gxmcp-worker")]
        [InlineData("//server/pipe/gxmcp-worker")]
        [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\dir\file.txt")]
        [InlineData(@"\??\pipe\gxmcp-worker")]
        [InlineData(@"\??\PIPE\gxmcp-worker")]
        [InlineData(@"\??\UNC\server\pipe\gxmcp-worker")]
        [InlineData(@"\??\COM1")]
        [InlineData(@"\??\C:\dir\file.txt")]
        [InlineData("/??/pipe/gxmcp-worker")]
        [InlineData(@"\?\pipe\gxmcp-worker")]
        public void IsDeviceOrPipePath_RecognisesTheDeviceNamespaceAndThePipeShare(string path)
        {
            Assert.True(EditContentSource.IsDeviceOrPipePath(path));
        }

        [Theory]
        [InlineData(@"C:\dir\file.txt")]
        [InlineData(@"C:\pipe\file.txt")]
        [InlineData(@"\\?\C:\dir\file.txt")]
        [InlineData(@"\\?\c:\dir\file.txt")]
        [InlineData(@"\\?\UNC\server\share\file.txt")]
        [InlineData(@"\\server\share\file.txt")]
        [InlineData(@"\\server\share\pipe\file.txt")]
        [InlineData(@"\\server\pipes\file.txt")]
        [InlineData("//server/share/file.txt")]
        [InlineData(@"\\server")]
        public void IsDeviceOrPipePath_LeavesOrdinaryFilePathsAlone(string path)
        {
            Assert.False(EditContentSource.IsDeviceOrPipePath(path));
        }

        [Fact]
        public void Resolve_AnExtendedLengthPathToARealFileIsRead_NotMistakenForADevice()
        {
            string path = Write("extended.txt", "extended text");
            JObject args = FullWrite(@"\\?\" + path);

            EditContentSource.Resolve(args);

            Assert.Equal("extended text", (string?)args["content"]);
        }

        [Fact]
        public void Resolve_ForwardSlashesInARealPathAreRead()
        {
            string path = Write("slashes.txt", "slashed text");
            JObject args = FullWrite(path.Replace('\\', '/'));

            EditContentSource.Resolve(args);

            Assert.Equal("slashed text", (string?)args["content"]);
        }

        [Theory]
        [InlineData(@"\\.\pipe\{0}")]
        [InlineData("//./pipe/{0}")]
        [InlineData(@"\\?\pipe\{0}")]
        [InlineData(@"\??\pipe\{0}")]
        [InlineData("/??/pipe/{0}")]
        public async System.Threading.Tasks.Task Resolve_ANamedPipePathIsRefusedWithoutConnectingToIt(string spelling)
        {
            // Opening the path as a file is a client connection to whatever listens on the pipe (it could be a Worker's own
            // pipe), made before any later check could refuse it. The server below must never see one, whatever the spelling.
            string pipeName = "gxmcp-contentpath-" + Guid.NewGuid().ToString("N");
            string pipePath = string.Format(spelling, pipeName);
            var server = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            try
            {
                System.Threading.Tasks.Task connected = server.WaitForConnectionAsync();

                var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(pipePath)));
                Assert.Null(EditContentSource.TryFingerprint(pipePath));
                var viaArguments = FullWrite(pipePath);
                Assert.Null(EditContentSource.TryFingerprintOfPathArgument(viaArguments));

                Assert.Equal("ContentPathInvalid", ex.Code);
                Assert.Contains("No write was attempted", ex.Message);
                System.Threading.Tasks.Task first = await System.Threading.Tasks.Task.WhenAny(connected, System.Threading.Tasks.Task.Delay(500));
                Assert.NotSame(connected, first);
            }
            finally
            {
                server.Dispose();
            }
        }

        // ---- an empty file is not a request to clear the part ---------------------------------------------------------

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]
        public void Resolve_AnEmptyFileIsRefused_ClearingAPartIsAskedForWithContent(byte[] bytes)
        {
            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(Write("empty.txt", bytes))));

            Assert.Equal("ContentPathEmpty", ex.Code);
            Assert.Contains("content=\"\"", ex.Message);
            Assert.Contains("No write was attempted", ex.Message);
        }

        // ---- the fingerprint the idempotency check adds --------------------------------------------------------------

        [Fact]
        public void TryFingerprint_IsTheSha256OfTheFileBytes_ByteOrderMarkIncluded()
        {
            byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("styles X { }\n")).ToArray();
            string path = Write("fingerprint.txt", bytes);

            string? fingerprint = EditContentSource.TryFingerprint(path);

            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), fingerprint);
        }

        [Fact]
        public void TryFingerprint_ChangesWhenTheFileIsRewritten()
        {
            string path = Write("rewritten.txt", "first");
            string? before = EditContentSource.TryFingerprint(path);
            File.WriteAllText(path, "second");

            Assert.NotEqual(before, EditContentSource.TryFingerprint(path));
        }

        [Fact]
        public void TryFingerprint_SaysNothingAboutWhatItCannotRead()
        {
            Assert.Null(EditContentSource.TryFingerprint(null));
            Assert.Null(EditContentSource.TryFingerprint("   "));
            Assert.Null(EditContentSource.TryFingerprint("relative.txt"));
            Assert.Null(EditContentSource.TryFingerprint(Path.Combine(_dir, "absent.txt")));
            Assert.Null(EditContentSource.TryFingerprint(_dir));
            Assert.Null(EditContentSource.TryFingerprint(Write("over.txt", "12345"), maxBytes: 4));
        }

        [Fact]
        public void TryFingerprint_ARelativePathIsNotReadEvenWhenTheFileExistsUnderTheWorkingDirectory()
        {
            // Resolve refuses a relative path, so its fingerprint must not depend on where the Gateway happens to run.
            string name = "gxmcp-fingerprint-" + Guid.NewGuid().ToString("N") + ".txt";
            string inWorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), name);
            File.WriteAllText(inWorkingDirectory, "text");
            try
            {
                Assert.NotNull(EditContentSource.TryFingerprint(inWorkingDirectory));
                Assert.Null(EditContentSource.TryFingerprint(name));
            }
            finally
            {
                File.Delete(inWorkingDirectory);
            }
        }

        // ---- the fingerprint a call adds to its idempotency hash ---------------------------------------------------------

        [Fact]
        public void TryFingerprintOfPathArgument_IsTheFilesFingerprint_WhenTheTextComesFromTheFile()
        {
            string path = Write("argument.txt", "text from the file");
            string expected = EditContentSource.TryFingerprint(path)!;

            Assert.Equal(expected, EditContentSource.TryFingerprintOfPathArgument(FullWrite(path)));
        }

        [Fact]
        public void TryFingerprintOfPathArgument_AnExplicitNullContentCountsAsNone_AsItDoesInResolve()
        {
            // `content: null` is a JValue, not a missing key: it must not hide the file from the idempotency hash.
            string path = Write("null-content-fingerprint.txt", "text from the file");
            JObject args = FullWrite(path);
            args["content"] = JValue.CreateNull();

            Assert.Equal(EditContentSource.TryFingerprint(path), EditContentSource.TryFingerprintOfPathArgument(args));
            EditContentSource.Resolve(args);
            Assert.Equal("text from the file", (string?)args["content"]);
        }

        [Theory]
        [InlineData("inline")]
        [InlineData("")]
        public void TryFingerprintOfPathArgument_IsNothing_WhenTheCallCarriesItsOwnText(string inline)
        {
            JObject args = FullWrite(Write("with-inline.txt", "text from the file"));
            args["content"] = inline;

            Assert.Null(EditContentSource.TryFingerprintOfPathArgument(args));
        }

        [Fact]
        public void TryFingerprintOfPathArgument_IsNothing_ForACallWithoutAUsablePath()
        {
            Assert.Null(EditContentSource.TryFingerprintOfPathArgument(null));
            Assert.Null(EditContentSource.TryFingerprintOfPathArgument(new JObject { ["name"] = "SampleProc", ["content"] = "inline" }));
            Assert.Null(EditContentSource.TryFingerprintOfPathArgument(new JObject { ["contentPath"] = JValue.CreateNull() }));
            Assert.Null(EditContentSource.TryFingerprintOfPathArgument(new JObject { ["contentPath"] = 42 }));
            Assert.Null(EditContentSource.TryFingerprintOfPathArgument(new JObject { ["contentPath"] = Path.Combine(_dir, "absent.txt") }));
        }

        [Fact]
        public void Resolve_WithoutAContentPath_LeavesTheArgumentsAlone()
        {
            var args = new JObject { ["name"] = "SampleProc", ["part"] = "Source", ["content"] = "inline" };
            string before = args.ToString();

            EditContentSource.Resolve(args);
            EditContentSource.Resolve(null);
            var nullPath = new JObject { ["name"] = "SampleProc", ["contentPath"] = JValue.CreateNull() };
            EditContentSource.Resolve(nullPath);

            Assert.Equal(before, args.ToString());
            Assert.Null(nullPath["content"]);
        }

        // ---- what is refused ----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_AnEmptyPathIsRefused(string path)
        {
            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(path)));
            Assert.Equal("ContentPathInvalid", ex.Code);
            Assert.Contains("No write was attempted", ex.Message);
        }

        [Fact]
        public void Resolve_APathThatIsNotAStringIsRefused()
        {
            var args = FullWrite("ignored");
            args["contentPath"] = 42;

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(args));
            Assert.Equal("ContentPathInvalid", ex.Code);
        }

        [Theory]
        [InlineData("content")]
        [InlineData("")]
        public void Resolve_ContentAndContentPathTogetherAreRefused(string inline)
        {
            // An explicit empty content is a deliberate "clear the part": it must not silently lose to the file.
            JObject args = FullWrite(Write("both.txt", "from the file"));
            args["content"] = inline;

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(args));

            Assert.Equal("ContentConflict", ex.Code);
            Assert.Contains("No write was attempted", ex.Message);
            Assert.Equal(inline, (string?)args["content"]);
        }

        [Fact]
        public void Resolve_ANullContentCountsAsAbsent()
        {
            JObject args = FullWrite(Write("null-content.txt", "from the file"));
            args["content"] = JValue.CreateNull();

            EditContentSource.Resolve(args);

            Assert.Equal("from the file", (string?)args["content"]);
        }

        // Rooted is not enough: "C:styles.txt" is relative to the current directory of drive C and "\styles.txt" to the
        // current drive, so both depend on where the Gateway happens to be running.
        [Theory]
        [InlineData("styles.txt")]
        [InlineData("..\\styles.txt")]
        [InlineData("C:styles.txt")]
        [InlineData("\\styles.txt")]
        public void Resolve_APathThatIsNotFullyQualifiedIsRefused(string path)
        {
            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(path)));
            Assert.Equal("ContentPathNotAbsolute", ex.Code);
            Assert.Contains("No write was attempted", ex.Message);
        }

        // ---- a path belongs to one single-part write, not to an item of a batch ----------------------------------------

        [Theory]
        [InlineData("targets")]
        [InlineData("parts")]
        public void Resolve_AContentPathInsideABatchItemIsRefusedInsteadOfIgnored(string array)
        {
            var args = new JObject
            {
                [array] = new JArray(new JObject { ["name"] = "SampleProc", ["part"] = "Source", ["contentPath"] = Write("item.txt", "x") })
            };

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(args));

            Assert.Equal("ContentPathNotAWholePartWrite", ex.Code);
            Assert.Contains(array + "[]", ex.Message);
        }

        [Theory]
        [InlineData("changes")]
        [InlineData("targets")]
        public void Resolve_AContentPathInsideAChangeSetItemIsRefusedInsteadOfIgnored(string array)
        {
            var args = new JObject
            {
                ["changeSet"] = new JObject
                {
                    [array] = new JArray(new JObject { ["name"] = "SampleProc", ["contentPath"] = Write("item.txt", "x") })
                }
            };

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(args));

            Assert.Equal("ContentPathNotAWholePartWrite", ex.Code);
            Assert.Contains("changeSet", ex.Message);
        }

        [Fact]
        public void Resolve_ANullContentPathInsideAnItemIsNotAContentPath()
        {
            // A client that serialises every field sends `contentPath: null`: that is "no path", not a refusal.
            var args = new JObject
            {
                ["targets"] = new JArray(new JObject { ["name"] = "SampleProc", ["part"] = "Source", ["content"] = "inline", ["contentPath"] = JValue.CreateNull() }),
                ["changeSet"] = new JObject { ["changes"] = new JArray(new JObject { ["name"] = "SampleProc", ["contentPath"] = JValue.CreateNull() }) }
            };
            string before = args.ToString();

            EditContentSource.Resolve(args);

            Assert.Equal(before, args.ToString());
        }

        [Fact]
        public void Resolve_BatchItemsThatCarryTheirOwnContentAreLeftAlone()
        {
            var args = new JObject
            {
                ["targets"] = new JArray(new JObject { ["name"] = "SampleProc", ["part"] = "Source", ["content"] = "inline" })
            };
            string before = args.ToString();

            EditContentSource.Resolve(args);

            Assert.Equal(before, args.ToString());
        }

        [Fact]
        public void TheRouter_RefusesAContentPathInsideATargetsItem()
        {
            var args = new JObject
            {
                ["targets"] = new JArray(new JObject { ["name"] = "SampleProc", ["part"] = "Source", ["contentPath"] = Write("item.txt", "x") })
            };

            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_edit", args));

            Assert.Equal("ContentPathNotAWholePartWrite", ex.Code);
        }

        [Fact]
        public void Resolve_AMissingFileAndADirectoryAreRefused()
        {
            var missing = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(Path.Combine(_dir, "absent.txt"))));
            Assert.Equal("ContentPathNotFound", missing.Code);

            var directory = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(_dir)));
            Assert.Equal("ContentPathNotFound", directory.Code);
        }

        [Fact]
        public void Resolve_AFileInAFolderThatDoesNotExistIsRefusedAsMissing()
        {
            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(Path.Combine(_dir, "no-such-folder", "styles.txt"))));

            Assert.Equal("ContentPathNotFound", ex.Code);
            Assert.Contains("No write was attempted", ex.Message);
        }

        [Fact]
        public void Resolve_APathWithANulCharacterIsRefusedAsInvalid_NotLeftToThrow()
        {
            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(Path.Combine(_dir, "bad\0name.txt"))));

            Assert.Equal("ContentPathInvalid", ex.Code);
            Assert.Contains("No write was attempted", ex.Message);
        }

        [Fact]
        public void Resolve_AFileAnotherProcessHoldsOpenForWritingIsRefusedAsUnreadable()
        {
            string path = Write("locked.txt", "text");

            // Another writer holds the file: the share mode the read asks for (read only) is refused.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(path)));

                Assert.Equal("ContentPathUnreadable", ex.Code);
                Assert.Contains("No write was attempted", ex.Message);
            }
        }

        [Theory]
        [InlineData(new byte[] { 0xC3, 0x28 })]
        [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 })]
        public void Resolve_AFileThatIsNotUtf8IsRefused(byte[] bytes)
        {
            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(FullWrite(Write("not-utf8.txt", bytes))));
            Assert.Equal("ContentPathNotUtf8", ex.Code);
            Assert.Contains("No write was attempted", ex.Message);
        }

        [Theory]
        [InlineData("mode", "patch")]
        [InlineData("mode", "ops")]
        public void Resolve_APathIsNotAFragment_ModePatchAndOpsAreRefused(string key, string value)
        {
            JObject args = FullWrite(Write("whole.txt", "x"));
            args[key] = value;

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(args));

            Assert.Equal("ContentPathNotAWholePartWrite", ex.Code);
            Assert.Contains("mode=" + value, ex.Message);
        }

        [Theory]
        [InlineData("changeSet")]
        [InlineData("targets")]
        [InlineData("parts")]
        [InlineData("ops")]
        [InlineData("patch")]
        [InlineData("operation")]
        [InlineData("context")]
        public void Resolve_APathCannotFeedSeveralWritesOrAFragment(string key)
        {
            JObject args = FullWrite(Write("whole.txt", "x"));
            args.Remove("mode");
            args[key] = key == "operation" || key == "context" ? (JToken)"Replace" : new JObject { ["a"] = 1 };

            var ex = Assert.Throws<UsageException>(() => EditContentSource.Resolve(args));

            Assert.Equal("ContentPathNotAWholePartWrite", ex.Code);
            Assert.Contains("`" + key + "`", ex.Message);
        }

        // ---- through the router -------------------------------------------------------------------------------------

        [Fact]
        public void TheRouter_RoutesAContentPathWriteAsIfTheTextHadBeenInline()
        {
            string text = "styles Sample {\n    .A {\n        color: red;\n    }\n}\n";
            JObject args = FullWrite(Write("router.txt", text));
            args["dryRun"] = true;

            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args)!);

            Assert.Equal("Write", routed["module"]!.ToString());
            Assert.Equal("full", routed["mode"]!.ToString());
            Assert.Equal(text, routed["content"]!.ToString());
            Assert.Equal(text, routed["payload"]!.ToString());
            Assert.True(routed["dryRun"]!.Value<bool>());
        }

        [Fact]
        public void TheRouter_RefusesAContentPathPatch_BeforeAnythingIsRouted()
        {
            JObject args = FullWrite(Write("router.txt", "x"));
            args["mode"] = "patch";
            args["patch"] = new JObject { ["find"] = "a", ["replace"] = "b" };

            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_edit", args));

            Assert.Equal("ContentPathNotAWholePartWrite", ex.Code);
        }

        [Fact]
        public void TheRouter_AFullWriteWithNeitherContentNorPath_NamesBothInItsRefusal()
        {
            var args = new JObject { ["name"] = "SampleProc", ["part"] = "Source", ["mode"] = "full" };

            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_edit", args));

            Assert.Equal("ContentRequired", ex.Code);
            Assert.Contains("`content` or `contentPath`", ex.Message);
        }

        [Fact]
        public void EditSchema_DeclaresContentPathAsAString()
        {
            var tools = JArray.Parse(GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            JObject edit = tools.OfType<JObject>().Single(t => (string?)t["name"] == "genexus_edit");

            JToken? property = edit["inputSchema"]?["properties"]?["contentPath"];

            Assert.NotNull(property);
            Assert.Equal("string", (string?)property!["type"]);
        }

        // The gateway's argument guard refuses an undeclared key that looks like a typo of a declared one, so a
        // `contentPath` the schema does not declare would be rejected before it ever reached the router.
        [Fact]
        public void EditSchema_DeclaresContentPath()
        {
            var result = GatewayArgsValidator.Validate("genexus_edit", new JObject
            {
                ["name"] = "SampleDesignSystem",
                ["part"] = "Styles",
                ["mode"] = "full",
                ["contentPath"] = "C:\\tmp\\styles.txt"
            });

            Assert.True(
                result.Ok,
                "contentPath must pass schema pre-validation: "
                + string.Join("; ", result.Violations.Select(v => $"{v.Path} ({v.Actual})")));
        }
    }
}

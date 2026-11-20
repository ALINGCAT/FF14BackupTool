using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FF14BackupTool.Core
{
    /// <summary>
    /// Best-effort, offline character name resolver for the CN client.
    /// It reads the binary files in the character log folder. No network, no game memory.
    /// </summary>
    public static class CharacterNameResolver
    {
        private const int EntryHeaderLength = 8;
        private const int DefaultMaxEntriesPerCharacter = 20000;
        private const int MaxOffsetTableLength = 4 * 1024 * 1024;
        private const int JobChangeKind = 57;
        private static readonly int[] ChatKinds = { 14, 15, 37 };
        private const byte Delimiter = 0x1F;
        private const byte SeStringTokenStart = 0x02;
        private const byte SeStringTokenEnd = 0x03;
        private static readonly Regex JobChangeRegex = new Regex(
            "(?<name>.+?)的职业转换成了“(?:<[^>]+>)?(?<job>[^”]+)”",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Encoding Utf8 = Encoding.GetEncoding(
            65001,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);

        public static CharacterNameResult TryResolve(string characterDirectory, int maxEntriesPerCharacter = DefaultMaxEntriesPerCharacter)
        {
            if (string.IsNullOrWhiteSpace(characterDirectory)) return null;
            string logDirectory = Path.Combine(characterDirectory, "log");
            if (!Directory.Exists(logDirectory)) return null;

            CharacterNameResult chatCandidate = null;
            int scannedEntries = 0;

            foreach (string filePath in EnumerateLogFilesNewestFirst(logDirectory))
            {
                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.RandomAccess))
                    {
                        LogIndex index = ReadIndex(stream);
                        if (index == null) continue;

                        for (int entryIndex = index.EntryCount - 1; entryIndex >= 0; entryIndex--)
                        {
                            if (scannedEntries >= maxEntriesPerCharacter)
                                return chatCandidate;
                            scannedEntries++;

                            EntryHeader header = ReadEntryHeader(stream, index, entryIndex);
                            int kind = (int)(header.Meta & 0x7Fu);

                            if (kind == JobChangeKind)
                            {
                                byte[] payload = ReadPayload(stream, index, entryIndex);
                                string sender;
                                string body;
                                if (!TrySplitPayload(payload, out sender, out body)) continue;
                                string text = CleanText(body);
                                Match match = JobChangeRegex.Match(text);
                                if (!match.Success) continue;
                                string name = CleanName(match.Groups["name"].Value);
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                return new CharacterNameResult
                                {
                                    Name = name,
                                    Job = CleanName(match.Groups["job"].Value),
                                    Source = "JobChange",
                                    Timestamp = FromUnixSeconds(header.Timestamp)
                                };
                            }

                            if (chatCandidate == null && ChatKinds.Contains(kind))
                            {
                                byte[] payload = ReadPayload(stream, index, entryIndex);
                                string senderBytesText;
                                string body;
                                if (!TrySplitPayload(payload, out senderBytesText, out body)) continue;
                                string sender = CleanName(senderBytesText);
                                if (string.IsNullOrWhiteSpace(sender)) continue;
                                if (ContainsPlayerLink(payload)) continue;
                                if (sender.Length < 1 || sender.Length > 64) continue;
                                // Ignore obvious system text.
                                if (sender.IndexOf("的", StringComparison.Ordinal) >= 0 && sender.Length > 20) continue;
                                chatCandidate = new CharacterNameResult
                                {
                                    Name = sender,
                                    Job = string.Empty,
                                    Source = "ChatSender",
                                    Timestamp = FromUnixSeconds(header.Timestamp)
                                };
                            }
                        }
                    }
                }
                catch
                {
                    // A damaged or locked log must not stop the scan.
                }
            }

            return chatCandidate;
        }

        private static IEnumerable<string> EnumerateLogFilesNewestFirst(string logDirectory)
        {
            try
            {
                return Directory.EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => f.FullName)
                    .Take(200)
                    .ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static LogIndex ReadIndex(FileStream stream)
        {
            if (stream.Length < 8) return null;
            byte[] header = ReadExactly(stream, 8, 0);
            uint begin = BitConverter.ToUInt32(header, 0);
            uint end = BitConverter.ToUInt32(header, 4);
            if (end < begin) return null;
            long count = (long)end - begin;
            if (count <= 0 || count > 1000000) return null;
            long tableLength = count * 4;
            if (tableLength > MaxOffsetTableLength) return null;
            long bodyBase = 8 + tableLength;
            if (bodyBase > stream.Length) return null;

            byte[] table = ReadExactly(stream, (int)tableLength, 8);
            uint[] offsets = new uint[count + 1];
            for (int i = 0; i < count; i++)
            {
                uint offset = BitConverter.ToUInt32(table, i * 4);
                if (offset < offsets[i]) return null;
                if (bodyBase + offset > stream.Length) return null;
                offsets[i + 1] = offset;
            }
            return new LogIndex((int)count, bodyBase, offsets, stream.Length);
        }

        private static EntryHeader ReadEntryHeader(FileStream stream, LogIndex index, int entryIndex)
        {
            long start = index.BodyBase + index.Offsets[entryIndex];
            long end = index.BodyBase + index.Offsets[entryIndex + 1];
            if (end - start < EntryHeaderLength) return new EntryHeader(0, 0);
            byte[] header = ReadExactly(stream, EntryHeaderLength, start);
            return new EntryHeader(BitConverter.ToUInt32(header, 0), BitConverter.ToUInt32(header, 4));
        }

        private static byte[] ReadPayload(FileStream stream, LogIndex index, int entryIndex)
        {
            long start = index.BodyBase + index.Offsets[entryIndex];
            long end = index.BodyBase + index.Offsets[entryIndex + 1];
            long length = end - start - EntryHeaderLength;
            if (length <= 0 || length > 16 * 1024 * 1024) return new byte[0];
            return ReadExactly(stream, (int)length, start + EntryHeaderLength);
        }

        private static bool TrySplitPayload(byte[] payload, out string sender, out string body)
        {
            sender = string.Empty;
            body = string.Empty;
            if (payload == null || payload.Length == 0) return false;

            int first = Array.IndexOf(payload, Delimiter);
            if (first < 0)
            {
                body = ExtractPlainText(payload);
                return true;
            }

            int second = Array.IndexOf(payload, Delimiter, first + 1);
            if (second < 0)
            {
                body = ExtractPlainText(payload.Skip(first + 1).ToArray());
                return true;
            }

            sender = ExtractPlainText(payload.Skip(first + 1).Take(second - first - 1).ToArray());
            body = ExtractPlainText(payload.Skip(second + 1).ToArray());
            return true;
        }

        private static string ExtractPlainText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            var output = new List<byte>(bytes.Length);
            int i = 0;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b == SeStringTokenStart)
                {
                    int next = TrySkipToken(bytes, i);
                    if (next > i)
                    {
                        i = next;
                        continue;
                    }
                }
                output.Add(b);
                i++;
            }
            return CleanText(Utf8.GetString(output.ToArray()));
        }

        private static int TrySkipToken(byte[] bytes, int start)
        {
            if (start + 2 >= bytes.Length) return -1;
            int cursor = start + 1; // tag
            cursor++;
            int length;
            if (!TryReadTypedInteger(bytes, cursor, out length, out int consumed)) return -1;
            cursor += consumed;
            if (length < 0 || cursor + length > bytes.Length) return -1;
            int end = cursor + length;
            if (end < bytes.Length && bytes[end] == SeStringTokenEnd) return end + 1;
            if (end - 1 < bytes.Length && bytes[end - 1] == SeStringTokenEnd) return end;
            return -1;
        }

        private static bool TryReadTypedInteger(byte[] bytes, int offset, out int value, out int consumed)
        {
            value = 0;
            consumed = 0;
            if (offset < 0 || offset >= bytes.Length) return false;
            byte type = bytes[offset];
            if (type == 0) return false;
            if (type < 240)
            {
                value = type - 1;
                consumed = 1;
                return true;
            }
            int count;
            switch (type)
            {
                case 240:
                case 241:
                case 243:
                case 247:
                    count = 1;
                    break;
                case 242:
                case 244:
                case 245:
                case 248:
                case 249:
                case 251:
                    count = 2;
                    break;
                case 246:
                case 250:
                case 252:
                case 253:
                    count = 3;
                    break;
                case 254:
                    count = 4;
                    break;
                default:
                    return false;
            }
            if (offset + 1 + count > bytes.Length) return false;
            // The exact value is not needed for token skipping; decode little-endian-ish.
            int result = 0;
            for (int i = 0; i < count; i++)
                result = (result << 8) | bytes[offset + 1 + i];
            value = result;
            consumed = 1 + count;
            return true;
        }

        private static bool ContainsPlayerLink(byte[] payload)
        {
            if (payload == null) return false;
            for (int i = 0; i < payload.Length - 2; i++)
            {
                if (payload[i] != SeStringTokenStart || payload[i + 1] != 0x27) continue;
                // Presence of a 0x27 interactable token in the sender segment is enough to be cautious.
                return true;
            }
            return false;
        }

        private static string CleanText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '\uFFFC' || c == '\u001F') continue;
                if (char.IsControl(c)) continue;
                if (c >= '\uE000' && c <= '\uF8FF') continue;
                builder.Append(c);
            }
            return builder.ToString().Trim();
        }

        private static string CleanName(string value)
        {
            string cleaned = CleanText(value);
            if (cleaned.Length > 64) cleaned = cleaned.Substring(0, 64);
            return cleaned.Trim();
        }

        private static DateTimeOffset FromUnixSeconds(uint seconds)
        {
            return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
        }

        private static byte[] ReadExactly(FileStream stream, int length, long offset)
        {
            byte[] buffer = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(buffer, read, length - read);
                if (n <= 0) throw new EndOfStreamException("日志文件被截断。");
                read += n;
            }
            return buffer;
        }

        private sealed class LogIndex
        {
            public LogIndex(int entryCount, long bodyBase, uint[] offsets, long fileLength)
            {
                EntryCount = entryCount;
                BodyBase = bodyBase;
                Offsets = offsets;
                FileLength = fileLength;
            }

            public int EntryCount { get; private set; }
            public long BodyBase { get; private set; }
            public uint[] Offsets { get; private set; }
            public long FileLength { get; private set; }
        }

        private struct EntryHeader
        {
            public EntryHeader(uint timestamp, uint meta)
            {
                Timestamp = timestamp;
                Meta = meta;
            }

            public uint Timestamp;
            public uint Meta;
        }
    }
}

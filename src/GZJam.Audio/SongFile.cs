using ManagedBass;

namespace GZJam.Audio;

/// <summary>
/// Opens a song for decoding, for the decks, the headphone preview and analysis alike. BASS looks
/// for a file's audio only near its start (16 KB). An MP3 whose audio starts further in, behind a
/// damaged tag nothing reads as one (#220), is opened again from where its audio really starts.
/// Only files BASS refuses are searched: looking further into every file would make it likelier
/// that a FLAC or a broken file is taken for MP3 and played as noise. An MP4 (.m4a) whose boxes
/// run past its end before its index is never handed to BASS (#224): BASS spends minutes giving up
/// on one, and every other sound call waits behind it. A WMA that won't open is checked for copy
/// protection, to say so (#228).
/// </summary>
internal static class SongFile
{
    private const BassFlags Flags = BassFlags.Decode | BassFlags.Float | BassFlags.Prescan;   // Prescan: exact length and seeking for VBR MP3s

    /// <summary>How far into a file MP3 audio is looked for: past a damaged tag holding album art.</summary>
    private const int LookWithin = 8 * 1024 * 1024;

    /// <summary>MPEG frames in a row, alike, that are taken as the audio rather than chance bytes.</summary>
    private const int FramesInARow = 4;

    /// <summary>A decoding stream for <paramref name="path"/>. Throws, saying why, if it can't be played.</summary>
    public static int Open(string path)
    {
        if (RemoteSongs.IsRemote(path))
            return Open(RemoteSongs.LocalFile(path));   // a song on a server: its downloaded copy
        if (Mp4Damaged(path))
            throw AudioException.Damaged();
        int stream = Bass.CreateStream(path, 0, 0, Flags);
        if (stream != 0)
            return stream;
        Errors error = Bass.LastError;
        if (error == Errors.FileFormat && Mp3AudioStart(path) is { } start)
        {
            stream = Bass.CreateStream(path, start, 0, Flags);
            if (stream != 0)
                return stream;
        }
        if (AsfProtected(path))
            throw AudioException.CopyProtected();
        throw AudioException.CouldNotOpen(path, error);
    }

    /// <summary>The ASF (WMA) header object, and the objects in it that say the content is encrypted.</summary>
    private static readonly byte[] AsfHeader = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray();

    private static readonly byte[][] AsfEncryption =
    [
        new Guid("2211B3FB-BD23-11D2-B4B7-00AA00A9D9A9").ToByteArray(),   // content encryption (DRM 1)
        new Guid("298AE614-2622-4C17-B935-DAE07EE9289C").ToByteArray(),   // extended content encryption (DRM 7 and later)
        new Guid("43058533-6981-49E6-9B74-AD12CB86D58C").ToByteArray(),   // advanced content encryption (PlayReady)
    ];

    /// <summary>Whether <paramref name="path"/> is an ASF (WMA) file whose header says it's copy-protected.</summary>
    internal static bool AsfProtected(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> start = stackalloc byte[24];
            if (file.Read(start) < 24 || !start[..16].SequenceEqual(AsfHeader))
                return false;
            long size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(start[16..24]);
            byte[] header = new byte[(int)Math.Clamp(size - 24, 0, Math.Min(LookWithin, file.Length - 24))];
            file.ReadExactly(header);
            return AsfEncryption.Any(guid => header.AsSpan().IndexOf(guid) >= 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Box types an MP4 file starts with, to know one by its first box rather than its name.</summary>
    private static readonly HashSet<string> Mp4Boxes = ["ftyp", "moov", "mdat", "free", "skip", "wide", "pdin", "uuid"];

    /// <summary>
    /// Whether <paramref name="path"/> is an MP4 file whose top-level boxes don't add up before its
    /// index (<c>moov</c>): one that claims to run past the end of the file, or is too short to be a
    /// box. A file cut short after its index isn't damaged here: it plays what's there.
    /// </summary>
    internal static bool Mp4Damaged(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[16];
            long at = 0, length = file.Length;
            bool first = true;
            while (at + 8 <= length)
            {
                file.Position = at;
                file.ReadExactly(header[..8]);
                string type = System.Text.Encoding.ASCII.GetString(header[4..8]);
                if (first && !Mp4Boxes.Contains(type))
                    return false;   // not an MP4: BASS knows what it is
                first = false;
                if (type == "moov")
                    return false;   // the index is there; past it, BASS copes
                long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
                if (size == 0)
                    return true;    // "to the end of the file", before the index: it isn't there
                if (size == 1)
                {
                    if (at + 16 > length)
                        return true;
                    file.ReadExactly(header[8..16]);
                    size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                }
                if (size < 8 || size > length - at)
                    return true;
                at += size;
            }
            return !first;   // boxes all through, and no index
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // BASS says why
        }
    }

    /// <summary>
    /// Where MP3 audio starts in <paramref name="path"/>: past the ID3 tags readable as tags, the
    /// first of <see cref="FramesInARow"/> Layer III frames, one after another, alike. Null if none.
    /// </summary>
    internal static long? Mp3AudioStart(string path)
    {
        byte[] data;
        try
        {
            using var file = File.OpenRead(path);
            data = new byte[(int)Math.Min(file.Length, LookWithin)];
            file.ReadExactly(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        int at = 0;
        while (at + 10 <= data.Length && data[at] == 'I' && data[at + 1] == 'D' && data[at + 2] == '3')
        {
            int size = (data[at + 6] & 0x7F) << 21 | (data[at + 7] & 0x7F) << 14 | (data[at + 8] & 0x7F) << 7 | (data[at + 9] & 0x7F);
            at += 10 + size + ((data[at + 5] & 0x10) != 0 ? 10 : 0);   // a footer, if flagged
        }
        for (; at + 4 <= data.Length; at++)
        {
            if (FramesFrom(data, at))
                return at;
        }
        return null;
    }

    private static bool FramesFrom(byte[] data, int at)
    {
        int kind = -1;
        for (int i = 0; i < FramesInARow; i++)
        {
            if (FrameLength(data, at) is not var (length, thisKind) || (kind >= 0 && thisKind != kind))
                return false;
            kind = thisKind;
            at += length;
        }
        return true;
    }

    private static readonly int[] Mpeg1Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Kbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] Mpeg1Hz = [44100, 48000, 32000];

    /// <summary>
    /// The length of the Layer III frame whose header is at <paramref name="at"/>, and its kind
    /// (MPEG version and sample rate, the same all through a song), or null if there's none there.
    /// </summary>
    private static (int Length, int Kind)? FrameLength(byte[] data, int at)
    {
        if (at < 0 || at + 4 > data.Length || data[at] != 0xFF || (data[at + 1] & 0xE0) != 0xE0)
            return null;
        int version = (data[at + 1] >> 3) & 3;   // 3: MPEG 1, 2: MPEG 2, 0: MPEG 2.5, 1: reserved
        int layer = (data[at + 1] >> 1) & 3;     // 1: Layer III
        int bitrate = data[at + 2] >> 4, rate = (data[at + 2] >> 2) & 3, padding = (data[at + 2] >> 1) & 1;
        if (version == 1 || layer != 1 || bitrate is 0 or 15 || rate == 3)
            return null;
        bool mpeg1 = version == 3;
        int hz = Mpeg1Hz[rate] >> (mpeg1 ? 0 : version == 2 ? 1 : 2);
        int kbps = (mpeg1 ? Mpeg1Kbps : Mpeg2Kbps)[bitrate];
        return ((mpeg1 ? 144_000 : 72_000) * kbps / hz + padding, version << 2 | rate);
    }
}

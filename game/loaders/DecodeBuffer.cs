using System;

namespace XonoticGodot.Game.Loaders;

/// <summary>
/// Per-thread reusable RGBA8 decode scratch shared by the image decoders (<see cref="TgaDecoder"/> /
/// <see cref="DdsDecoder"/>). Each decoder used to allocate a fresh <c>new byte[w*h*4]</c> per texture — a
/// 2048² surface is 16 MB, and a material/player texture SET decodes many same-size images back-to-back, so
/// these were the 50-160 MB worker-side decode bursts driving gen0/1/2 GC (PERFORMANCE_REPORT §12.6b;
/// confirmed in the 2026-06-14 release profile, where GC pause tripled as bot models streamed in).
///
/// <para>Why a hand-rolled per-thread buffer rather than <see cref="System.Buffers.ArrayPool{T}"/>:
/// <c>Image.CreateFromData</c> COPIES the buffer synchronously into the Godot Image (so it's free to reuse on
/// the next decode) but requires the array length to match the surface EXACTLY — an ArrayPool rental is only
/// guaranteed to be ≥ the requested size, so its oversized tail would be read as extra pixels. We therefore
/// keep ONE exactly-sized buffer per thread, reused when the next texture is the same byte length (the common
/// case within a material set) and cleared so a reused buffer is byte-identical to a fresh <c>new byte[]</c>.</para>
///
/// <para>Threading: the decoders run on the BackgroundAssetStreamer's worker(s) (and occasionally the main
/// thread), one image at a time per thread, so a <c>[ThreadStatic]</c> buffer is race-free and a single shared
/// buffer serves both decoders. The only cost is one retained buffer per decoding thread (bounded by the
/// largest texture), which is exactly the pooling trade the §12.6b note called for.</para>
/// </summary>
internal static class RgbaDecodeBuffer
{
    [ThreadStatic] private static byte[]? _buffer;

    /// <summary>
    /// A zeroed RGBA8 buffer of EXACTLY <paramref name="byteLength"/> bytes, reused across same-size decodes on
    /// the calling thread. Valid only until the next <see cref="Rent"/> on the same thread — copy it out (e.g.
    /// via <c>Image.CreateFromData</c>, which copies) before the next call.
    /// </summary>
    public static byte[] Rent(int byteLength)
    {
        byte[]? b = _buffer;
        if (b is null || b.Length != byteLength)
        {
            // First use of this size on this thread (or a size change) — allocate and retain. CreateFromData's
            // exact-length contract means we can't keep an oversized buffer, so a size change discards the old one.
            b = new byte[byteLength];
            _buffer = b;
        }
        else
        {
            // Same size as last time → reuse, but zero it first so the result matches `new byte[]` even if a
            // decoder leaves any byte unwritten (e.g. alpha for an alpha-less source).
            Array.Clear(b, 0, byteLength);
        }
        return b;
    }
}

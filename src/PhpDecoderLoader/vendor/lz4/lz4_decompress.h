/*
 * Minimal LZ4 block decompressor — declaration only.
 *
 * Implements the LZ4 raw-block format (no frame header), compatible with
 * K4os.Compression.LZ4's LZ4Codec.Encode output and with liblz4's
 * LZ4_decompress_safe.
 *
 * License: BSD 2-Clause (same as the reference LZ4 implementation).
 */
#ifndef MM_LZ4_DECOMPRESS_H
#define MM_LZ4_DECOMPRESS_H

#ifdef __cplusplus
extern "C" {
#endif

/*
 * LZ4_decompress_safe — decompress a raw LZ4 block.
 *
 * src           : compressed data
 * dst           : output buffer
 * compressedSize: number of bytes in src
 * dstCapacity   : size of dst buffer (must be >= original uncompressed size)
 *
 * Returns the number of bytes written to dst on success, or a negative value
 * on error (corrupted data, output overflow, or invalid input).
 */
int LZ4_decompress_safe(const char *src, char *dst,
                        int compressedSize, int dstCapacity);

#ifdef __cplusplus
}
#endif
#endif /* MM_LZ4_DECOMPRESS_H */
